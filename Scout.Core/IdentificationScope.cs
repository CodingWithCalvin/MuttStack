using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scout.Core;

// Describes the user's inventory request, never a restriction on matching candidates.
public sealed record IdentificationScope(string Kind = "files", string? Folder = null)
{
    public static IdentificationScope? Read(JsonObject report) => report["identification_scope"]?.Deserialize<IdentificationScope>(Data.Options);

    internal (IReadOnlyList<Episode> Episodes, string Note) Coverage(string root, string series,
        NamingOptions naming, IReadOnlyList<Episode> catalog, SeriesIdentity? identity)
    {
        var filesOnly = (Episodes: (IReadOnlyList<Episode>)Array.Empty<Episode>(),
            Note: "Coverage is limited to the selected files. Other guide entries are not counted as missing.");
        if (Kind is not ("show" or "season" or "legacy")) return filesOnly;
        var folder = Kind == "season" ? Folder : root;
        if (string.IsNullOrWhiteSpace(folder) || !PreviewPlanner.Within(folder, root)) return filesOnly;
        var season = SeasonFolderScope.Detect(folder, series, naming, catalog, identity);
        if (season != null)
            return (catalog.Where(e => e.Season == season.Season).ToList(),
                $"Coverage: selected season {season.Season:00}. Unresolved recordings may contain additional episodes.");
        if (Kind == "show") return (catalog.Where(e => e.Season > 0).ToList(),
            "Coverage: full show. Unresolved recordings may contain additional episodes.");
        return filesOnly;
    }
}
