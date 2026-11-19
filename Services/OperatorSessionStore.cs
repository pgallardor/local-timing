using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using RFIDLocal.Domain;

namespace RFIDLocal.Services
{
    public sealed class OperatorSessionStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RFIDLocalRace operator session v1");
        private readonly string path;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public OperatorSessionStore()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RFIDLocalRace", "OperatorSession.dat")) { }

        public OperatorSessionStore(string path) { this.path = path; }

        public OperatorSession Load()
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] protectedData = File.ReadAllBytes(path);
                string json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser));
                OperatorSession session = serializer.Deserialize<OperatorSession>(json);
                return session != null && !String.IsNullOrWhiteSpace(session.PlatformUrl) &&
                    !String.IsNullOrWhiteSpace(session.RefreshToken) ? session : null;
            }
            catch { return null; }
        }

        public void Save(OperatorSession session)
        {
            if (session == null || String.IsNullOrWhiteSpace(session.RefreshToken)) throw new ArgumentException("La sesión renovable está incompleta.", "session");
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            byte[] clear = Encoding.UTF8.GetBytes(serializer.Serialize(session));
            byte[] protectedData = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedData, 0, protectedData.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public void Clear()
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
