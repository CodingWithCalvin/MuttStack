namespace Doggo;

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
