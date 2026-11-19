using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;
using RFIDLocal.Infrastructure;
using RFIDTiming.Domain;
using RFIDTiming.Domain.Results;
using RFIDTiming.Services;

namespace RFIDLocal.Services
{
    public sealed class LocalIngestResult
    {
        public int HttpStatus { get; set; }
        public string Status { get; set; }
        public string OriginalStatus { get; set; }
        public string Reason { get; set; }
        public string Message { get; set; }
        public string ResponseJson { get; set; }
    }

    public sealed class LocalRaceDatabase : IDisposable
    {
        private sealed class LeaseEntry
        {
            public FileStream LockFile;
            public int References;
        }
        private static readonly object LeaseSync = new object();
        private static readonly Dictionary<string, LeaseEntry> Leases = new Dictionary<string, LeaseEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        private NativeSqlite db;
        private readonly string path;
        private bool leaseHeld;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = D07TransferPackage.MaximumBytes };

        public string Path { get { return path; } }

        public LocalRaceDatabase(string databasePath)
        {
            path = System.IO.Path.GetFullPath(databasePath);
            bool existed = File.Exists(path);
            AcquireLease(path);
            leaseHeld = true;
            try
            {
                db = new NativeSqlite(path);
                if (!existed) throw new InvalidDataException("La base no existía; no se inicializa una base vacía al abrir un evento.");
                db.Execute("PRAGMA foreign_keys=ON;");
                db.Execute("PRAGMA journal_mode=WAL;");
                db.Execute("PRAGMA synchronous=FULL;");
                long version = db.ScalarInt64("PRAGMA user_version;");
                if (version == 0) throw new InvalidDataException("La base del evento no contiene el esquema local esperado.");
                if (version > 3) throw new InvalidDataException("La base local requiere una versión de aplicación más nueva (esquema " + version + ").");
                if (version == 1) UpgradeSchemaToVersion2();
                if (db.ScalarInt64("PRAGMA user_version;") == 2) UpgradeSchemaToVersion3();
                IList<Dictionary<string, object>> check = db.Query("PRAGMA quick_check;");
                if (check.Count == 0 || Convert.ToString(check[0].Values.FirstOrDefault()) != "ok")
                    throw new InvalidDataException("SQLite detectó corrupción en la base local; el evento no se abrió vacío.");
            }
            catch { if (db != null) db.Dispose(); ReleaseLease(path); leaseHeld = false; throw; }
        }

