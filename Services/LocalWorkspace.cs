using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;

namespace RFIDLocal.Services
{
    public sealed class LocalWorkspace
    {
        public const int DefaultReceiverPort = 5328;
        private readonly string root;

        public LocalWorkspace(string dataRoot)
        {
            root = Path.GetFullPath(dataRoot);
            Directory.CreateDirectory(root);
        }

        public static string DefaultDataRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RFIDLocalRace"); }
        }

        public string DataRoot { get { return root; } }

        public IList<LocalEvent> ListEvents()
        {
            var events = new List<LocalEvent>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                string dbPath = Path.Combine(directory, "race.sqlite3");
                if (!File.Exists(dbPath)) continue;
                using (var db = new LocalRaceDatabase(dbPath)) events.Add(db.GetSummary());
            }
            events.Sort((left, right) => right.EventDate.CompareTo(left.EventDate));
            return events;
        }

        public IList<LocalEvent> ListArchivedEvents()
        {
            var events = new List<LocalEvent>();
            string archiveRoot = ArchivedEventsDirectory;
            if (!Directory.Exists(archiveRoot)) return events;
            foreach (string directory in Directory.GetDirectories(archiveRoot))
            {
                Guid eventGuid;
                if (!Guid.TryParse(Path.GetFileName(directory), out eventGuid)) continue;
                string dbPath = Path.Combine(directory, "race.sqlite3");
                if (!File.Exists(dbPath)) continue;
                using (var db = new LocalRaceDatabase(dbPath))
                {
                    LocalEvent race = db.GetSummary();
                    if (String.Equals(race.EventId, eventGuid.ToString("D"), StringComparison.OrdinalIgnoreCase)) events.Add(race);
                }
            }
            events.Sort((left, right) => right.EventDate.CompareTo(left.EventDate));
            return events;
        }

        public LocalEvent ArchiveEvent(string eventId)
        {
            string normalizedId = NormalizeEventId(eventId);
            string sourceDirectory = Path.Combine(root, normalizedId);
            string sourceDatabase = Path.Combine(sourceDirectory, "race.sqlite3");
            if (!File.Exists(sourceDatabase)) throw new FileNotFoundException("No se encontró el evento que se quiere archivar.", sourceDatabase);
            LocalEvent race;
            using (var database = new LocalRaceDatabase(sourceDatabase)) race = database.GetSummary();
            if (!String.Equals(race.EventId, normalizedId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La identidad interna del evento no coincide con la carpeta local.");
            if (race.Status == "open") throw new InvalidOperationException("Cierra la recepción del evento antes de archivarlo.");

            string archiveRoot = ArchivedEventsDirectory;
            Directory.CreateDirectory(archiveRoot);
            string archivedDirectory = Path.Combine(archiveRoot, normalizedId);
            if (Directory.Exists(archivedDirectory)) throw new IOException("Este evento ya existe en el archivo.");
            Directory.Move(sourceDirectory, archivedDirectory);
            try
            {
                using (var archived = new LocalRaceDatabase(Path.Combine(archivedDirectory, "race.sqlite3")))
                    return archived.GetSummary();
            }
            catch
            {
                Directory.Move(archivedDirectory, sourceDirectory);
                throw;
            }
        }

        public LocalEvent RestoreArchivedEvent(string eventId)
        {
            string normalizedId = NormalizeEventId(eventId);
            string archivedDirectory = Path.Combine(ArchivedEventsDirectory, normalizedId);
            string activeDirectory = Path.Combine(root, normalizedId);
            if (!File.Exists(Path.Combine(archivedDirectory, "race.sqlite3")))
                throw new FileNotFoundException("No se encontró el evento archivado.", archivedDirectory);
            if (Directory.Exists(activeDirectory)) throw new IOException("Ya existe un evento activo con esta identidad. No se mezclaron los datos.");
            Directory.Move(archivedDirectory, activeDirectory);
            try
            {
                using (var restored = new LocalRaceDatabase(Path.Combine(activeDirectory, "race.sqlite3")))
                {
                    LocalEvent race = restored.GetSummary();
                    if (!String.Equals(race.EventId, normalizedId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("La identidad interna del evento no coincide con su carpeta archivada.");
                    return race;
                }
            }
            catch
            {
                Directory.CreateDirectory(ArchivedEventsDirectory);
                Directory.Move(activeDirectory, archivedDirectory);
                throw;
            }
        }

        public LocalEvent CreateEvent(string name, DateTime eventDate, string timeZone,
            IEnumerable<string> distances, IDictionary<string, bool> exclusionPolicies, out LocalDevice device)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("El evento requiere un nombre.", "name");
            ResolveTimeZone(timeZone);
            string eventId = Guid.NewGuid().ToString("D");
            string deviceId = Guid.NewGuid().ToString("D");
            string credential = NewCredential();
            string directory = Path.Combine(root, eventId);
            Directory.CreateDirectory(directory);
            string dbPath = Path.Combine(directory, "race.sqlite3");
            try
            {
                LocalRaceDatabase.InitializeNewDatabase(dbPath);
                using (var db = new LocalRaceDatabase(dbPath))
                {
                    db.CreateEvent(eventId, name.Trim(), eventDate, timeZone, distances, exclusionPolicies, deviceId, HashCredential(credential));
                    LocalEvent created = db.GetSummary();
                    device = new LocalDevice { DeviceId = deviceId, Credential = credential, MinimumRssi = -127 };
                    return created;
                }
            }
            catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
        }

        public LocalEvent ImportPreparation(string preparationPath)
        {
            if (String.IsNullOrWhiteSpace(preparationPath) || !File.Exists(preparationPath))
                throw new FileNotFoundException("Selecciona el paquete de preparación remoto descargado desde la administración web.", preparationPath);
            FileInfo file = new FileInfo(preparationPath);
            if (file.Length > D07TransferPackage.MaximumBytes) throw new InvalidDataException("El paquete supera 32 MB.");
            string json = File.ReadAllText(preparationPath, Encoding.UTF8);
            return ImportPreparationJson(json);
        }

        public LocalEvent ImportPreparationJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > D07TransferPackage.MaximumBytes)
                throw new InvalidDataException("La preparación está vacía o supera 32 MB.");
            Dictionary<string, object> package = D07TransferPackage.Parse(json);
            D07TransferPackage.ValidatePreparation(package);
            Dictionary<string, object> eventData = D07TransferPackage.Object(D07TransferPackage.Get(package, "event"), "event");
            string eventId = D07TransferPackage.Text(eventData, "id");
            Guid parsed;
            if (!Guid.TryParse(eventId, out parsed)) throw new InvalidDataException("La identidad del evento remoto no es válida.");
            string directory = Path.Combine(root, parsed.ToString("D"));
            if (Directory.Exists(directory) || Directory.Exists(Path.Combine(ArchivedEventsDirectory, parsed.ToString("D"))))
                throw new IOException("Ya existe una copia local o archivada de este evento. Actualizar una preparación existente requiere revisión y conciliación D07; no se sobrescribió.");
            Directory.CreateDirectory(directory);
            string dbPath = Path.Combine(directory, "race.sqlite3");
            try
            {
                LocalRaceDatabase.InitializeNewDatabase(dbPath);
                using (var database = new LocalRaceDatabase(dbPath)) database.ImportPreparationPackage(package);
                using (var database = new LocalRaceDatabase(dbPath)) return database.GetSummary();
            }
            catch
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                throw;
            }
        }

        public LocalRaceDatabase Open(LocalEvent race)
        {
            if (race == null) throw new ArgumentNullException("race");
            if (String.IsNullOrWhiteSpace(race.DatabasePath) || !File.Exists(race.DatabasePath))
                throw new FileNotFoundException("No se encontró la base local del evento. No se creó una base vacía.", race.DatabasePath);
            return new LocalRaceDatabase(race.DatabasePath);
        }

        public LocalRaceDatabase Open(string eventId)
        {
            Guid parsed;
            if (!Guid.TryParse(eventId, out parsed)) throw new ArgumentException("Identidad de evento no válida.", "eventId");
            string path = Path.Combine(root, parsed.ToString("D"), "race.sqlite3");
            if (!File.Exists(path)) throw new FileNotFoundException("No se encontró la base local del evento.", path);
            return new LocalRaceDatabase(path);
        }

        internal string ImportRestoredDatabase(string restoredPath, string originalEventId)
        {
            Guid parsed;
            if (!Guid.TryParse(originalEventId, out parsed)) throw new InvalidDataException("El respaldo no contiene una identidad de evento válida.");
            string directory = Path.Combine(root, parsed.ToString("D"));
            if (Directory.Exists(directory) || Directory.Exists(Path.Combine(ArchivedEventsDirectory, parsed.ToString("D"))))
                throw new IOException("Ya existe este evento local o archivado. No se mezclaron sus datos.");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "race.sqlite3");
            try
            {
                using (var staged = new LocalRaceDatabase(restoredPath)) staged.MarkRestoredForReview();
                using (var staged = new LocalRaceDatabase(restoredPath)) staged.BackupTo(destination);
                using (var installed = new LocalRaceDatabase(destination))
                {
                    LocalEvent restored = installed.GetSummary();
                    if (restored.HasAuthority || restored.Status != "closed")
                        throw new InvalidDataException("La copia restaurada no quedó aislada para consulta.");
                }
            }
            catch
            {
                if (File.Exists(destination)) File.Delete(destination);
                Directory.Delete(directory, true);
                throw;
            }
            return destination;
        }

        internal static string HashCredential(string credential)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(credential ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public static TimeZoneInfo ResolveTimeZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException)
            {
                if (String.Equals(id, "America/Santiago", StringComparison.OrdinalIgnoreCase))
                    return TimeZoneInfo.FindSystemTimeZoneById("Pacific SA Standard Time");
                throw;
            }
        }

        private string ArchivedEventsDirectory { get { return Path.Combine(root, "Archived"); } }

        private static string NormalizeEventId(string eventId)
        {
            Guid parsed;
            if (!Guid.TryParse(eventId, out parsed)) throw new ArgumentException("Identidad de evento no válida.", "eventId");
            return parsed.ToString("D");
        }

        private static string NewCredential()
        {
            byte[] secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            return Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
