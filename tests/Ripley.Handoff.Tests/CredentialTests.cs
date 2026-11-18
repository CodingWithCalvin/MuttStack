using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Doggo;

static class CredentialTests
{
    public static int Run(string parent)
    {
        var passed = 0;
        foreach (var (name, read) in new (string, Func<string, (string Key, string? Pin)>)[] { ("Ripley", Ripley.ScoutCredentials.Read), ("Tucker", Tucker.ScoutCredentials.Read) })
        {
            var root = Path.Combine(parent, name + "-credentials"); Directory.CreateDirectory(root);
            var source = Path.Combine(root, "scout.db"); var target = Path.Combine(root, "doggo.db"); var settings = Path.Combine(root, "UI-Trial");
            void Check(bool yes, string label) { if (!yes) throw new Exception(label); passed++; Console.WriteLine("PASS " + name + " " + label); }
            void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException) { Check(true, label); return; } throw new Exception(label); }
            void Key(string path, string table, string? value, string? pin = null)
            {
                var keys = new Dictionary<string, string>(); if (value != null) keys["metadata|tvdb"] = value; if (pin != null) keys["metadata|tvdb-pin"] = pin;
                var encrypted = Protect(JsonSerializer.SerializeToUtf8Bytes(keys));
                using var db = new Sqlite(path);
                db.Exec($"INSERT OR REPLACE INTO {table} VALUES(?,'credentials.dpapi',X'{Convert.ToHexString(encrypted)}')", Path.Combine(settings, "credentials.dpapi"));
            }
            using (var db = new Sqlite(source)) db.Exec("CREATE TABLE documents(path TEXT PRIMARY KEY,kind TEXT,content BLOB)");
            Key(source, "documents", "legacy-test-key", "test-pin");
            Check(read(root) == ("legacy-test-key", "test-pin"), "retains legacy credential compatibility before a switch");
            _ = new DoggoInbox(target);
            using (var db = new Sqlite(target)) { db.Exec("CREATE TABLE scout_documents(path TEXT PRIMARY KEY,kind TEXT,content BLOB)"); db.Exec("INSERT INTO doggo_migrations VALUES('scout',1,'test')"); }
            Key(target, "scout_documents", "shared-test-key");
            ScoutStorageSelection.Save(root, new(1, "Doggo", target, settings, Path.Combine(settings, "Workspaces")));
            var before = SHA256.HashData(File.ReadAllBytes(target));
            Check(read(root) == ("shared-test-key", null) && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))), "reads persisted Doggo credentials without changing the database");
            Check(read(settings) == ("shared-test-key", null), "resolves older UI-Trial folder preferences through the same persistent selection");
            Key(target, "scout_documents", "rotated-test-key", "new-pin");
            Check(read(root) == ("rotated-test-key", "new-pin"), "sees rotated keys and PINs without restarting or caching stale credentials");
            Key(target, "scout_documents", null);
            Reject(() => read(root), "does not fall back to legacy keys after removal");
            Key(target, "scout_documents", "shared-test-key");
            File.WriteAllText(ScoutStorageSelection.PendingPath(root), "incomplete");
            Reject(() => read(root), "refuses credentials while a cutover needs recovery"); File.Delete(ScoutStorageSelection.PendingPath(root));
            using (StorageLease.Acquire(true, target)) Reject(() => read(root), "waits for exclusive database maintenance");
            File.Move(target, target + ".offline"); Reject(() => read(root), "refuses an unavailable selected database instead of using a stale key"); File.Move(target + ".offline", target);
            using (var db = new Sqlite(target)) db.Exec("INSERT INTO doggo_migrations VALUES('scout',2,'future')");
            Reject(() => read(root), "rejects unsupported selected credential schemas");
        }
        return passed;
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, [MarshalAs(UnmanagedType.LPWStr)] string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Protect(byte[] bytes)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (!CryptProtectData(ref input, "isolated test", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)) throw new CryptographicException("Test credential protection failed.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); CryptographicOperations.ZeroMemory(bytes); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
