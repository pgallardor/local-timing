using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RFIDLocal.Infrastructure
{
    // Windows 10+ ships winsqlite3.dll. This small wrapper keeps the local app
    // independent of NuGet/native packages and uses SQLite's transactional backup API.
    internal sealed class NativeSqlite : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private const int OpenReadWrite = 0x00000002;
        private const int OpenCreate = 0x00000004;
        private const int OpenFullMutex = 0x00010000;
        private static readonly IntPtr Transient = new IntPtr(-1);
        private IntPtr handle;

        public NativeSqlite(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            int rc = sqlite3_open_v2(Utf8Z(path), out handle,
                OpenReadWrite | OpenCreate | OpenFullMutex, IntPtr.Zero);
            if (rc != Ok)
            {
                string message = ErrorMessage;
                Dispose();
                throw new IOException("No se pudo abrir SQLite: " + message);
            }
            sqlite3_busy_timeout(handle, 5000);
        }

        public string ErrorMessage
        {
            get { return handle == IntPtr.Zero ? "SQLite no abrió la base." : PtrToUtf8(sqlite3_errmsg(handle)); }
        }

        public void Execute(string sql, params object[] values)
        {
            using (Statement statement = Prepare(sql))
            {
                BindAll(statement.Handle, values);
                int rc;
                while ((rc = sqlite3_step(statement.Handle)) == Row) { }
                if (rc != Done) Throw(rc);
            }
        }

        public void ExecuteScript(string sql)
        {
            IntPtr error;
            int rc = sqlite3_exec(handle, Utf8Z(sql), IntPtr.Zero, IntPtr.Zero, out error);
            if (rc == Ok) return;
            string message = error == IntPtr.Zero ? ErrorMessage : PtrToUtf8(error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            throw new IOException("Error SQLite " + rc + ": " + message);
        }

        public IList<Dictionary<string, object>> Query(string sql, params object[] values)
        {
            var rows = new List<Dictionary<string, object>>();
            using (Statement statement = Prepare(sql))
            {
                BindAll(statement.Handle, values);
                int rc;
                while ((rc = sqlite3_step(statement.Handle)) == Row)
                {
                    int count = sqlite3_column_count(statement.Handle);
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int index = 0; index < count; index++)
                    {
                        string name = PtrToUtf8(sqlite3_column_name(statement.Handle, index));
                        int type = sqlite3_column_type(statement.Handle, index);
                        object value;
                        if (type == 1) value = sqlite3_column_int64(statement.Handle, index);
                        else if (type == 2) value = sqlite3_column_double(statement.Handle, index);
                        else if (type == 3)
                        {
                            IntPtr pointer = sqlite3_column_text(statement.Handle, index);
                            int length = sqlite3_column_bytes(statement.Handle, index);
                            byte[] buffer = new byte[length];
                            if (length > 0) Marshal.Copy(pointer, buffer, 0, length);
                            value = Encoding.UTF8.GetString(buffer);
                        }
                        else if (type == 4)
                        {
                            IntPtr pointer = sqlite3_column_blob(statement.Handle, index);
                            int length = sqlite3_column_bytes(statement.Handle, index);
                            byte[] buffer = new byte[length];
                            if (length > 0) Marshal.Copy(pointer, buffer, 0, length);
                            value = buffer;
                        }
                        else value = null;
                        row[name] = value;
                    }
                    rows.Add(row);
                }
                if (rc != Done) Throw(rc);
            }
            return rows;
        }

        public string ScalarString(string sql, params object[] values)
        {
            IList<Dictionary<string, object>> rows = Query(sql, values);
            if (rows.Count == 0 || rows[0].Count == 0 || rows[0].Values.FirstOrDefault() == null) return null;
            return Convert.ToString(rows[0].Values.FirstOrDefault());
        }

        public long ScalarInt64(string sql, params object[] values)
        {
            string result = ScalarString(sql, values);
            long value;
            return Int64.TryParse(result, out value) ? value : 0;
        }

        public void Begin() { Execute("BEGIN IMMEDIATE;"); }
        public void Commit() { Execute("COMMIT;"); }
        public void Rollback() { Execute("ROLLBACK;"); }

        public void BackupTo(string destinationPath)
        {
            using (var target = new NativeSqlite(destinationPath))
            {
                IntPtr backup = sqlite3_backup_init(target.handle, Utf8Z("main"), handle, Utf8Z("main"));
                if (backup == IntPtr.Zero) throw new IOException("No se pudo iniciar el respaldo SQLite: " + target.ErrorMessage);
                int stepResult = sqlite3_backup_step(backup, -1);
                int finishResult = sqlite3_backup_finish(backup);
                if ((stepResult != Done && stepResult != Ok) || finishResult != Ok)
                    throw new IOException("No se pudo completar el respaldo SQLite: " + target.ErrorMessage);
                target.Execute("PRAGMA journal_mode=DELETE;");
            }
        }

        private Statement Prepare(string sql)
        {
            IntPtr statement;
            int rc = sqlite3_prepare_v2(handle, Utf8Z(sql), -1, out statement, IntPtr.Zero);
            if (rc != Ok) Throw(rc);
            return new Statement(this, statement);
        }

        private static void BindAll(IntPtr statement, object[] values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                object value = values[i];
                int rc;
                if (value == null || value == DBNull.Value) rc = sqlite3_bind_null(statement, i + 1);
                else if (value is bool) rc = sqlite3_bind_int(statement, i + 1, (bool)value ? 1 : 0);
                else if (value is byte || value is short || value is int) rc = sqlite3_bind_int(statement, i + 1, Convert.ToInt32(value));
                else if (value is long) rc = sqlite3_bind_int64(statement, i + 1, (long)value);
                else if (value is byte[])
                {
                    byte[] bytes = (byte[])value;
                    rc = sqlite3_bind_blob(statement, i + 1, bytes, bytes.Length, Transient);
                }
                else
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Convert.ToString(value) ?? "");
                    rc = sqlite3_bind_text(statement, i + 1, bytes, bytes.Length, Transient);
                }
                if (rc != Ok) throw new IOException("No se pudo preparar un valor SQLite (" + rc + ").");
            }
        }

        private void Throw(int rc)
        {
            throw new IOException("Error SQLite " + rc + ": " + ErrorMessage);
        }

        private static byte[] Utf8Z(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private static string PtrToUtf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            byte[] bytes = new byte[length];
            if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            sqlite3_close_v2(handle);
            handle = IntPtr.Zero;
        }

        internal sealed class Statement : IDisposable
        {
            private readonly NativeSqlite owner;
            public IntPtr Handle { get; private set; }
            public Statement(NativeSqlite owner, IntPtr handle) { this.owner = owner; Handle = handle; }
            public void Dispose()
            {
                if (Handle == IntPtr.Zero) return;
                int rc = sqlite3_finalize(Handle);
                Handle = IntPtr.Zero;
                if (rc != Ok && rc != Done) owner.Throw(rc);
            }
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
        private static extern void sqlite3_free(IntPtr pointer);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
        private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int")]
        private static extern int sqlite3_bind_int(IntPtr statement, int index, int value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
        private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_double")]
        private static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_blob")]
        private static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_count")]
        private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_name")]
        private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
        private static extern double sqlite3_column_double(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_blob")]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
        private static extern IntPtr sqlite3_backup_init(IntPtr destination, byte[] destinationName, IntPtr source, byte[] sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
        private static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
        private static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
