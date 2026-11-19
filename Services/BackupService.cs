using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;
using RFIDLocal.Infrastructure;

namespace RFIDLocal.Services
{
    public sealed class BackupService
    {
        private sealed class BackupManifest
        {
            public int format_version { get; set; }
            public string event_id { get; set; }
            public string database_file { get; set; }
            public string sha256 { get; set; }
            public string created_at { get; set; }
        }

        public string Create(LocalRaceDatabase database, LocalEvent race, string destination)
        {
            if (database == null || race == null) throw new ArgumentNullException();
            if (String.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Seleccione dónde guardar el respaldo.");
            string fullDestination = Path.GetFullPath(destination);
            string directory = Path.GetDirectoryName(fullDestination);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string tempDb = Path.Combine(Path.GetTempPath(), "rfid-local-backup-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            string tempArchive = fullDestination + ".tmp";
            try
            {
                database.Checkpoint();
                database.BackupTo(tempDb);
                string hash = HashFile(tempDb);
                var manifest = new BackupManifest
                {
                    format_version = 1,
                    event_id = race.EventId,
                    database_file = "race.sqlite3",
                    sha256 = hash,
                    created_at = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                };
                byte[] manifestBytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(manifest));
                if (File.Exists(tempArchive)) File.Delete(tempArchive);
                using (FileStream file = new FileStream(tempArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (Stream stream = manifestEntry.Open()) stream.Write(manifestBytes, 0, manifestBytes.Length);
                    ZipArchiveEntry databaseEntry = archive.CreateEntry("race.sqlite3", CompressionLevel.Optimal);
                    using (Stream stream = databaseEntry.Open())
                    using (FileStream input = File.OpenRead(tempDb)) input.CopyTo(stream);
                }
                if (File.Exists(fullDestination)) File.Replace(tempArchive, fullDestination, null);
                else File.Move(tempArchive, fullDestination);
                return fullDestination;
            }
            finally
            {
                if (File.Exists(tempDb)) File.Delete(tempDb);
                if (File.Exists(tempArchive)) File.Delete(tempArchive);
            }
        }

        public LocalEvent Restore(LocalWorkspace workspace, string archivePath)
        {
            if (workspace == null) throw new ArgumentNullException("workspace");
            string tempDb = Path.Combine(Path.GetTempPath(), "rfid-local-restore-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            try
            {
                BackupManifest manifest;
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    if (archive.Entries.Count != 2 || archive.GetEntry("manifest.json") == null || archive.GetEntry("race.sqlite3") == null)
                        throw new InvalidDataException("El paquete no tiene el formato de respaldo local esperado.");
                    if (archive.GetEntry("manifest.json").Length > 65536 || archive.GetEntry("race.sqlite3").Length > 250L * 1024 * 1024)
                        throw new InvalidDataException("El respaldo supera el tamaño máximo admitido.");
                    using (Stream stream = archive.GetEntry("manifest.json").Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        manifest = new JavaScriptSerializer().Deserialize<BackupManifest>(reader.ReadToEnd());
                    if (manifest == null || manifest.format_version != 1 || manifest.database_file != "race.sqlite3" || String.IsNullOrWhiteSpace(manifest.event_id))
                        throw new InvalidDataException("El manifiesto del respaldo no es compatible.");
                    using (Stream input = archive.GetEntry("race.sqlite3").Open())
                    using (FileStream output = new FileStream(tempDb, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
                string actualHash = HashFile(tempDb);
                if (!String.Equals(actualHash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La huella de la base no coincide con el manifiesto; no se restauró.");
                using (var database = new LocalRaceDatabase(tempDb))
                {
                    LocalEvent restored = database.GetSummary();
                    if (!String.Equals(restored.EventId, manifest.event_id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("La identidad interna no coincide con la del manifiesto.");
                }
                string installedPath = workspace.ImportRestoredDatabase(tempDb, manifest.event_id);
                using (var restoredDb = new LocalRaceDatabase(installedPath)) return restoredDb.GetSummary();
            }
            finally { if (File.Exists(tempDb)) File.Delete(tempDb); }
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
