using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;

namespace RFIDLocal.Services
{
    // Minimal HTTP/1.1 endpoint on IPv4 loopback. TcpListener avoids Http.sys URL ACLs,
    // so normal operation does not need an administrator or firewall rule.
    public sealed class LocalIngestReceiver : IDisposable
    {
        private const string Route = "/functions/v1/ingest";
        private const string EventListRoute = "/functions/v1/events";
        private const string PairReaderRoute = "/functions/v1/readers/pair";
        private const int MaxPayloadBytes = 32768;
        private const int MaxHeaderBytes = 16384;
        private readonly LocalRaceDatabase database;
        private readonly LocalWorkspace workspace;
        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly SemaphoreSlim requestSlots = new SemaphoreSlim(32, 32);
        private volatile bool stopping;

        public int Port { get; private set; }
        public string Endpoint { get { return "http://127.0.0.1:" + Port + Route; } }
        public bool IsRunning { get; private set; }

        public LocalIngestReceiver(LocalRaceDatabase database, int port)
            : this(null, database, port)
        {
        }

        public LocalIngestReceiver(LocalWorkspace workspace, LocalRaceDatabase database, int port)
        {
            if (database == null) throw new ArgumentNullException("database");
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port", "Use un puerto entre 1024 y 65535.");
            this.database = database;
            this.workspace = workspace;
            Port = port;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(64);
            IsRunning = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "RFID local ingest receiver" };
            acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (!requestSlots.Wait(0))
                    {
                        using (client) Write(client.GetStream(), 503, Json("unavailable", "receiver_busy", "Receptor ocupado; reintente con el mismo reading_id."));
                        continue;
                    }
                    if (!ThreadPool.QueueUserWorkItem(HandleClient, client))
                    {
                        requestSlots.Release();
                        client.Close();
                    }
                }
                catch (SocketException) { if (!stopping) Thread.Sleep(100); }
                catch (ObjectDisposedException) { break; }
            }
        }

        private void HandleClient(object state)
        {
            using (TcpClient client = (TcpClient)state)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    NetworkStream stream = client.GetStream();
                    string headerText = ReadHeaders(stream);
                    string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] requestLine = lines[0].Split(' ');
                    if (requestLine.Length < 2) { Write(stream, 400, Json("invalid", "invalid_http", "Solicitud HTTP inválida.")); return; }
                    string method = requestLine[0].ToUpperInvariant();
                    Uri uri;
                    if (!Uri.TryCreate("http://127.0.0.1" + requestLine[1], UriKind.Absolute, out uri) ||
                        (uri.AbsolutePath.TrimEnd('/') != Route && uri.AbsolutePath.TrimEnd('/') != EventListRoute &&
                         uri.AbsolutePath.TrimEnd('/') != PairReaderRoute))
                    { Write(stream, 404, Json("not_found", "route_not_found", "Ruta local no encontrada.")); return; }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int index = 1; index < lines.Length; index++)
                    {
                        int colon = lines[index].IndexOf(':');
                        if (colon <= 0) continue;
                        headers[lines[index].Substring(0, colon).Trim()] = lines[index].Substring(colon + 1).Trim();
                    }
                    string credential = Header(headers, "X-Reader-Credential");
                    string deviceId = Query(uri, "device_id");
                    string path = uri.AbsolutePath.TrimEnd('/');
                    if (method == "GET" && path == EventListRoute)
                    {
                        Write(stream, 200, BuildEventListResponse());
                        return;
                    }
                    if (method == "GET")
                    {
                        if (path != Route)
                        { Write(stream, 405, Json("invalid", "method_not_allowed", "Use POST para vincular un lector.")); return; }
                        bool disposeDatabase;
                        string eventId = Query(uri, "event_id");
                        LocalRaceDatabase eventDatabase = ResolveEventDatabase(eventId, out disposeDatabase);
                        if (eventDatabase == null)
                        { Write(stream, 404, Json("not_found", "event_not_found", "El evento local ya no está disponible.")); return; }
                        try
                        {
                            if (!eventDatabase.AuthenticateDevice(deviceId, credential))
                            { Write(stream, 401, Json("invalid_device", "unauthorized", "Credencial de lector inválida.")); return; }
                            Write(stream, 200, new JavaScriptSerializer().Serialize(eventDatabase.GetReadiness(eventId, deviceId)));
                        }
                        finally { if (disposeDatabase) eventDatabase.Dispose(); }
                        return;
                    }
                    if (method != "POST")
                    { Write(stream, 405, Json("invalid", "method_not_allowed", "Use GET para preflight o POST para lecturas.")); return; }
                    if (!String.Equals((Header(headers, "Content-Type") ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    { Write(stream, 400, Json("invalid", "content_type", "El receptor espera application/json.")); return; }
                    if (!String.IsNullOrEmpty(Header(headers, "Transfer-Encoding")))
                    { Write(stream, 400, Json("invalid", "transfer_encoding", "El receptor requiere Content-Length.")); return; }
                    int contentLength;
                    if (!Int32.TryParse(Header(headers, "Content-Length"), out contentLength) || contentLength < 0)
                    { Write(stream, 411, Json("invalid", "content_length_required", "Falta Content-Length.")); return; }
                    if (contentLength > MaxPayloadBytes)
                    { Write(stream, 413, Json("invalid", "payload_too_large", "El payload supera el límite local.")); return; }
                    byte[] body = ReadExactly(stream, contentLength);
                    string json = Encoding.UTF8.GetString(body);
                    IDictionary<string, object> payload;
                    try { payload = new JavaScriptSerializer { MaxJsonLength = MaxPayloadBytes }.Deserialize<Dictionary<string, object>>(json); }
                    catch (Exception) { Write(stream, 400, Json("invalid", "invalid_json", "El JSON no es válido.")); return; }
                    if (payload == null) { Write(stream, 400, Json("invalid", "invalid_json", "El JSON debe ser un objeto.")); return; }
                    object payloadDevice;
                    string payloadDeviceId = payload.TryGetValue("device_id", out payloadDevice) ? Convert.ToString(payloadDevice) : null;
                    object payloadEvent;
                    string payloadEventId = payload.TryGetValue("event_id", out payloadEvent) ? Convert.ToString(payloadEvent) : null;
                    if (path == PairReaderRoute)
                    {
                        if (String.IsNullOrWhiteSpace(payloadEventId))
                        { Write(stream, 400, Json("invalid", "event_id_required", "Falta event_id para vincular el lector.")); return; }
                        bool disposePairDatabase;
                        LocalRaceDatabase pairDatabase = ResolveEventDatabase(payloadEventId, out disposePairDatabase);
                        if (pairDatabase == null)
                        { Write(stream, 404, Json("not_found", "event_not_found", "El evento local ya no está disponible.")); return; }
                        try
                        {
                            LocalDevice paired = pairDatabase.AddReaderCredential();
                            Write(stream, 200, new JavaScriptSerializer().Serialize(new
                            {
                                event_id = payloadEventId,
                                device_id = paired.DeviceId,
                                credential = paired.Credential,
                                ingest_endpoint = Endpoint
                            }));
                        }
                        catch (InvalidOperationException exception)
                        { Write(stream, 409, Json("conflict", "reader_pairing_not_allowed", exception.Message)); }
                        finally { if (disposePairDatabase) pairDatabase.Dispose(); }
                        return;
                    }
                    if (path != Route)
                    { Write(stream, 405, Json("invalid", "method_not_allowed", "Use GET para listar eventos.")); return; }
                    if (String.IsNullOrWhiteSpace(credential))
                    { Write(stream, 401, Json("invalid_device", "unauthorized", "Falta X-Reader-Credential.")); return; }
                    bool disposeTarget;
                    LocalRaceDatabase targetDatabase = ResolveEventDatabase(payloadEventId, out disposeTarget);
                    if (targetDatabase == null)
                    { Write(stream, 404, Json("not_found", "event_not_found", "El evento local ya no está disponible.")); return; }
                    try
                    {
                        if (!targetDatabase.AuthenticateDevice(payloadDeviceId, credential))
                        { Write(stream, 401, Json("invalid_device", "unauthorized", "Credencial inválida para este lector.")); return; }
                        LocalIngestResult result = targetDatabase.Ingest(json, payload);
                        Write(stream, result.HttpStatus, result.ResponseJson);
                    }
                    finally { if (disposeTarget) targetDatabase.Dispose(); }
                }
                catch (Exception exception)
                {
                    try { Write(client.GetStream(), 500, Json("unavailable", "receiver_error", "El receptor no pudo confirmar la escritura: " + exception.Message)); }
                    catch { }
                }
                finally { requestSlots.Release(); }
            }
        }

        private string BuildEventListResponse()
        {
            var events = new List<object>();
            IList<LocalEvent> available = workspace == null
                ? new List<LocalEvent> { database.GetSummary() }
                : workspace.ListEvents();
            foreach (LocalEvent race in available)
            {
                bool disposeDatabase;
                LocalRaceDatabase eventDatabase = ResolveEventDatabase(race.EventId, out disposeDatabase);
                string activeDeviceId = null;
                if (eventDatabase != null)
                {
                    try { activeDeviceId = eventDatabase.GetActiveDeviceId(); }
                    finally { if (disposeDatabase) eventDatabase.Dispose(); }
                }
                events.Add(new
                {
                    id = race.EventId,
                    name = race.Name,
                    status = race.Status,
                    event_date = race.EventDate.ToString("yyyy-MM-dd"),
                    device_id = activeDeviceId
                });
            }
            return new JavaScriptSerializer().Serialize(new { events = events.ToArray(), ingest_endpoint = Endpoint });
        }

        private LocalRaceDatabase ResolveEventDatabase(string eventId, out bool disposeDatabase)
        {
            disposeDatabase = false;
            if (String.IsNullOrWhiteSpace(eventId)) return null;
            if (workspace == null)
            {
                return String.Equals(database.GetSummary().EventId, eventId, StringComparison.OrdinalIgnoreCase)
                    ? database : null;
            }
            if (String.Equals(database.GetSummary().EventId, eventId, StringComparison.OrdinalIgnoreCase))
                return database;
            Guid parsed;
            if (!Guid.TryParse(eventId, out parsed)) return null;
            disposeDatabase = true;
            try { return workspace.Open(parsed.ToString("D")); }
            catch (FileNotFoundException) { disposeDatabase = false; return null; }
        }

        private static string ReadHeaders(NetworkStream stream)
        {
            var bytes = new List<byte>();
            int matched = 0;
            while (bytes.Count < MaxHeaderBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("Conexión cerrada antes de completar encabezados HTTP.");
                byte current = (byte)value;
                bytes.Add(current);
                if ((matched == 0 && current == 13) || (matched == 1 && current == 10) ||
                    (matched == 2 && current == 13) || (matched == 3 && current == 10)) matched++;
                else matched = current == 13 ? 1 : 0;
                if (matched == 4) return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new InvalidDataException("Los encabezados HTTP superan el límite local.");
        }

        private static byte[] ReadExactly(NetworkStream stream, int length)
        {
            byte[] bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(bytes, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException("El cuerpo HTTP llegó incompleto.");
                offset += read;
            }
            return bytes;
        }

        private static string Query(Uri uri, string key)
        {
            string query = uri.Query.TrimStart('?');
            foreach (string pair in query.Split('&'))
            {
                int equals = pair.IndexOf('=');
                string name = Uri.UnescapeDataString(equals < 0 ? pair : pair.Substring(0, equals)).Replace('+', ' ');
                if (!String.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(equals < 0 ? "" : pair.Substring(equals + 1)).Replace('+', ' ');
            }
            return null;
        }

        private static string Header(IDictionary<string, string> headers, string name)
        {
            string value; return headers.TryGetValue(name, out value) ? value : null;
        }

        private static string Json(string status, string reason, string message)
        {
            return new JavaScriptSerializer().Serialize(new { status = status, reason = reason, message = message });
        }

        private static void Write(NetworkStream stream, int statusCode, string body)
        {
            string reason = statusCode == 200 ? "OK" : statusCode == 400 ? "Bad Request" :
                statusCode == 401 ? "Unauthorized" : statusCode == 404 ? "Not Found" :
                statusCode == 405 ? "Method Not Allowed" : statusCode == 409 ? "Conflict" :
                statusCode == 411 ? "Length Required" : statusCode == 413 ? "Payload Too Large" :
                statusCode == 503 ? "Service Unavailable" : "Internal Server Error";
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\nContent-Length: " + bytes.Length +
                "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            stopping = true;
            IsRunning = false;
            try { listener.Stop(); } catch { }
            if (acceptThread != null && Thread.CurrentThread != acceptThread) acceptThread.Join(2000);
        }
    }
}
