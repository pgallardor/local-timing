using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RFIDLocal.Services
{
    internal static class D07TransferPackage
    {
        public const string Format = "yr-event-transfer";
        public const int Version = 1;
        public const int MaximumParticipants = 5000;
        public const int MaximumBytes = 32 * 1024 * 1024;

        public static Dictionary<string, object> Parse(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("El paquete D07 está vacío o supera 32 MB.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumBytes };
            Dictionary<string, object> package = serializer.Deserialize<Dictionary<string, object>>(json);
            if (package == null || Text(package, "format") != Format || Integer(package, "version") != Version)
                throw new InvalidDataException("El formato o la versión del paquete D07 no es compatible.");
            string supplied = Text(package, "sha256");
            if (String.IsNullOrWhiteSpace(supplied) || !String.Equals(supplied, Hash(package), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La huella SHA-256 del paquete D07 no coincide.");
            RejectSecrets(package);
            return package;
        }

        public static string Seal(Dictionary<string, object> package)
        {
            package.Remove("sha256");
            package["sha256"] = Hash(package);
            string json = Canonical(package);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("El paquete supera 32 MB. Exporta una revisión con menos historial por archivo.");
            return json;
        }

        public static string Hash(IDictionary<string, object> package)
        {
            var unsigned = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in package)
                if (!String.Equals(pair.Key, "sha256", StringComparison.Ordinal)) unsigned[pair.Key] = pair.Value;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(unsigned))))
                    .Replace("-", "").ToLowerInvariant();
        }

        public static string Canonical(object value)
        {
            var output = new StringBuilder();
            WriteCanonical(output, value);
            return output.ToString();
        }

        public static Dictionary<string, object> Object(object value, string label)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) throw new InvalidDataException("El objeto " + label + " no es válido.");
            return map;
        }

        public static IList<object> Array(object value, string label)
        {
            var sequence = value as IEnumerable;
            if (sequence == null || value is string) throw new InvalidDataException("La lista " + label + " no es válida.");
            return sequence.Cast<object>().ToList();
        }

        public static string Text(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public static long Integer(IDictionary<string, object> map, string key)
        {
            object value;
            long result;
            return map != null && map.TryGetValue(key, out value) && value != null &&
                Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        public static string NormalizeSex(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string normalized = value.Normalize(NormalizationForm.FormD);
            var compact = new StringBuilder();
            var words = new List<string>();
            var word = new StringBuilder();
            foreach (char character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (Char.IsLetterOrDigit(character))
                {
                    char lower = Char.ToLowerInvariant(character);
                    compact.Append(lower);
                    word.Append(lower);
                }
                else if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }
            }
            if (word.Length > 0) words.Add(word.ToString());
            string key = compact.ToString();
            string[] female = { "f", "fem", "femina", "femenino", "femenina", "mujer", "mujeres", "dama", "damas", "female", "woman", "women", "transfemenina", "mujertrans", "senora", "nina" };
            string[] male = { "m", "masc", "masculino", "masculina", "h", "hombre", "hombres", "varon", "varones", "male", "man", "men", "transmasculino", "hombretrans", "senor", "nino" };
            string[] other = { "otro", "otra", "x", "nobinario", "nobinaria", "nonbinary", "nonbinario", "nonbinaria", "intersexual", "intersex", "agenero", "generofluido", "genderfluid" };
            string[] noInfo = { "nd", "na", "sindato", "sininformacion", "noinformado", "noespecificado", "noespecificada", "prefieronodecir", "prefieronoresponder", "nosabe", "unknown", "unspecified" };
            if (female.Contains(key)) return "F";
            if (male.Contains(key)) return "M";
            if (other.Contains(key)) return "otro";
            if (noInfo.Contains(key)) return "no_informado";
            bool hasFemale = words.Any(female.Contains);
            bool hasMale = words.Any(male.Contains);
            if (hasFemale != hasMale) return hasFemale ? "F" : "M";
            if (words.Any(other.Contains)) return "otro";
            return "no_informado";
        }

        public static bool Boolean(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        public static void ValidatePreparation(Dictionary<string, object> package)
        {
            if (Text(package, "kind") != "preparation") throw new InvalidDataException("El archivo no es una preparación de evento.");
            Dictionary<string, object> eventData = Object(Get(package, "event"), "event");
            Dictionary<string, object> manifest = Object(Get(package, "manifest"), "manifest");
            Guid eventId, operationId;
            if (!Guid.TryParse(Text(eventData, "id"), out eventId) ||
                !Guid.TryParse(Text(manifest, "operation_id"), out operationId) ||
                String.IsNullOrWhiteSpace(Text(eventData, "name")) ||
                String.IsNullOrWhiteSpace(Text(eventData, "timezone")))
                throw new InvalidDataException("La preparación no tiene identidad, operación o definición de evento válida.");
            if (Array(Get(package, "participants"), "participants").Count == 0 ||
                Array(Get(package, "participants"), "participants").Count > MaximumParticipants ||
                Array(Get(package, "distances"), "distances").Count == 0)
                throw new InvalidDataException("La preparación requiere distancias y entre 1 y 5000 participantes.");
            foreach (object value in Array(Get(package, "distances"), "distances"))
            {
                Dictionary<string, object> distance = Object(value, "distance");
                if (!Boolean(distance, "award_policy_configured"))
                    throw new InvalidDataException("La preparación necesita una política de podio configurada por cada distancia.");
            }
            Dictionary<string, object> policy = Object(Get(package, "policy"), "policy");
            if (Text(policy, "preset_id") != "compat_actual_v1" || Text(policy, "revision") != "1")
                throw new InvalidDataException("La versión de política del paquete todavía no es compatible con RFID Local Race.");
        }

        public static void ValidatePublication(Dictionary<string, object> package)
        {
            if (Text(package, "kind") != "publication") throw new InvalidDataException("El archivo no es una publicación D07.");
            Dictionary<string, object> eventData = Object(Get(package, "event"), "event");
            Dictionary<string, object> manifest = Object(Get(package, "manifest"), "manifest");
            Guid eventId, transferId;
            if (!Guid.TryParse(Text(eventData, "id"), out eventId) ||
                !Guid.TryParse(Text(manifest, "transfer_id"), out transferId) ||
                Integer(manifest, "revision") < 1 || String.IsNullOrWhiteSpace(Text(manifest, "source_revision")))
                throw new InvalidDataException("La publicación no tiene identidad de evento, revisión o transferencia válida.");
            DateTime eventDate;
            if (!DateTime.TryParseExact(Text(eventData, "event_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out eventDate) ||
                String.IsNullOrWhiteSpace(Text(eventData, "name")) || String.IsNullOrWhiteSpace(Text(eventData, "timezone")) ||
                !new[] { "draft", "open", "closed" }.Contains(Text(eventData, "status")))
                throw new InvalidDataException("La publicación no contiene fecha o definición válida del evento.");
            string operationIdText = Text(manifest, "operation_id");
            if (!String.IsNullOrWhiteSpace(operationIdText))
            {
                Guid operationId;
                if (!Guid.TryParse(operationIdText, out operationId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(Text(manifest, "base_package_hash") ?? "", "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("La publicación vinculada no identifica la operación y huella de preparación remota.");
            }
            IList<object> participants = Array(Get(package, "participants"), "participants");
            if (participants.Count == 0 || participants.Count > MaximumParticipants)
                throw new InvalidDataException("La publicación requiere entre 1 y 5000 participantes.");
            Array(Get(package, "distances"), "distances");
            Array(Get(package, "marks"), "marks");
            Array(Get(package, "operations"), "operations");
            Array(Get(package, "receipts"), "receipts");
            if (Array(Get(package, "expected_results"), "expected_results").Count != participants.Count)
                throw new InvalidDataException("La publicación necesita un resultado calculado por participante.");
            Dictionary<string, object> policy = Object(Get(package, "policy"), "policy");
            if (Text(policy, "preset_id") != "compat_actual_v1" || Text(policy, "revision") != "1")
                throw new InvalidDataException("La versión de política del paquete todavía no es compatible con la web.");
        }

        public static object Get(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        private static void RejectSecrets(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map != null)
            {
                foreach (KeyValuePair<string, object> pair in map)
                {
                    string key = pair.Key.ToLowerInvariant();
                    if (key.Contains("credential") || key.Contains("service_role") || key == "epc" ||
                        key == "rut" || key.Contains("source_file") || key.Contains("source_payload"))
                        throw new InvalidDataException("El paquete contiene un campo privado no permitido: " + pair.Key + ".");
                    RejectSecrets(pair.Value);
                }
                return;
            }
            var sequence = value as IEnumerable;
            if (sequence == null || value is string || value is byte[]) return;
            foreach (object item in sequence) RejectSecrets(item);
        }

        private static void WriteCanonical(StringBuilder output, object value)
        {
            if (value == null || value == DBNull.Value) { output.Append("null"); return; }
            if (value is string || value is char)
            {
                WriteString(output, Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
            if (value is IDictionary<string, object>)
            {
                IDictionary<string, object> map = (IDictionary<string, object>)value;
                output.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(output, pair.Key);
                    output.Append(':');
                    WriteCanonical(output, pair.Value);
                }
                output.Append('}');
                return;
            }
            var sequence = value as IEnumerable;
            if (sequence != null && !(value is byte[]))
            {
                output.Append('[');
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteCanonical(output, item);
                }
                output.Append(']');
                return;
            }
            if (value is byte[] || value is DateTime || value is DateTimeOffset || value is Guid)
            {
                WriteString(output, Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is float || value is double || value is decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidDataException("El paquete contiene un número no válido.");
                output.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char character in value ?? "")
            {
                switch (character)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(character);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
