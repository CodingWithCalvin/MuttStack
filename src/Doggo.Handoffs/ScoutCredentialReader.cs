using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Doggo;

public static class ScoutCredentialReader
{
    public static (string Key, string? Pin) ReadTvdb(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Episcout");
        var root = folder;
        var settingsFolder = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Equals("UI-Trial", StringComparison.OrdinalIgnoreCase);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
        if (settingsFolder && parent != null && (parent.Equals(ScoutStorageSelection.DefaultRoot, StringComparison.OrdinalIgnoreCase) || parent.Equals(legacy, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(ScoutStorageSelection.ConfigPath(parent)) || File.Exists(ScoutStorageSelection.PendingPath(parent)) || File.Exists(Path.Combine(parent, "scout.db")))) root = parent;
        if (root.Equals(legacy, StringComparison.OrdinalIgnoreCase)) root = ScoutStorageSelection.DefaultRoot;
        using var configLease = StorageLease.Acquire(false, ScoutStorageSelection.ConfigPath(root));
        var choice = ScoutStorageSelection.Resolve(root, settingsFolder ? folder : Path.Combine(folder, "UI-Trial"), Environment.GetEnvironmentVariable("SCOUT_DOGGO_DATABASE"), Environment.GetEnvironmentVariable("SCOUT_SETTINGS"));
        var strict = choice.UsesDoggo || File.Exists(ScoutStorageSelection.ConfigPath(root));
        if (!strict && !File.Exists(choice.DatabasePath) && File.Exists(Path.Combine(folder, "scout.db")))
            choice = choice with { DatabasePath = Path.Combine(folder, "scout.db") };
        using var databaseLease = StorageLease.Acquire(false, choice.DatabasePath);
        foreach (var encrypted in Blobs(root, folder, choice))
        {
            var input = new Blob { Size = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) }; Blob output = default;
            byte[]? plain = null;
            try
            {
                Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                {
                    if (strict) throw new CryptographicException("Windows could not unlock Scout's selected credentials.");
                    continue;
                }
                plain = new byte[output.Size]; Marshal.Copy(output.Data, plain, 0, plain.Length);
                var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
                if (keys?.TryGetValue("metadata|tvdb", out var key) == true && !string.IsNullOrWhiteSpace(key))
                    return (key, keys.GetValueOrDefault("metadata|tvdb-pin"));
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                Marshal.Copy(new byte[encrypted.Length], 0, input.Data, encrypted.Length); Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
            }
        }
        throw new IOException("No TVDB key is configured in Scout's selected database. Save it in Scout Settings; older databases are not used after a storage switch.");
    }
    static IEnumerable<byte[]> Blobs(string root, string legacyFolder, ScoutStorageChoice choice)
    {
        var selected = choice.UsesDoggo || File.Exists(ScoutStorageSelection.ConfigPath(root));
        if (File.Exists(choice.DatabasePath))
        {
            using var db = new Sqlite(choice.DatabasePath, readOnly: true);
            if (choice.UsesDoggo)
            {
                foreach (var component in new[] { "doggo", "scout" })
                    if (db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component=?", component)[0][0] != "1")
                        throw new InvalidDataException("Scout's selected database schema is not supported by this app.");
            }
            var table = choice.UsesDoggo ? "scout_documents" : "documents";
            // A persisted selection has one authoritative credential document. Deleted keys cannot fall back to old copies.
            var rows = selected
                ? db.Query($"SELECT hex(content) FROM {table} WHERE kind='credentials.dpapi' AND path=? COLLATE NOCASE AND length(content) BETWEEN 1 AND 1048576", choice.CredentialPath)
                : db.Query($"SELECT hex(content) FROM {table} WHERE kind='credentials.dpapi' AND length(content) BETWEEN 1 AND 1048576 ORDER BY path");
            foreach (var row in rows) yield return Convert.FromHexString(row[0]);
            if (selected) yield break;
        }
        if (selected) throw new FileNotFoundException("Scout's selected credential database is unavailable.");
        foreach (var path in new[] { Path.Combine(legacyFolder, "credentials.dpapi"), Path.Combine(legacyFolder, "UI-Trial", "credentials.dpapi") })
            if (File.Exists(path)) yield return File.ReadAllBytes(path);
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
