using System.IO;
namespace Tucker;
public static class AppStorage { public static string Prepare(string local) { var path=Path.Combine(local,"Tucker");Directory.CreateDirectory(path);return path; } }
public static class DataFiles
{
    public const string DiscMetadata = "ripley-disc.json";
    public const string Sidecar = ".ripley.json";
    public const string Sessions = ".ripley-rips";
    // Existing user media is never renamed just because the application was renamed.
    public static readonly string[] SessionFolders = [Sessions, ".reel-rips"];
    public static readonly string[] Sidecars = [Sidecar, ".reel.json"];
    public static string FindDiscMetadata(string folder) => new[] { DiscMetadata, "reel-disc.json" }
        .Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists) ?? Path.Combine(folder, DiscMetadata);
}

