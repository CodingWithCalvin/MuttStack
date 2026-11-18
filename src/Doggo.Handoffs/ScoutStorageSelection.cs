using System.Text.Json;

namespace Doggo;

public sealed record ScoutStorageChoice(int Version, string Mode, string DatabasePath,
    string SettingsRoot, string WorkspacesRoot, string? CutoverId = null)
{
    public bool UsesDoggo => Mode == "Doggo";
    public string CredentialPath => Path.Combine(SettingsRoot, "credentials.dpapi");
}

// Bootstrap information stays outside either database so every launch can find it.
public static class ScoutStorageSelection
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scout");
    public static string ConfigPath(string root) => Path.Combine(Path.GetFullPath(root), "storage.json");
    public static string PendingPath(string root) => Path.Combine(Path.GetFullPath(root), "storage.pending.json");
    public static ScoutStorageChoice Resolve(string root, string? legacySettingsRoot = null, string? doggoOverride = null,
        string? settingsOverride = null, string? workspacesOverride = null)
    {
        root = Path.GetFullPath(root);
        if (File.Exists(PendingPath(root))) throw new IOException("Scout's database switch is unfinished. Use Scout.DatabaseCutover recover before opening Scout or reading its credentials.");
        var config = ConfigPath(root);
        ScoutStorageChoice choice;
        if (File.Exists(config))
        {
            try { choice = JsonSerializer.Deserialize<ScoutStorageChoice>(File.ReadAllBytes(config)) ?? throw new InvalidDataException("Empty Scout storage selection."); }
            catch (JsonException ex) { throw new InvalidDataException("Scout storage selection is unreadable. Existing databases were kept.", ex); }
            Validate(choice);
        }
        else
        {
            var settings = legacySettingsRoot ?? Path.Combine(root, "UI-Trial");
            choice = new(1, "Standalone", Path.Combine(root, "scout.db"), settings, Path.Combine(settings, "Workspaces"));
        }
        // Environment overrides remain process-local for isolated tests; never persist them.
        if (doggoOverride != null)
        {
            choice = choice with { Mode = "Doggo", DatabasePath = doggoOverride, CutoverId = null };
            Validate(choice);
        }
        choice = choice with { SettingsRoot = settingsOverride ?? choice.SettingsRoot, WorkspacesRoot = workspacesOverride ?? choice.WorkspacesRoot };
        if (!Path.IsPathFullyQualified(choice.SettingsRoot) || !Path.IsPathFullyQualified(choice.WorkspacesRoot))
            throw new InvalidDataException("Scout settings and workspace roots must be absolute paths.");
        return choice;
    }
    public static void Validate(ScoutStorageChoice choice)
    {
        if (choice.Version != 1 || choice.Mode is not ("Standalone" or "Doggo") ||
            string.IsNullOrWhiteSpace(choice.DatabasePath) || !Path.IsPathFullyQualified(choice.DatabasePath) ||
            string.IsNullOrWhiteSpace(choice.SettingsRoot) || !Path.IsPathFullyQualified(choice.SettingsRoot) ||
            string.IsNullOrWhiteSpace(choice.WorkspacesRoot) || !Path.IsPathFullyQualified(choice.WorkspacesRoot))
            throw new InvalidDataException("Unsupported or invalid Scout storage selection.");
        if (!File.Exists(choice.DatabasePath)) throw new FileNotFoundException("Scout's selected database is unavailable. Restore access; Scout will not create a replacement or use the old database.", choice.DatabasePath);
    }
    public static void Save(string root, ScoutStorageChoice choice)
    {
        Validate(choice);
        AtomicWrite(ConfigPath(root), JsonSerializer.SerializeToUtf8Bytes(choice, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Updated apps hold shared leases for their lifetime; maintenance requires all of them.
public sealed class StorageLease : IDisposable
{
    readonly List<FileStream> files = [];
    StorageLease() { }
    public static StorageLease Acquire(bool exclusive, params string[] resources)
    {
        var lease = new StorageLease();
        try
        {
            foreach (var resource in resources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resource)!);
                lease.files.Add(new FileStream(resource + ".access", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    exclusive ? FileShare.None : FileShare.ReadWrite));
            }
            return lease;
        }
        catch (IOException ex) { lease.Dispose(); throw new IOException("Database storage is in use or being switched. Close the affected Doggo apps before maintenance, or finish recovery before reopening them.", ex); }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose() { foreach (var file in files) file.Dispose(); files.Clear(); }
}
