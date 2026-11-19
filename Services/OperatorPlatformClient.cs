using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;

namespace RFIDLocal.Services
{
    public sealed class OperatorPlatformClient
    {
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = D07TransferPackage.MaximumBytes };
        private readonly OperatorSessionStore store;

        public OperatorPlatformClient(OperatorSessionStore store) { this.store = store; }

        public OperatorSession Login(string platformUrl, string email, string password)
        {
            string url = NormalizePlatformUrl(platformUrl);
            var data = new Dictionary<string, object> { { "email", email.Trim() }, { "password", password } };
            OperatorSession session = ReadSession(PostJson(url + "/api/operator/session", serializer.Serialize(data)), url, email.Trim());
            store.Save(session);
            return session;
        }

        public OperatorSession EnsureFresh(OperatorSession session)
        {
            if (session == null) throw new InvalidOperationException("Inicia sesión como operador para usar la plataforma.");
            string url = NormalizePlatformUrl(session.PlatformUrl);
            if (!String.IsNullOrWhiteSpace(session.AccessToken) && session.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120)
                return session;
            if (String.IsNullOrWhiteSpace(session.RefreshToken)) throw new InvalidOperationException("La sesión del operador venció. Inicia sesión nuevamente.");
            var data = new Dictionary<string, object> { { "refresh_token", session.RefreshToken } };
            OperatorSession updated = ReadSession(PostJson(url + "/api/operator/session", serializer.Serialize(data)), url, session.OperatorEmail);
            store.Save(updated);
            return updated;
        }

        public IList<PlatformEvent> ListAvailableEvents(OperatorSession session)
        {
            session = EnsureFresh(session);
            using (var request = new HttpRequestMessage(HttpMethod.Get, NormalizePlatformUrl(session.PlatformUrl) + "/api/reader/events"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
                using (HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult())
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    EnsureSuccess(response, body);
                    var root = serializer.Deserialize<Dictionary<string, object>>(body);
                    var list = root != null && root.ContainsKey("events") ? root["events"] as ArrayList : null;
                    var events = new List<PlatformEvent>();
                    if (list == null) return events;
                    foreach (object item in list)
                    {
                        var map = item as Dictionary<string, object>;
                        if (map == null) continue;
                        string status = Text(map, "status");
                        if (status == "closed" || status == "cancelled") continue;
                        events.Add(new PlatformEvent { Id = Text(map, "id"), Name = Text(map, "name"),
                            Status = status, EventDate = Text(map, "event_date") ?? Text(map, "start_at") ?? "Fecha por definir" });
                    }
                    return events;
                }
            }
        }

        public string DownloadPreparation(OperatorSession session, string eventId)
        {
            session = EnsureFresh(session);
            string url = NormalizePlatformUrl(session.PlatformUrl) + "/admin/events/" + Uri.EscapeDataString(eventId) + "/preparation";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
                using (HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult())
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    EnsureSuccess(response, body);
                    return body;
                }
            }
        }

        public string Publish(OperatorSession session, string packageJson, string targetEventId, bool final)
        {
            session = EnsureFresh(session);
            string url = NormalizePlatformUrl(session.PlatformUrl) + "/api/admin/event-transfers";
            var preview = TransferRequest(session, url, packageJson, "preview", targetEventId, final);
            if (preview == null) throw new InvalidOperationException("La plataforma no devolvió una vista previa de la sincronización.");
            Dictionary<string, object> committed = TransferRequest(session, url, packageJson, "commit", targetEventId, final);
            Dictionary<string, object> receipt = committed == null ? null : committed["receipt"] as Dictionary<string, object>;
            if (receipt == null) throw new InvalidOperationException("La sincronización terminó sin un recibo verificable.");
            return serializer.Serialize(receipt);
        }

        private Dictionary<string, object> TransferRequest(OperatorSession session, string url, string packageJson,
            string mode, string targetEventId, bool final)
        {
            var uri = new UriBuilder(url);
            uri.Query = "mode=" + Uri.EscapeDataString(mode) + "&target_event_id=" + Uri.EscapeDataString(targetEventId ?? "") +
                "&final=" + (final ? "true" : "false");
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri.Uri))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
                request.Content = new StringContent(packageJson, Encoding.UTF8, "application/vnd.yr-event-transfer+json");
                using (HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult())
                {
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    EnsureSuccess(response, text);
                    var root = serializer.Deserialize<Dictionary<string, object>>(text);
                    return root != null && root.ContainsKey("result") ? root["result"] as Dictionary<string, object> : null;
                }
            }
        }

        private string PostJson(string url, string body)
        {
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = http.PostAsync(url, content).GetAwaiter().GetResult())
            {
                string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                EnsureSuccess(response, text);
                return text;
            }
        }

        private OperatorSession ReadSession(string response, string platformUrl, string email)
        {
            var map = serializer.Deserialize<Dictionary<string, object>>(response);
            string accessToken = Text(map, "access_token"), refreshToken = Text(map, "refresh_token");
            if (String.IsNullOrWhiteSpace(accessToken) || String.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("La plataforma no devolvió una sesión renovable.");
            long expiresAt;
            if (!Int64.TryParse(Convert.ToString(D07TransferPackage.Get(map, "expires_at"), CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresAt))
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(50).ToUnixTimeSeconds();
            return new OperatorSession { PlatformUrl = platformUrl, OperatorEmail = email, AccessToken = accessToken,
                RefreshToken = refreshToken, ExpiresAt = expiresAt };
        }

        private static string NormalizePlatformUrl(string value)
        {
            Uri uri;
            string input = (value ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(input, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                uri.AbsolutePath != "/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Ingresa la URL base de Next.js con HTTPS (HTTP solo para localhost).", "value");
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string Text(IDictionary<string, object> map, string key)
        {
            object value; return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (response.IsSuccessStatusCode) return;
            string message = body;
            try { message = Text(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body), "error") ?? message; }
            catch { }
            throw new InvalidOperationException("La plataforma respondió HTTP " + (int)response.StatusCode + ": " + (message ?? "sin detalle"));
        }
    }
}