        internal static void InitializeNewDatabase(string databasePath)
        {
            using (var connection = new NativeSqlite(databasePath))
            {
                connection.Execute("PRAGMA foreign_keys=ON;");
                connection.Execute("PRAGMA journal_mode=WAL;");
                connection.Execute("PRAGMA synchronous=FULL;");
                connection.ExecuteScript(@"
CREATE TABLE events (
 event_id TEXT PRIMARY KEY, name TEXT NOT NULL, event_date TEXT NOT NULL, time_zone TEXT NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('draft','open','closed')), source TEXT NOT NULL,
 has_authority INTEGER NOT NULL DEFAULT 1, roster_revision INTEGER NOT NULL DEFAULT 0,
 timing_revision INTEGER NOT NULL DEFAULT 0, policy_preset TEXT NOT NULL, policy_revision TEXT NOT NULL,
 remote_event_id TEXT, remote_operation_id TEXT, remote_base_revision TEXT, remote_base_hash TEXT,
 last_remote_revision INTEGER NOT NULL DEFAULT 0, last_remote_transfer_id TEXT, last_remote_payload_hash TEXT,
 publication_state TEXT NOT NULL DEFAULT 'unpublished', local_publication_revision INTEGER NOT NULL DEFAULT 0,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE distances (
 distance_key TEXT PRIMARY KEY, display_name TEXT NOT NULL, scheduled_start_utc TEXT, official_started INTEGER NOT NULL DEFAULT 0,
 exclude_general_top3_from_categories INTEGER NOT NULL DEFAULT 0, award_policy_configured INTEGER NOT NULL DEFAULT 0);
CREATE TABLE participants (
 participant_id TEXT PRIMARY KEY, bib TEXT, name TEXT NOT NULL, chip TEXT, distance TEXT NOT NULL,
 sex TEXT, category TEXT, age_bracket TEXT, dnf_at TEXT, source_json TEXT NOT NULL DEFAULT '{}',
 web_first_name TEXT, web_last_name TEXT, web_age INTEGER, web_team TEXT, web_public_token TEXT,
 source_row INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE INDEX participant_bib_idx ON participants(bib);
CREATE UNIQUE INDEX participant_bib_unique ON participants(bib COLLATE NOCASE) WHERE bib IS NOT NULL;
CREATE INDEX participant_chip_idx ON participants(chip);
CREATE INDEX participant_distance_idx ON participants(distance);
CREATE TABLE devices (
 device_id TEXT PRIMARY KEY, credential_hash TEXT NOT NULL, minimum_rssi INTEGER NOT NULL DEFAULT -127,
 active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
CREATE TABLE ingest_receipts (
 device_id TEXT NOT NULL, reading_id TEXT NOT NULL, payload_json TEXT NOT NULL, payload_hash TEXT NOT NULL,
 status TEXT NOT NULL, response_json TEXT NOT NULL, received_at TEXT NOT NULL,
 PRIMARY KEY(device_id, reading_id));
CREATE INDEX receipt_received_idx ON ingest_receipts(received_at);
CREATE TABLE receipt_reprocess_audit (
 reprocess_id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL, reading_id TEXT NOT NULL,
 previous_status TEXT NOT NULL, new_status TEXT NOT NULL, reason TEXT NOT NULL,
 previous_response_json TEXT NOT NULL, attempted_at TEXT NOT NULL);
CREATE TABLE timing_operations (
 operation_order INTEGER PRIMARY KEY AUTOINCREMENT, operation_id TEXT NOT NULL UNIQUE,
 participant_id TEXT NOT NULL REFERENCES participants(participant_id), stage TEXT NOT NULL CHECK(stage IN ('start','finish')),
 kind TEXT NOT NULL CHECK(kind IN ('rfid_accepted','manual_set','manual_voided')),
 captured_at TEXT, source_reading_id TEXT, antenna INTEGER, rssi INTEGER, reason TEXT, created_at TEXT NOT NULL);
CREATE INDEX timing_operation_participant_idx ON timing_operations(participant_id, stage, operation_order);
CREATE TABLE timing_marks (
 participant_id TEXT NOT NULL REFERENCES participants(participant_id), stage TEXT NOT NULL,
 operation_id TEXT NOT NULL, captured_at TEXT NOT NULL, kind TEXT NOT NULL, antenna INTEGER, rssi INTEGER,
 PRIMARY KEY(participant_id, stage));
CREATE TABLE audit_log (
 audit_id INTEGER PRIMARY KEY AUTOINCREMENT, action TEXT NOT NULL, entity_id TEXT, details_json TEXT NOT NULL,
 created_at TEXT NOT NULL);
CREATE TABLE result_snapshots (
 snapshot_id TEXT PRIMARY KEY, roster_revision TEXT NOT NULL, timing_revision TEXT NOT NULL,
 policy_preset TEXT NOT NULL, policy_revision TEXT NOT NULL, engine_version TEXT NOT NULL,
 snapshot_json TEXT NOT NULL, created_at TEXT NOT NULL,
 UNIQUE(roster_revision,timing_revision,policy_preset,policy_revision,engine_version));
CREATE TABLE transfer_outbox (
 transfer_id TEXT PRIMARY KEY, source_revision TEXT NOT NULL UNIQUE, revision INTEGER NOT NULL,
 payload_hash TEXT NOT NULL, package_json TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
 receipt_json TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
PRAGMA user_version=3;");
            }
        }

        private void UpgradeSchemaToVersion2()
        {
            db.ExecuteScript(@"
ALTER TABLE events ADD COLUMN remote_event_id TEXT;
ALTER TABLE events ADD COLUMN remote_operation_id TEXT;
ALTER TABLE events ADD COLUMN remote_base_revision TEXT;
ALTER TABLE events ADD COLUMN remote_base_hash TEXT;
ALTER TABLE events ADD COLUMN last_remote_revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE events ADD COLUMN last_remote_transfer_id TEXT;
ALTER TABLE events ADD COLUMN last_remote_payload_hash TEXT;
ALTER TABLE events ADD COLUMN publication_state TEXT NOT NULL DEFAULT 'unpublished';
ALTER TABLE events ADD COLUMN local_publication_revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE participants ADD COLUMN web_first_name TEXT;
ALTER TABLE participants ADD COLUMN web_last_name TEXT;
ALTER TABLE participants ADD COLUMN web_age INTEGER;
ALTER TABLE participants ADD COLUMN web_team TEXT;
ALTER TABLE participants ADD COLUMN web_public_token TEXT;
CREATE TABLE transfer_outbox (
 transfer_id TEXT PRIMARY KEY, source_revision TEXT NOT NULL UNIQUE, revision INTEGER NOT NULL,
 payload_hash TEXT NOT NULL, package_json TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
 receipt_json TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
PRAGMA user_version=2;");
        }

        private void UpgradeSchemaToVersion3()
        {
            db.ExecuteScript(@"
ALTER TABLE distances ADD COLUMN exclude_general_top3_from_categories INTEGER NOT NULL DEFAULT 0;
ALTER TABLE distances ADD COLUMN award_policy_configured INTEGER NOT NULL DEFAULT 0;
UPDATE distances SET exclude_general_top3_from_categories=CASE WHEN distance_key IN ('10KM','21KM') THEN 1 ELSE 0 END,
 award_policy_configured=1;
PRAGMA user_version=3;");
        }

        public void CreateEvent(string eventId, string name, DateTime eventDate, string timeZone,
            IEnumerable<string> distanceNames, IDictionary<string, bool> exclusionPolicies,
            string deviceId, string credentialHash)
        {
            string now = UtcNow();
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    db.Execute("INSERT INTO events(event_id,name,event_date,time_zone,status,source,has_authority,policy_preset,policy_revision,created_at,updated_at) VALUES(?,?,?,?,'draft','autonomous',1,?,?,?,?);",
                        eventId, name, eventDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZone,
                        ResultPolicy.CompatActualV1, "1", now, now);
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string distance in distanceNames ?? Enumerable.Empty<string>())
                    {
                        string display = (distance ?? "").Trim();
                        string key = ResultDistanceKey.Normalize(display);
                        if (key == null || !used.Add(key)) continue;
                        if (exclusionPolicies == null || !exclusionPolicies.ContainsKey(key))
                            throw new InvalidOperationException("Confirme la política de podio para la distancia " + display + ".");
                        db.Execute("INSERT INTO distances(distance_key,display_name,exclude_general_top3_from_categories,award_policy_configured) VALUES(?,?,?,1);",
                            key, display, exclusionPolicies[key] ? 1 : 0);
                    }
                    if (used.Count == 0) throw new ArgumentException("Agregue al menos una distancia válida.");
                    db.Execute("INSERT INTO devices(device_id,credential_hash,minimum_rssi,active,created_at) VALUES(?,?, -127,1,?);",
                        deviceId, credentialHash, now);
                    Audit("event_created", eventId, new { name = name, source = "autonomous", distances = used.Count });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public void ImportPreparationPackage(Dictionary<string, object> package)
        {
            D07TransferPackage.ValidatePreparation(package);
            Dictionary<string, object> manifest = D07TransferPackage.Object(D07TransferPackage.Get(package, "manifest"), "manifest");
            Dictionary<string, object> eventData = D07TransferPackage.Object(D07TransferPackage.Get(package, "event"), "event");
            Dictionary<string, object> policy = D07TransferPackage.Object(D07TransferPackage.Get(package, "policy"), "policy");
            string eventId = D07TransferPackage.Text(eventData, "id");
            string name = D07TransferPackage.Text(eventData, "name").Trim();
            string timezone = D07TransferPackage.Text(eventData, "timezone").Trim();
            string eventDate = D07TransferPackage.Text(eventData, "event_date");
            DateTime parsedDate;
            if (!DateTime.TryParseExact(eventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                throw new InvalidDataException("La preparación no tiene una fecha local válida.");
            LocalWorkspace.ResolveTimeZone(timezone);
            IList<object> participants = D07TransferPackage.Array(D07TransferPackage.Get(package, "participants"), "participants");
            IList<object> distances = D07TransferPackage.Array(D07TransferPackage.Get(package, "distances"), "distances");
            lock (sync)
            {
                if (db.ScalarInt64("SELECT COUNT(*) FROM events WHERE event_id=?;", eventId) != 0)
                    throw new InvalidOperationException("Este evento ya existe localmente. No se mezclaron nómina ni marcas.");
                db.Begin();
                try
                {
                    db.Execute("INSERT INTO events(event_id,name,event_date,time_zone,status,source,has_authority,roster_revision,timing_revision,policy_preset,policy_revision,remote_event_id,remote_operation_id,remote_base_revision,remote_base_hash,created_at,updated_at) VALUES(?,?,?,?,'draft','linked',0,1,0,?,?,?,?,?,?,?,?);",
                        eventId, name, eventDate, timezone, D07TransferPackage.Text(policy, "preset_id"),
                        D07TransferPackage.Text(policy, "revision"), eventId,
                        D07TransferPackage.Text(manifest, "operation_id"),
                        D07TransferPackage.Text(manifest, "source_revision"),
                        D07TransferPackage.Text(package, "sha256"), UtcNow(), UtcNow());

                    var distanceKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object item in distances)
                    {
                        Dictionary<string, object> distance = D07TransferPackage.Object(item, "distance");
                        string display = D07TransferPackage.Text(distance, "distance");
                        string key = ResultDistanceKey.Normalize(display);
                        if (key == null || !distanceKeys.Add(key)) throw new InvalidDataException("La preparación contiene distancias duplicadas o vacías.");
                        if (!D07TransferPackage.Boolean(distance, "award_policy_configured"))
                            throw new InvalidDataException("La preparación no incluye una política explícita para " + display + ".");
                        db.Execute("INSERT INTO distances(distance_key,display_name,scheduled_start_utc,official_started,exclude_general_top3_from_categories,award_policy_configured) VALUES(?,?,?,0,?,1);",
                            key, display, D07TransferPackage.Text(distance, "official_start_at"),
                            D07TransferPackage.Boolean(distance, "exclude_general_top3_from_categories") ? 1 : 0);
                    }
                    if (distanceKeys.Count == 0) throw new InvalidDataException("La preparación no contiene distancias.");

                    var participantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object item in participants)
                    {
                        Dictionary<string, object> participant = D07TransferPackage.Object(item, "participant");
                        string participantId = D07TransferPackage.Text(participant, "id");
                        Guid parsedId;
                        string firstName = D07TransferPackage.Text(participant, "first_name");
                        string lastName = D07TransferPackage.Text(participant, "last_name");
                        string fullName = String.Join(" ", new[] { firstName, lastName }.Where(part => !String.IsNullOrWhiteSpace(part))).Trim();
                        string distanceName = D07TransferPackage.Text(participant, "distance");
                        if (!Guid.TryParse(participantId, out parsedId) || !participantIds.Add(participantId) ||
                            String.IsNullOrWhiteSpace(fullName) || !distanceKeys.Contains(ResultDistanceKey.Normalize(distanceName)))
                            throw new InvalidDataException("La preparación contiene una persona duplicada o incompleta.");
                        string webFields = serializer.Serialize(new Dictionary<string, object>
                        {
                            { "first_name", firstName }, { "last_name", lastName },
                            { "age", D07TransferPackage.Get(participant, "age") },
                            { "team", D07TransferPackage.Get(participant, "team") },
                            { "age_bracket", D07TransferPackage.Get(participant, "age_bracket") }
                        });
                        db.Execute("INSERT INTO participants(participant_id,bib,name,chip,distance,sex,category,age_bracket,dnf_at,source_json,web_first_name,web_last_name,web_age,web_team,web_public_token,source_row,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?, ?,?,?,?,NULL,?,?);",
                            participantId, D07TransferPackage.Text(participant, "bib"), fullName,
                            D07TransferPackage.Text(participant, "chip"), distanceName,
                            D07TransferPackage.Text(participant, "sex"), D07TransferPackage.Text(participant, "category"),
                            D07TransferPackage.Text(participant, "age_bracket"), D07TransferPackage.Text(participant, "dnf_at"),
                            webFields, firstName, lastName, D07TransferPackage.Get(participant, "age"),
                            D07TransferPackage.Text(participant, "team"), D07TransferPackage.Text(participant, "public_token"), UtcNow(), UtcNow());
                    }
                    if (participantIds.Count != participants.Count) throw new InvalidDataException("La preparación contiene participantes duplicados.");
                    Audit("remote_preparation_imported", eventId, new
                    {
                        operation_id = D07TransferPackage.Text(manifest, "operation_id"),
                        source_revision = D07TransferPackage.Text(manifest, "source_revision"),
                        package_hash = D07TransferPackage.Text(package, "sha256"),
                        participants = participantIds.Count,
                        distances = distanceKeys.Count
                    });
                    db.Commit();
                }
                catch { Rollback(); throw; }
            }
        }

        public string BuildEventTransferPackage()
        {
            ResultSnapshot snapshot = CalculateResults();
            lock (sync)
            {
                Dictionary<string, object> eventRow = db.Query("SELECT * FROM events LIMIT 1;").First();
                string sourceRevision = Number(eventRow, "roster_revision").ToString(CultureInfo.InvariantCulture) + ":" +
                    Number(eventRow, "timing_revision").ToString(CultureInfo.InvariantCulture) + ":" +
                    Text(eventRow, "policy_revision") + ":" + Text(eventRow, "status");
                Dictionary<string, object> saved = db.Query("SELECT package_json FROM transfer_outbox WHERE source_revision=?;", sourceRevision).FirstOrDefault();
                if (saved != null) return Text(saved, "package_json");

                db.Begin();
                try
                {
                    long revision = Math.Max(Number(eventRow, "local_publication_revision"), Number(eventRow, "last_remote_revision")) + 1;
                    string transferId = Guid.NewGuid().ToString("D");
                    var eventData = new Dictionary<string, object>
                    {
                        { "id", Text(eventRow, "event_id") }, { "name", Text(eventRow, "name") },
                        { "event_date", Text(eventRow, "event_date") }, { "timezone", Text(eventRow, "time_zone") },
                        { "status", Text(eventRow, "status") }
                    };
                    var distanceList = new List<object>();
                    var distanceNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Dictionary<string, object> row in db.Query("SELECT distance_key,distance_key AS distance,display_name,scheduled_start_utc,official_started,exclude_general_top3_from_categories,award_policy_configured FROM distances ORDER BY distance_key;"))
                    {
                        distanceNames[Text(row, "distance_key")] = Text(row, "display_name");
                        distanceList.Add(new Dictionary<string, object>
                        {
                            { "distance", Text(row, "display_name") }, { "distance_key", Text(row, "distance_key") },
                            { "official_start_at", Text(row, "scheduled_start_utc") },
                            { "started_at", Number(row, "official_started") != 0 ? Text(row, "scheduled_start_utc") : null },
                            { "exclude_general_top3_from_categories", Number(row, "exclude_general_top3_from_categories") != 0 },
                            { "award_policy_configured", Number(row, "award_policy_configured") != 0 }
                        });
                    }

                    var participantList = new List<object>();
                    foreach (Dictionary<string, object> row in db.Query("SELECT * FROM participants ORDER BY participant_id;"))
                    {
                        string firstName = Text(row, "web_first_name"), lastName = Text(row, "web_last_name");
                        if (String.IsNullOrWhiteSpace(firstName)) firstName = Text(row, "name");
                        string distanceKey = ResultDistanceKey.Normalize(Text(row, "distance"));
                        string distanceDisplay;
                        if (!distanceNames.TryGetValue(distanceKey ?? "", out distanceDisplay)) distanceDisplay = Text(row, "distance");
                        participantList.Add(new Dictionary<string, object>
                        {
                            { "id", Text(row, "participant_id") }, { "bib", Text(row, "bib") }, { "chip", Text(row, "chip") },
                            { "first_name", firstName }, { "last_name", lastName }, { "sex", D07TransferPackage.NormalizeSex(Text(row, "sex")) },
                            { "category", Text(row, "category") }, { "team", Text(row, "web_team") },
                            { "distance", distanceDisplay }, { "age", TransferAge(row) },
                            { "age_bracket", Text(row, "age_bracket") }, { "dnf_at", Text(row, "dnf_at") },
                            { "public_token", Text(row, "web_public_token") }
                        });
                    }
                    var marks = new List<object>();
                    foreach (Dictionary<string, object> row in db.Query("SELECT participant_id,stage,operation_id,captured_at,kind,antenna,rssi FROM timing_marks ORDER BY participant_id,stage;"))
                        marks.Add(new Dictionary<string, object>
                        {
                            { "participant_id", Text(row, "participant_id") }, { "stage", Text(row, "stage") },
                            { "operation_id", Text(row, "operation_id") }, { "captured_at", Text(row, "captured_at") },
                            { "kind", Text(row, "kind") }, { "antenna", NullableNumber(row, "antenna") }, { "rssi", NullableNumber(row, "rssi") }
                        });
                    var operations = new List<object>();
                    foreach (Dictionary<string, object> row in db.Query("SELECT operation_id,participant_id,stage,kind,operation_order,captured_at,antenna,rssi,reason,source_reading_id,created_at FROM timing_operations ORDER BY operation_order;"))
                        operations.Add(new Dictionary<string, object>
                        {
                            { "operation_id", Text(row, "operation_id") }, { "participant_id", Text(row, "participant_id") },
                            { "stage", Text(row, "stage") }, { "kind", Text(row, "kind") },
                            { "operation_order", Number(row, "operation_order") }, { "captured_at", Text(row, "captured_at") },
                            { "antenna", NullableNumber(row, "antenna") }, { "rssi", NullableNumber(row, "rssi") },
                            { "reason", Text(row, "reason") }, { "source_reading_id", Text(row, "source_reading_id") },
                            { "created_at", Text(row, "created_at") }
                        });
                    var receipts = new List<object>();
                    foreach (Dictionary<string, object> row in db.Query("SELECT device_id,reading_id,status,payload_json,response_json,received_at FROM ingest_receipts ORDER BY received_at,device_id,reading_id;"))
                    {
                        Dictionary<string, object> payload = ParseObject(Text(row, "payload_json"));
                        Dictionary<string, object> response = ParseObject(Text(row, "response_json"));
                        receipts.Add(new Dictionary<string, object>
                        {
                            { "device_id", Text(row, "device_id") }, { "reading_id", Text(row, "reading_id") },
                            { "status", Text(row, "status") }, { "reason", D07TransferPackage.Text(response, "reason") },
                            { "stage", D07TransferPackage.Text(payload, "stage") },
                            { "captured_at", D07TransferPackage.Text(payload, "captured_at") },
                            { "received_at", Text(row, "received_at") }
                        });
                    }
                    var eventHistory = new List<object>();
                    foreach (Dictionary<string, object> row in db.Query("SELECT action,entity_id,details_json,created_at FROM audit_log WHERE action IN ('official_distance_start','official_category_start','participant_dnf','participant_dnf_cleared','event_status_changed','manual_mark_set') ORDER BY audit_id;"))
                        eventHistory.Add(new Dictionary<string, object>
                        {
                            { "action", Text(row, "action") }, { "entity_id", Text(row, "entity_id") },
                            { "details", ParseObject(Text(row, "details_json")) }, { "created_at", Text(row, "created_at") }
                        });
                    foreach (Dictionary<string, object> row in db.Query("SELECT device_id,reading_id,previous_status,new_status,reason,attempted_at FROM receipt_reprocess_audit ORDER BY reprocess_id;"))
                        eventHistory.Add(new Dictionary<string, object>
                        {
                            { "action", "receipt_reprocessed" }, { "entity_id", Text(row, "reading_id") },
                            { "details", new Dictionary<string, object>
                                {
                                    { "device_id", Text(row, "device_id") }, { "previous_status", Text(row, "previous_status") },
                                    { "new_status", Text(row, "new_status") }, { "reason", Text(row, "reason") }
                                }
                            },
                            { "created_at", Text(row, "attempted_at") }
                        });
                    var expectedResults = new List<object>();
                    foreach (ParticipantResult result in snapshot.Results)
                        expectedResults.Add(new Dictionary<string, object>
                        {
                            { "participant_id", result.ParticipantId }, { "status", result.StatusLabel },
                            { "start_at", result.Start == null ? null : result.Start.CapturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                            { "finish_at", result.Finish == null ? null : result.Finish.CapturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                            { "overall_place", result.OverallPlace }, { "category_place", result.CategoryPlace }
                        });

                    var manifest = new Dictionary<string, object>
                    {
                        { "transfer_id", transferId }, { "revision", revision }, { "source_revision", sourceRevision },
                        { "operation_id", Text(eventRow, "remote_operation_id") },
                        { "base_package_hash", Text(eventRow, "remote_base_hash") },
                        { "engine_version", snapshot.EngineVersion }, { "result_roster_revision", snapshot.RosterRevision },
                        { "result_timing_revision", snapshot.TimingRevision }, { "final_requested", Text(eventRow, "status") == "closed" }
                    };
                    var package = new Dictionary<string, object>
                    {
                        { "format", D07TransferPackage.Format }, { "version", D07TransferPackage.Version },
                        { "kind", "publication" }, { "manifest", manifest }, { "event", eventData },
                        { "policy", new Dictionary<string, object> { { "preset_id", Text(eventRow, "policy_preset") }, { "revision", Text(eventRow, "policy_revision") } } },
                        { "distances", distanceList }, { "participants", participantList }, { "marks", marks },
                        { "operations", operations }, { "receipts", receipts },
                        { "history", eventHistory }, { "expected_results", expectedResults }
                    };
                    D07TransferPackage.ValidatePublication(package);
                    string packageJson = D07TransferPackage.Seal(package);
                    string hash = D07TransferPackage.Text(package, "sha256");
                    db.Execute("INSERT INTO transfer_outbox(transfer_id,source_revision,revision,payload_hash,package_json,status,created_at,updated_at) VALUES(?,?,?,?,?,'pending',?,?);",
                        transferId, sourceRevision, revision, hash, packageJson, UtcNow(), UtcNow());
                    db.Execute("UPDATE events SET local_publication_revision=?,updated_at=?;", revision, UtcNow());
                    Audit("publication_package_created", transferId, new { revision = revision, sha256 = hash });
                    db.Commit();
                    return packageJson;
                }
                catch { Rollback(); throw; }
            }
        }

        public void ApplyPublicationReceipt(string receiptJson)
        {
            if (String.IsNullOrWhiteSpace(receiptJson) || Encoding.UTF8.GetByteCount(receiptJson) > 65536)
                throw new InvalidDataException("El recibo de publicación está vacío o supera 64 KB.");
            Dictionary<string, object> receipt = serializer.Deserialize<Dictionary<string, object>>(receiptJson);
            if (receipt == null || D07TransferPackage.Text(receipt, "format") != "yr-publication-receipt" ||
                D07TransferPackage.Integer(receipt, "version") != 1 ||
                !D07TransferPackage.Boolean(receipt, "received") || !D07TransferPackage.Boolean(receipt, "verified") ||
                !new[] { "provisional", "final" }.Contains(D07TransferPackage.Text(receipt, "publication_state")) ||
                D07TransferPackage.Boolean(receipt, "published") != (D07TransferPackage.Text(receipt, "publication_state") == "final"))
                throw new InvalidDataException("El recibo web no tiene un formato reconocido o no confirma recepción.");
            string transferId = D07TransferPackage.Text(receipt, "transfer_id");
            string hash = D07TransferPackage.Text(receipt, "sha256");
            long revision = D07TransferPackage.Integer(receipt, "revision");
            DateTimeOffset receivedAt;
            string receivedAtText = D07TransferPackage.Text(receipt, "received_at");
            Guid parsedTransferId;
            if (!Guid.TryParse(transferId, out parsedTransferId) || String.IsNullOrWhiteSpace(hash) ||
                !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{64}$") || revision < 1 ||
                !DateTimeOffset.TryParse(receivedAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out receivedAt))
                throw new InvalidDataException("La identidad, la huella o la revisión del recibo no son válidas.");
            lock (sync)
            {
                db.Begin();
                try
                {
                    Dictionary<string, object> outgoing = db.Query("SELECT payload_hash,package_json FROM transfer_outbox WHERE transfer_id=?;", transferId).FirstOrDefault();
                    if (outgoing == null || !String.Equals(Text(outgoing, "payload_hash"), hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El recibo no corresponde a una revisión que esta aplicación haya generado.");
                    Dictionary<string, object> package = serializer.Deserialize<Dictionary<string, object>>(Text(outgoing, "package_json"));
                    Dictionary<string, object> manifest = D07TransferPackage.Object(D07TransferPackage.Get(package, "manifest"), "manifest");
                    Dictionary<string, object> packageEvent = D07TransferPackage.Object(D07TransferPackage.Get(package, "event"), "event");
                    if (!String.Equals(D07TransferPackage.Hash(package), hash, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(D07TransferPackage.Text(packageEvent, "id"), D07TransferPackage.Text(receipt, "event_id"), StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(D07TransferPackage.Text(manifest, "source_revision"), D07TransferPackage.Text(receipt, "source_revision"), StringComparison.Ordinal) ||
                        D07TransferPackage.Integer(manifest, "revision") != D07TransferPackage.Integer(receipt, "local_revision") ||
                        D07TransferPackage.Integer(manifest, "revision") != revision ||
                        !String.Equals(D07TransferPackage.Text(manifest, "transfer_id"), transferId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El recibo no identifica el mismo evento y revisión local del paquete.");
                    if (revision < Number(db.Query("SELECT last_remote_revision FROM events LIMIT 1;").First(), "last_remote_revision") &&
                        !String.Equals(Text(db.Query("SELECT last_remote_transfer_id FROM events LIMIT 1;").First(), "last_remote_transfer_id"), transferId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El recibo pertenece a una revisión remota anterior a la ya confirmada.");
                    db.Execute("UPDATE transfer_outbox SET status=?,receipt_json=?,updated_at=? WHERE transfer_id=?;",
                        D07TransferPackage.Text(receipt, "publication_state"), receiptJson, UtcNow(), transferId);
                    db.Execute("UPDATE events SET last_remote_revision=?,last_remote_transfer_id=?,last_remote_payload_hash=?,publication_state=?,updated_at=?;",
                        revision, transferId, hash, D07TransferPackage.Text(receipt, "publication_state"), UtcNow());
                    Audit("publication_receipt_imported", transferId, new { revision = revision, sha256 = hash, state = D07TransferPackage.Text(receipt, "publication_state") });
                    db.Commit();
                }
                catch { Rollback(); throw; }
            }
        }

        public LocalEvent GetSummary()
        {
            lock (sync)
            {
                Dictionary<string, object> row = db.Query("SELECT * FROM events LIMIT 1;").FirstOrDefault();
                if (row == null) throw new InvalidDataException("La base local no contiene un evento.");
                return new LocalEvent
                {
                    EventId = Text(row, "event_id"), Name = Text(row, "name"),
                    EventDate = DateTime.ParseExact(Text(row, "event_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    TimeZone = Text(row, "time_zone"), Status = Text(row, "status"), Source = Text(row, "source"),
                    HasAuthority = Number(row, "has_authority") != 0,
                    ParticipantCount = (int)db.ScalarInt64("SELECT COUNT(*) FROM participants;"),
                    ReceiptCount = (int)db.ScalarInt64("SELECT COUNT(*) FROM ingest_receipts;"),
                    DatabasePath = path,
                    RemoteEventId = Text(row, "remote_event_id"), RemoteOperationId = Text(row, "remote_operation_id"),
                    PublicationState = Text(row, "publication_state"), LastRemoteRevision = Number(row, "last_remote_revision"),
                    LastRemoteTransferId = Text(row, "last_remote_transfer_id")
                };
            }
        }

        public IList<string> GetDistances()
        {
            lock (sync) return db.Query("SELECT display_name FROM distances ORDER BY distance_key;")
                .Select(row => Text(row, "display_name")).ToList();
        }

        public IList<string> GetCategories(string distance)
        {
            string key = ResultDistanceKey.Normalize(distance);
            if (key == null) throw new ArgumentException("Distancia no válida.");
            lock (sync) return db.Query("SELECT distance,category FROM participants;")
                .Where(row => ResultDistanceKey.Normalize(Text(row, "distance")) == key)
                .Select(row => (Text(row, "category") ?? "").Trim())
                .Where(category => !String.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IList<Dictionary<string, object>> GetParticipants()
        {
            lock (sync) return db.Query("SELECT participant_id,bib,name,chip,distance,sex,category,age_bracket,dnf_at,source_row,updated_at FROM participants ORDER BY CASE WHEN bib IS NULL THEN 1 ELSE 0 END,bib,name;");
        }

        public RosterImportPreview AssessRosterImport(RosterImportPreview preview)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            lock (sync)
            {
                if (db.ScalarInt64("SELECT COUNT(*) FROM distances WHERE award_policy_configured=0;") > 0)
                    throw new InvalidOperationException("Confirme la política de podio de cada distancia antes de cargar la nómina.");
                IList<Dictionary<string, object>> existing = db.Query("SELECT participant_id,bib,name,chip,distance,sex,category,age_bracket,web_first_name,web_last_name,web_age,web_team FROM participants;");
                var distances = new HashSet<string>(db.Query("SELECT distance_key FROM distances;").Select(row => Text(row, "distance_key")), StringComparer.Ordinal);
                var byBib = existing.Where(row => Text(row, "bib") != null)
                    .GroupBy(row => Text(row, "bib"), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var byId = existing.ToDictionary(row => Text(row, "participant_id"), StringComparer.OrdinalIgnoreCase);
                foreach (RosterRow row in preview.Rows)
                {
                    Dictionary<string, object> current = null;
                    if (!String.IsNullOrWhiteSpace(row.ParticipantId)) byId.TryGetValue(row.ParticipantId, out current);
                    Dictionary<string, object> bibOwner = null;
                    if (!String.IsNullOrWhiteSpace(row.Bib)) byBib.TryGetValue(row.Bib, out bibOwner);
                    if (current != null && bibOwner != null &&
                        !String.Equals(Text(current, "participant_id"), Text(bibOwner, "participant_id"), StringComparison.OrdinalIgnoreCase))
                        preview.Errors.Add("Fila " + row.SourceRow + ": el participant_id y el dorsal apuntan a dos personas distintas.");
                    if (current == null && !String.IsNullOrWhiteSpace(row.Bib)) byBib.TryGetValue(row.Bib, out current);
                    if (current == null) preview.Added++;
                    else if (Same(Text(current, "bib"), EmptyToNull(row.Bib)) && Same(Text(current, "name"), row.Name) && Same(Text(current, "chip"), EmptyToNull(ChipIdNormalizer.Normalize(row.Chip))) &&
                        Same(Text(current, "distance"), row.Distance) && Same(Text(current, "sex"), EmptyToNull(row.Sex)) &&
                        Same(Text(current, "category"), EmptyToNull(row.Category)) && Same(Text(current, "age_bracket"), EmptyToNull(row.AgeBracket)) &&
                        Same(Text(current, "web_first_name"), EmptyToNull(row.FirstName)) && Same(Text(current, "web_last_name"), EmptyToNull(row.LastName)) &&
                        Same(Text(current, "web_age"), row.Age.HasValue ? row.Age.Value.ToString(CultureInfo.InvariantCulture) : null) &&
                        Same(Text(current, "web_team"), EmptyToNull(row.Team))) preview.Unchanged++;
                    else preview.Updated++;

                    string key = ResultDistanceKey.Normalize(row.Distance);
                    if (key == null || !distances.Contains(key))
                    {
                        preview.UnknownDistance++;
                        preview.Errors.Add("Fila " + row.SourceRow + ": distancia " + row.Distance + " no está configurada en este evento.");
                    }
                    string chip = ChipIdNormalizer.Normalize(row.Chip);
                    if (chip.Length > 0)
                    {
                        Dictionary<string, object> conflict = existing.FirstOrDefault(item =>
                            String.Equals(Text(item, "chip"), chip, StringComparison.OrdinalIgnoreCase) &&
                            (current == null || !String.Equals(Text(item, "participant_id"), Text(current, "participant_id"), StringComparison.OrdinalIgnoreCase)));
                        if (conflict != null) preview.Errors.Add("Fila " + row.SourceRow + ": chip " + chip + " ya está asignado a otro participante.");
                    }
                }
                return preview;
            }
        }

        public IList<Dictionary<string, object>> GetReceipts()
        {
            lock (sync)
            {
                IList<Dictionary<string, object>> rows = db.Query("SELECT received_at,device_id,reading_id,status,response_json FROM ingest_receipts ORDER BY received_at DESC LIMIT 1000;");
                foreach (Dictionary<string, object> row in rows)
                {
                    try
                    {
                        var response = serializer.Deserialize<Dictionary<string, object>>(Text(row, "response_json")) ?? new Dictionary<string, object>();
                        row["reason"] = response.ContainsKey("reason") ? response["reason"] : null;
                        row["message"] = response.ContainsKey("message") ? response["message"] : null;
                    }
                    catch { row["reason"] = "receipt_response_unreadable"; row["message"] = "El JSON de respuesta requiere revisión."; }
                }
                return rows;
            }
        }

        public IList<Dictionary<string, object>> GetAudit()
        {
            lock (sync) return db.Query("SELECT created_at,action,entity_id,details_json FROM audit_log ORDER BY audit_id DESC LIMIT 1000;");
        }

        public string ReprocessUnknownChip(string deviceId, string readingId, string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("El reprocesamiento requiere motivo.", "reason");
            lock (sync)
            {
                EnsureWritable();
                if (db.ScalarString("SELECT status FROM events LIMIT 1;") != "open")
                    throw new InvalidOperationException("Habilite recepción antes de reprocesar una lectura.");
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    Dictionary<string, object> receipt = db.Query("SELECT payload_json,status,response_json FROM ingest_receipts WHERE device_id=? AND reading_id=?;", deviceId, readingId).FirstOrDefault();
                    if (receipt == null) throw new InvalidOperationException("No se encontró el recibo local seleccionado.");
                    string previousStatus = Text(receipt, "status");
                    if (previousStatus != "unknown_chip")
                        throw new InvalidOperationException("Solo se pueden reprocesar recibos unknown_chip; este recibo tiene estado " + previousStatus + ".");
                    string payloadJson = Text(receipt, "payload_json");
                    IDictionary<string, object> payload = serializer.Deserialize<Dictionary<string, object>>(payloadJson);
                    string eventId = GetValue(payload, "event_id"), payloadDeviceId = GetValue(payload, "device_id");
                    string stage = (GetValue(payload, "stage") ?? "").ToLowerInvariant();
                    string chip = ChipIdNormalizer.Normalize(GetValue(payload, "epc"));
                    DateTimeOffset captured;
                    if (!String.Equals(eventId, GetEventId(), StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(deviceId, payloadDeviceId, StringComparison.OrdinalIgnoreCase) ||
                        (stage != "start" && stage != "finish") ||
                        !DateTimeOffset.TryParse(GetValue(payload, "captured_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out captured))
                        throw new InvalidDataException("El payload del recibo no coincide con su identidad original.");
                    string status, reasonCode = null, message;
                    Dictionary<string, object> participant = db.Query("SELECT participant_id FROM participants WHERE chip=? COLLATE NOCASE LIMIT 1;", chip).FirstOrDefault();
                    if (participant == null) { status = "unknown_chip"; reasonCode = "unknown_chip"; message = "El chip continúa sin asignar en la nómina."; }
                    else
                    {
                        string participantId = Text(participant, "participant_id");
                        Dictionary<string, object> current = db.Query("SELECT kind,captured_at FROM timing_marks WHERE participant_id=? AND stage=?;", participantId, stage).FirstOrDefault();
                        if (current != null && Text(current, "kind") == "manual_set")
                        { status = "manual_override"; reasonCode = "manual_override"; message = "La marca vigente fue corregida manualmente."; }
                        else if (current != null && DateTimeOffset.Parse(Text(current, "captured_at"), CultureInfo.InvariantCulture).ToUniversalTime() <= captured.ToUniversalTime())
                        { status = "later_than_current"; reasonCode = "later_than_current"; message = "Ya existe una lectura RFID anterior para esta etapa."; }
                        else
                        {
                            status = "accepted"; message = "Lectura original reprocesada y guardada.";
                            string operationId = deviceId + ":" + readingId;
                            InsertOperation(operationId, participantId, stage, "rfid_accepted", captured, readingId, null, null, reason);
                            db.Execute("INSERT OR REPLACE INTO timing_marks(participant_id,stage,operation_id,captured_at,kind,antenna,rssi) VALUES(?,?,?,?,'rfid_accepted',NULL,NULL);",
                                participantId, stage, operationId, captured.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                            db.Execute("UPDATE events SET timing_revision=timing_revision+1,updated_at=?;", UtcNow());
                        }
                    }
                    string response = BuildResponse(status, reasonCode, message, readingId, null);
                    string previousResponse = Text(receipt, "response_json");
                    db.Execute("UPDATE ingest_receipts SET status=?,response_json=? WHERE device_id=? AND reading_id=?;", status, response, deviceId, readingId);
                    db.Execute("INSERT INTO receipt_reprocess_audit(device_id,reading_id,previous_status,new_status,reason,previous_response_json,attempted_at) VALUES(?,?,?,?,?,?,?);",
                        deviceId, readingId, previousStatus, status, reason.Trim(), previousResponse, UtcNow());
                    Audit("receipt_reprocessed", readingId, new { device_id = deviceId, previous_status = previousStatus, new_status = status, reason = reason.Trim() });
                    db.Execute("COMMIT;");
                    return status;
                }
                catch { Rollback(); throw; }
            }
        }

        public IDictionary<string, long> GetReceiptCounts()
        {
            lock (sync)
            {
                var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> row in db.Query("SELECT status,COUNT(*) AS n FROM ingest_receipts GROUP BY status;"))
                    counts[Text(row, "status")] = Number(row, "n");
                return counts;
            }
        }

        public string GetRevisionKey()
        {
            lock (sync)
            {
                Dictionary<string, object> row = db.Query("SELECT roster_revision,timing_revision FROM events LIMIT 1;").First();
                return Number(row, "roster_revision").ToString(CultureInfo.InvariantCulture) + ":" +
                    Number(row, "timing_revision").ToString(CultureInfo.InvariantCulture);
            }
        }

        public string GetActiveDeviceId()
        {
            lock (sync) return db.ScalarString("SELECT device_id FROM devices WHERE active=1 ORDER BY created_at DESC,rowid DESC LIMIT 1;");
        }

        public LocalDevice AddReaderCredential()
        {
            string secret = NewCredential();
            string deviceId = Guid.NewGuid().ToString("D");
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    LocalEvent race = GetSummary();
                    if (!race.HasAuthority)
                        throw new InvalidOperationException("Este evento local no tiene autoridad. Tómela antes de vincular un lector.");
                    if (race.Status == "closed")
                        throw new InvalidOperationException("No se pueden vincular lectores a un evento cerrado.");
                    db.Execute("INSERT INTO devices(device_id,credential_hash,minimum_rssi,active,created_at) VALUES(?,?, -127,1,?);",
                        deviceId, LocalWorkspace.HashCredential(secret), UtcNow());
                    Audit("reader_credential_created", deviceId, new { event_id = race.EventId });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
            return new LocalDevice { DeviceId = deviceId, Credential = secret, MinimumRssi = -127 };
        }

        public void ApplyRoster(IEnumerable<RosterRow> incoming)
        {
            List<RosterRow> rows = (incoming ?? Enumerable.Empty<RosterRow>()).ToList();
            if (rows.Count == 0) throw new InvalidDataException("El archivo no contiene participantes.");
            lock (sync)
            {
                EnsureWritable();
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    int added = 0, updated = 0;
                    foreach (RosterRow row in rows)
                    {
                        Dictionary<string, object> existing = null;
                        if (!String.IsNullOrWhiteSpace(row.ParticipantId))
                            existing = db.Query("SELECT participant_id,name,chip,distance,sex,category,age_bracket FROM participants WHERE participant_id=? LIMIT 1;", row.ParticipantId).FirstOrDefault();
                        if (existing == null && !String.IsNullOrWhiteSpace(row.Bib))
                            existing = db.Query("SELECT participant_id,name,chip,distance,sex,category,age_bracket FROM participants WHERE bib=? COLLATE NOCASE LIMIT 1;", row.Bib).FirstOrDefault();
                        string id = existing == null ? (String.IsNullOrWhiteSpace(row.ParticipantId) ? Guid.NewGuid().ToString("D") : row.ParticipantId) : Text(existing, "participant_id");
                        string now = UtcNow();
                        if (existing == null)
                        {
                            db.Execute("INSERT INTO participants(participant_id,bib,name,chip,distance,sex,category,age_bracket,source_json,web_first_name,web_last_name,web_age,web_team,source_row,created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);",
                                id, EmptyToNull(row.Bib), row.Name.Trim(), EmptyToNull(ChipIdNormalizer.Normalize(row.Chip)), row.Distance.Trim(), EmptyToNull(row.Sex), EmptyToNull(row.Category), EmptyToNull(row.AgeBracket), row.SourceJson ?? "{}",
                                EmptyToNull(row.FirstName), EmptyToNull(row.LastName), row.Age, EmptyToNull(row.Team), row.SourceRow, now, now);
                            added++;
                        }
                        else
                        {
                            db.Execute("UPDATE participants SET bib=?,name=?,chip=?,distance=?,sex=?,category=?,age_bracket=?,source_json=?,web_first_name=?,web_last_name=?,web_age=?,web_team=?,source_row=?,updated_at=? WHERE participant_id=?;",
                                EmptyToNull(row.Bib), row.Name.Trim(), EmptyToNull(ChipIdNormalizer.Normalize(row.Chip)), row.Distance.Trim(), EmptyToNull(row.Sex), EmptyToNull(row.Category), EmptyToNull(row.AgeBracket), row.SourceJson ?? "{}",
                                EmptyToNull(row.FirstName), EmptyToNull(row.LastName), row.Age, EmptyToNull(row.Team), row.SourceRow, now, id);
                            updated++;
                        }
                    }
                    db.Execute("UPDATE events SET roster_revision=roster_revision+1,updated_at=?;", UtcNow());
                    Audit("roster_imported", GetEventId(), new { added = added, updated = updated, rows = rows.Count });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public void SetEventStatus(string status)
        {
            if (status != "open" && status != "closed" && status != "draft") throw new ArgumentException("Estado local no válido.");
            lock (sync)
            {
                string eventId = GetEventId();
                bool authority = db.ScalarInt64("SELECT has_authority FROM events LIMIT 1;") == 1;
                if (!authority && status == "open") throw new InvalidOperationException("La copia restaurada está en consulta. Elija explícitamente Tomar autoridad local antes de habilitar recepción.");
                if (status == "open" && (db.ScalarInt64("SELECT COUNT(*) FROM participants;") == 0 || db.ScalarInt64("SELECT COUNT(*) FROM distances;") == 0))
                    throw new InvalidOperationException("Prepare al menos una distancia y una nómina antes de habilitar recepción.");
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    db.Execute("UPDATE events SET status=?,updated_at=?;", status, UtcNow());
                    Audit("event_status_changed", eventId, new { status = status });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public LocalDevice RotateDeviceCredential()
        {
            string secret = NewCredential();
            string deviceId;
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    bool alreadyOwner = db.ScalarInt64("SELECT has_authority FROM events LIMIT 1;") == 1;
                    deviceId = alreadyOwner ? db.ScalarString("SELECT device_id FROM devices WHERE active=1 LIMIT 1;") : null;
                    if (String.IsNullOrWhiteSpace(deviceId))
                    {
                        deviceId = Guid.NewGuid().ToString("D");
                        db.Execute("UPDATE devices SET active=0;");
                        db.Execute("INSERT INTO devices(device_id,credential_hash,minimum_rssi,active,created_at) VALUES(?,?, -127,1,?);",
                            deviceId, LocalWorkspace.HashCredential(secret), UtcNow());
                    }
                    else db.Execute("UPDATE devices SET credential_hash=? WHERE device_id=?;", LocalWorkspace.HashCredential(secret), deviceId);
                    db.Execute("UPDATE events SET has_authority=1,status='draft',updated_at=?;", UtcNow());
                    Audit(alreadyOwner ? "reader_credential_rotated" : "local_authority_claimed", GetEventId(), new { device_id = deviceId });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
            return new LocalDevice { DeviceId = deviceId, Credential = secret, MinimumRssi = -127 };
        }

        internal void MarkRestoredForReview()
        {
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    db.Execute("UPDATE events SET has_authority=0,status='closed',updated_at=?;", UtcNow());
                    db.Execute("UPDATE devices SET active=0;");
                    Audit("backup_restored_for_review", GetEventId(), new { status = "closed" });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public bool AuthenticateDevice(string deviceId, string credential)
        {
            if (String.IsNullOrWhiteSpace(deviceId) || String.IsNullOrEmpty(credential)) return false;
            lock (sync)
            {
                Dictionary<string, object> row = db.Query("SELECT credential_hash FROM devices WHERE device_id=? AND active=1;", deviceId).FirstOrDefault();
                if (row == null) return false;
                byte[] expected = Encoding.ASCII.GetBytes(Text(row, "credential_hash") ?? "");
                byte[] actual = Encoding.ASCII.GetBytes(LocalWorkspace.HashCredential(credential));
                return ConstantTimeEquals(expected, actual);
            }
        }

        public IDictionary<string, object> GetReadiness(string eventId, string deviceId)
        {
            lock (sync)
            {
                LocalEvent race = GetSummary();
                string reason = null, message = "Receptor local listo.";
                bool canReceive = true;
                if (!String.Equals(eventId, race.EventId, StringComparison.OrdinalIgnoreCase)) { canReceive = false; reason = "event_not_found"; message = "El evento solicitado no está cargado en este receptor."; }
                else if (!HasDevice(deviceId)) { canReceive = false; reason = "invalid_device"; message = "El lector no está registrado para este evento."; }
                else if (!race.HasAuthority) { canReceive = false; reason = "authority_not_claimed"; message = "La copia restaurada requiere que el operador tome autoridad local."; }
                else if (race.Status != "open") { canReceive = false; reason = "event_closed"; message = race.Status == "draft" ? "Prepare y habilite recepción para este evento local." : "La recepción de este evento está cerrada."; }
                else if (race.ParticipantCount == 0) { canReceive = false; reason = "roster_empty"; message = "Cargue y revise la nómina antes de recibir lecturas."; }
                return new Dictionary<string, object>
                {
                    { "event_id", race.EventId }, { "device_id", deviceId }, { "event_status", race.Status },
                    { "can_receive", canReceive }, { "reason", reason }, { "message", message },
                    { "minimum_rssi", DeviceMinimumRssi(deviceId) }, { "policy_preset", "compat_actual_v1" },
                    { "roster_revision", db.ScalarInt64("SELECT roster_revision FROM events LIMIT 1;") },
                    { "receiver_version", "1.0" }
                };
            }
        }

        public LocalIngestResult Ingest(string json, IDictionary<string, object> payload)
        {
            string readingId = GetValue(payload, "reading_id");
            string deviceId = GetValue(payload, "device_id");
            string eventId = GetValue(payload, "event_id");
            string stage = (GetValue(payload, "stage") ?? "").ToLowerInvariant();
            string epc = GetValue(payload, "epc");
            string capturedText = GetValue(payload, "captured_at");
            long antennaLong, rssiLong, sequence;
            DateTimeOffset captured;
            Guid parsedReadingId, parsedDeviceId, parsedEventId;
            if (!Guid.TryParse(readingId, out parsedReadingId) || !Guid.TryParse(deviceId, out parsedDeviceId) || !Guid.TryParse(eventId, out parsedEventId) ||
                (stage != "start" && stage != "finish") || String.IsNullOrWhiteSpace(epc) || epc.Length > 128 ||
                !Int64.TryParse(GetValue(payload, "antenna"), NumberStyles.Integer, CultureInfo.InvariantCulture, out antennaLong) || antennaLong < 0 || antennaLong > 255 ||
                !Int64.TryParse(GetValue(payload, "rssi"), NumberStyles.Integer, CultureInfo.InvariantCulture, out rssiLong) || rssiLong < -128 || rssiLong > 0 ||
                !Int64.TryParse(GetValue(payload, "sequence"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence) || sequence < 0 ||
                !DateTimeOffset.TryParse(capturedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out captured))
                return Response(400, "invalid", "invalid_payload", "Payload de lectura incompleto o fuera de límites.", readingId, null);

            string canonicalChip = ChipIdNormalizer.Normalize(epc);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
            string payloadHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    Dictionary<string, object> previous = db.Query("SELECT payload_hash,status,response_json FROM ingest_receipts WHERE device_id=? AND reading_id=?;", deviceId, readingId).FirstOrDefault();
                    if (previous != null)
                    {
                        if (!String.Equals(Text(previous, "payload_hash"), payloadHash, StringComparison.Ordinal))
                        {
                            db.Execute("COMMIT;");
                            return Response(400, "invalid", "reading_id_payload_conflict", "El mismo reading_id llegó con otro payload.", readingId, null);
                        }
                        string original = Text(previous, "status");
                        db.Execute("COMMIT;");
                        return Response(200, "duplicate", null, "Lectura ya recibida.", readingId, original);
                    }

                    string status, reason = null, message;
                    string eventStatus = db.ScalarString("SELECT status FROM events WHERE event_id=?;", eventId);
                    if (!String.Equals(eventId, GetEventId(), StringComparison.OrdinalIgnoreCase)) { status = "invalid"; reason = "event_not_found"; message = "Evento no reconocido por el receptor local."; }
                    else if (!HasDevice(deviceId)) { status = "invalid_device"; reason = "invalid_device"; message = "Lector no registrado para este evento."; }
                    else if (eventStatus != "open") { status = "event_closed"; reason = eventStatus == "draft" ? "event_not_ready" : "event_closed"; message = eventStatus == "draft" ? "El evento está en preparación; habilite recepción local." : "La recepción del evento está cerrada."; }
                    else if (!GetSummary().HasAuthority) { status = "event_closed"; reason = "authority_not_claimed"; message = "La copia restaurada requiere autoridad local."; }
                    else if (rssiLong < DeviceMinimumRssi(deviceId)) { status = "below_threshold"; reason = "below_threshold"; message = "La lectura está bajo el umbral de RSSI del lector."; }
                    else
                    {
                        Dictionary<string, object> participant = db.Query("SELECT participant_id FROM participants WHERE chip=? COLLATE NOCASE LIMIT 1;", canonicalChip).FirstOrDefault();
                        if (participant == null) { status = "unknown_chip"; reason = "unknown_chip"; message = "El chip no está asignado a la nómina."; }
                        else
                        {
                            string participantId = Text(participant, "participant_id");
                            Dictionary<string, object> current = db.Query("SELECT kind,captured_at FROM timing_marks WHERE participant_id=? AND stage=?;", participantId, stage).FirstOrDefault();
                            if (current != null && Text(current, "kind") == "manual_set")
                            { status = "manual_override"; reason = "manual_override"; message = "La marca vigente fue corregida manualmente."; }
                            else if (current != null && DateTimeOffset.Parse(Text(current, "captured_at"), CultureInfo.InvariantCulture).ToUniversalTime() <= captured.ToUniversalTime())
                            { status = "later_than_current"; reason = "later_than_current"; message = "Ya existe una lectura RFID anterior para esta etapa."; }
                            else
                            {
                                status = "accepted"; message = "Lectura guardada localmente.";
                                InsertOperation(deviceId + ":" + readingId, participantId, stage, "rfid_accepted", captured,
                                    readingId, (int)antennaLong, (int)rssiLong, null);
                                string operationId = deviceId + ":" + readingId;
                                db.Execute("INSERT INTO timing_marks(participant_id,stage,operation_id,captured_at,kind,antenna,rssi) VALUES(?,?,?,?,'rfid_accepted',?,?) ON CONFLICT(participant_id,stage) DO UPDATE SET operation_id=excluded.operation_id,captured_at=excluded.captured_at,kind=excluded.kind,antenna=excluded.antenna,rssi=excluded.rssi;",
                                    participantId, stage, operationId, captured.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), (int)antennaLong, (int)rssiLong);
                                db.Execute("UPDATE events SET timing_revision=timing_revision+1,updated_at=?;", UtcNow());
                            }
                        }
                    }
                    string response = BuildResponse(status, reason, message, readingId, null);
                    db.Execute("INSERT INTO ingest_receipts(device_id,reading_id,payload_json,payload_hash,status,response_json,received_at) VALUES(?,?,?,?,?,?,?);",
                        deviceId, readingId, json, payloadHash, status, response, UtcNow());
                    Audit("ingest_" + status, readingId, new { event_id = eventId, device_id = deviceId, stage = stage, epc = canonicalChip, reason = reason });
                    db.Execute("COMMIT;");
                    int httpStatus = status == "event_closed" ? 409 : status == "invalid" ? 400 : 200;
                    return new LocalIngestResult { HttpStatus = httpStatus, Status = status, Reason = reason, Message = message, ResponseJson = response };
                }
                catch { Rollback(); throw; }
            }
        }

        public void SetOfficialStart(string distance, DateTimeOffset startAt, string reason)
        {
            string key = ResultDistanceKey.Normalize(distance);
            if (key == null) throw new ArgumentException("Distancia no válida.");
            lock (sync)
            {
                EnsureWritable();
                if (db.ScalarString("SELECT status FROM events LIMIT 1;") != "open")
                    throw new InvalidOperationException("Habilite la recepción local antes de registrar la salida oficial.");
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    IList<Dictionary<string, object>> rows = db.Query("SELECT participant_id,distance FROM participants;")
                        .Where(row => ResultDistanceKey.Normalize(Text(row, "distance")) == key).ToList();
                    if (rows.Count == 0) throw new InvalidOperationException("No hay participantes en la distancia " + distance + ".");
                    foreach (Dictionary<string, object> row in rows)
                        InsertManualMark(Text(row, "participant_id"), "start", startAt, reason ?? "Salida oficial por distancia.");
                    db.Execute("UPDATE distances SET scheduled_start_utc=?,official_started=1 WHERE distance_key=?;",
                        startAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), key);
                    db.Execute("UPDATE events SET timing_revision=timing_revision+1,updated_at=?;", UtcNow());
                    Audit("official_distance_start", key, new { participants = rows.Count, captured_at = startAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), reason = reason });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public void SetOfficialStartForCategory(string distance, string category, DateTimeOffset startAt, string reason)
        {
            string key = ResultDistanceKey.Normalize(distance);
            string categoryName = EmptyToNull(category);
            if (key == null) throw new ArgumentException("Distancia no válida.");
            if (categoryName == null) throw new ArgumentException("Categoría no válida.");
            lock (sync)
            {
                EnsureWritable();
                if (db.ScalarString("SELECT status FROM events LIMIT 1;") != "open")
                    throw new InvalidOperationException("Habilite la recepción local antes de registrar la salida oficial.");
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    IList<Dictionary<string, object>> rows = db.Query("SELECT participant_id,distance,category FROM participants;")
                        .Where(row => ResultDistanceKey.Normalize(Text(row, "distance")) == key &&
                            String.Equals((Text(row, "category") ?? "").Trim(), categoryName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (rows.Count == 0) throw new InvalidOperationException("No hay participantes de la categoría " + categoryName + " en " + distance + ".");
                    foreach (Dictionary<string, object> row in rows)
                        InsertManualMark(Text(row, "participant_id"), "start", startAt, reason ?? "Salida oficial por categoría.");
                    db.Execute("UPDATE events SET timing_revision=timing_revision+1,updated_at=?;", UtcNow());
                    Audit("official_category_start", key + ":" + categoryName, new
                    {
                        distance = distance,
                        category = categoryName,
                        participants = rows.Count,
                        captured_at = startAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        reason = reason
                    });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public void SetManualMark(string participantId, string stage, DateTimeOffset capturedAt, string reason)
        {
            if (stage != "start" && stage != "finish") throw new ArgumentException("Etapa no válida.");
            lock (sync)
            {
                EnsureWritable();
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    InsertManualMark(participantId, stage, capturedAt, reason);
                    db.Execute("UPDATE events SET timing_revision=timing_revision+1,updated_at=?;", UtcNow());
                    Audit("manual_mark_set", participantId, new { stage = stage, captured_at = capturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), reason = reason });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public void SetDnf(string participantId, bool dnf, string reason)
        {
            lock (sync)
            {
                EnsureWritable();
                string value = dnf ? UtcNow() : null;
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    db.Execute("UPDATE participants SET dnf_at=?,updated_at=? WHERE participant_id=?;", value, UtcNow(), participantId);
                    db.Execute("UPDATE events SET roster_revision=roster_revision+1,updated_at=?;", UtcNow());
                    Audit(dnf ? "participant_dnf" : "participant_dnf_cleared", participantId, new { reason = reason });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        public ResultSnapshot CalculateResults()
        {
            ResultCalculationInput input;
            HashSet<string> excludedDistances;
            lock (sync)
            {
                Dictionary<string, object> eventRow = db.Query("SELECT event_id,roster_revision,timing_revision,policy_revision FROM events LIMIT 1;").First();
                var participants = new List<ResultParticipant>();
                foreach (Dictionary<string, object> row in db.Query("SELECT participant_id,bib,chip,distance,sex,category,age_bracket,dnf_at FROM participants;"))
                {
                    DateTimeOffset? dnf = null;
                    if (Text(row, "dnf_at") != null) dnf = DateTimeOffset.Parse(Text(row, "dnf_at"), CultureInfo.InvariantCulture).ToUniversalTime();
                    participants.Add(new ResultParticipant(Text(row, "participant_id"), Text(row, "bib"), Text(row, "chip"),
                        Text(row, "distance"), Text(row, "sex"), Text(row, "category"), Text(row, "age_bracket"), dnf));
                }
                var operations = new List<TimingOperation>();
                foreach (Dictionary<string, object> row in db.Query("SELECT operation_id,participant_id,stage,kind,operation_order,captured_at FROM timing_operations ORDER BY operation_order;"))
                {
                    DateTimeOffset? timestamp = Text(row, "captured_at") == null ? (DateTimeOffset?)null : DateTimeOffset.Parse(Text(row, "captured_at"), CultureInfo.InvariantCulture).ToUniversalTime();
                    operations.Add(new TimingOperation(Text(row, "operation_id"), Text(row, "participant_id"),
                        Text(row, "stage") == "start" ? TimingStage.Start : TimingStage.Finish,
                        Text(row, "kind") == "manual_set" ? TimingOperationKind.ManualSet :
                        Text(row, "kind") == "manual_voided" ? TimingOperationKind.ManualVoided : TimingOperationKind.RfidAccepted,
                        Number(row, "operation_order"), timestamp));
                }
                input = new ResultCalculationInput(Text(eventRow, "event_id"),
                    "roster-" + Number(eventRow, "roster_revision").ToString(CultureInfo.InvariantCulture),
                    "timing-" + Number(eventRow, "timing_revision").ToString(CultureInfo.InvariantCulture), participants, operations);
                excludedDistances = new HashSet<string>(db.Query("SELECT distance_key FROM distances WHERE award_policy_configured=1 AND exclude_general_top3_from_categories=1;")
                    .Select(row => Text(row, "distance_key")), StringComparer.Ordinal);
            }
            ResultSnapshot snapshot = new ResultsEngine().Calculate(input, ResultPolicy.CreateCompatActualV1(), excludedDistances);
            PersistSnapshot(snapshot);
            return snapshot;
        }

        public void Checkpoint()
        {
            lock (sync) db.Execute("PRAGMA wal_checkpoint(FULL);");
        }

        internal void BackupTo(string destinationPath)
        {
            lock (sync) db.BackupTo(destinationPath);
        }

        public void Dispose()
        {
            lock (sync)
            {
                db.Dispose();
                if (leaseHeld) { ReleaseLease(path); leaseHeld = false; }
            }
        }

        private void InsertManualMark(string participantId, string stage, DateTimeOffset at, string reason)
        {
            string operationId = Guid.NewGuid().ToString("D");
            DateTimeOffset utc = at.ToUniversalTime();
            InsertOperation(operationId, participantId, stage, "manual_set", utc, null, null, null, reason);
            db.Execute("INSERT OR REPLACE INTO timing_marks(participant_id,stage,operation_id,captured_at,kind,antenna,rssi) VALUES(?,?,?,?,'manual_set',NULL,NULL);",
                participantId, stage, operationId, utc.ToString("o", CultureInfo.InvariantCulture));
        }

        private void PersistSnapshot(ResultSnapshot snapshot)
        {
            string snapshotJson = new JavaScriptSerializer { MaxJsonLength = 50 * 1024 * 1024 }.Serialize(snapshot);
            lock (sync)
            {
                db.Execute("BEGIN IMMEDIATE;");
                try
                {
                    db.Execute("INSERT OR REPLACE INTO result_snapshots(snapshot_id,roster_revision,timing_revision,policy_preset,policy_revision,engine_version,snapshot_json,created_at) VALUES(?,?,?,?,?,?,?,?);",
                        Guid.NewGuid().ToString("D"), snapshot.RosterRevision, snapshot.TimingRevision, snapshot.PresetId,
                        snapshot.PolicyRevision, snapshot.EngineVersion, snapshotJson, UtcNow());
                    Audit("result_snapshot_calculated", snapshot.EventId, new { roster_revision = snapshot.RosterRevision, timing_revision = snapshot.TimingRevision, engine_version = snapshot.EngineVersion });
                    db.Execute("COMMIT;");
                }
                catch { Rollback(); throw; }
            }
        }

        private void InsertOperation(string operationId, string participantId, string stage, string kind,
            DateTimeOffset? capturedAt, string sourceReadingId, int? antenna, int? rssi, string reason)
        {
            db.Execute("INSERT INTO timing_operations(operation_id,participant_id,stage,kind,captured_at,source_reading_id,antenna,rssi,reason,created_at) VALUES(?,?,?,?,?,?,?,?,?,?);",
                operationId, participantId, stage, kind, capturedAt.HasValue ? capturedAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null,
                sourceReadingId, antenna, rssi, reason, UtcNow());
        }

        private string GetEventId() { return db.ScalarString("SELECT event_id FROM events LIMIT 1;"); }
        private object TransferAge(IDictionary<string, object> participant)
        {
            int age;
            string savedAge = Text(participant, "web_age");
            if (Int32.TryParse(savedAge, NumberStyles.Integer, CultureInfo.InvariantCulture, out age) && age >= 0 && age <= 130)
                return age;
            Dictionary<string, object> source = ParseObject(Text(participant, "source_json"));
            foreach (KeyValuePair<string, object> field in source)
            {
                string key = field.Key.ToLowerInvariant().Normalize(NormalizationForm.FormD);
                var normalized = new StringBuilder();
                foreach (char character in key)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                    if (Char.IsLetterOrDigit(character)) normalized.Append(character);
                }
                if (!new[] { "age", "edad", "ageyears", "edadanos" }.Contains(normalized.ToString())) continue;
                if (Int32.TryParse(Convert.ToString(field.Value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out age) && age >= 0 && age <= 130) return age;
            }
            return null;
        }
        private bool HasDevice(string id) { return db.ScalarInt64("SELECT COUNT(*) FROM devices WHERE device_id=? AND active=1;", id) > 0; }
        private int DeviceMinimumRssi(string id) { return (int)db.ScalarInt64("SELECT minimum_rssi FROM devices WHERE device_id=? AND active=1;", id); }
        private void EnsureWritable()
        {
            if (db.ScalarInt64("SELECT has_authority FROM events LIMIT 1;") != 1)
                throw new InvalidOperationException("La copia está en consulta. Tome autoridad local para editarla.");
        }
        private static void AcquireLease(string databasePath)
        {
            lock (LeaseSync)
            {
                LeaseEntry entry;
                if (Leases.TryGetValue(databasePath, out entry)) { entry.References++; return; }
                string lockPath = databasePath + ".writer.lock";
                FileStream stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Leases.Add(databasePath, new LeaseEntry { LockFile = stream, References = 1 });
            }
        }
        private static void ReleaseLease(string databasePath)
        {
            lock (LeaseSync)
            {
                LeaseEntry entry;
                if (!Leases.TryGetValue(databasePath, out entry)) return;
                entry.References--;
                if (entry.References > 0) return;
                entry.LockFile.Dispose();
                Leases.Remove(databasePath);
            }
        }
        private void Audit(string action, string entityId, object details)
        {
            db.Execute("INSERT INTO audit_log(action,entity_id,details_json,created_at) VALUES(?,?,?,?);",
                action, entityId, details == null ? "{}" : serializer.Serialize(details), UtcNow());
        }
        private void Rollback() { try { db.Rollback(); } catch { } }
        private static string UtcNow() { return DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
        private static string EmptyToNull(string value) { return String.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
        private object NullableNumber(IDictionary<string, object> row, string key)
        {
            object value;
            if (row == null || !row.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        private Dictionary<string, object> ParseObject(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return new Dictionary<string, object>();
            return serializer.Deserialize<Dictionary<string, object>>(value) ?? new Dictionary<string, object>();
        }
        private static string Text(IDictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
        private static long Number(IDictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;
        }
        private static string GetValue(IDictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }
        private static bool Same(string left, string right) { return String.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase); }
        private static LocalIngestResult Response(int httpStatus, string status, string reason, string message, string readingId, string original)
        {
            string json = BuildResponse(status, reason, message, readingId, original);
            return new LocalIngestResult { HttpStatus = httpStatus, Status = status, OriginalStatus = original, Reason = reason, Message = message, ResponseJson = json };
        }
        private static string BuildResponse(string status, string reason, string message, string readingId, string original)
        {
            var values = new Dictionary<string, object> { { "status", status }, { "reason", reason }, { "message", message }, { "reading_id", readingId } };
            if (!String.IsNullOrWhiteSpace(original)) values["original_status"] = original;
            return new JavaScriptSerializer().Serialize(values);
        }

        private static string NewCredential()
        {
            byte[] secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            return Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
