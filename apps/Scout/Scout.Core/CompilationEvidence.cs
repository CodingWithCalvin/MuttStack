using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

public sealed record CompilationPart(string Path, double Duration);
public sealed record CompilationProof(IReadOnlyList<CompilationPart> Parts) {
    public string Summary => $"Complete dialogue is an exact concatenation of {Parts.Count} shorter recordings with the same total duration. Individual recordings keep their own episode assignments.";
}

// Derived from current report evidence on every resolution, never from a saved
// classification flag, filename, model guess, or duration alone. Three substantial
// recordings distinguish disc play-all titles from ordinary two-part episodes.
public static class CompilationEvidence {
    public static bool SourceUnchanged(JsonObject row) {
        try {
            var path = row.Text("path");
            return !row.Bool("source_changed") && File.Exists(path) && new FileInfo(path).Length == row["size"]!.GetValue<long>() &&
                PreviewPlanner.MtimeNs(path) == row["source_mtime_ns"]!.GetValue<long>() && PreviewPlanner.SampledDigest(path) == row.Text("sampled_digest");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException) { return false; }
    }
    public static bool SourcesUnchanged(CompilationProof proof, JsonObject whole, IEnumerable<JsonObject> rows) {
        var byPath = rows.ToDictionary(r => Path.GetFullPath(r.Text("path")), StringComparer.OrdinalIgnoreCase);
        return SourceUnchanged(whole) && proof.Parts.All(p => byPath.TryGetValue(p.Path, out var row) && SourceUnchanged(row));
    }
    sealed record Transcript(JsonObject Row, string Path, double Duration, string Text);
    static Transcript? Read(JsonObject row) {
        try {
            if(row["transcription"]!=null)return null; // Recognition output cannot prove an exact play-all concatenation.
            if (!row.Bool("full_dialogue") || row.Bool("source_changed") || row.Bool("move_pending") ||
                row.Text("relocation_error") != "" || RecordingAvailability.Missing(row) || row["reasoning"].Text("coverage") == "partial") return null;
            var duration = row["duration"]!.GetValue<double>();
            var cues = MultipartEvidence.Cues(row);
            if (!double.IsFinite(duration) || duration < 120 || cues.Count < 30 || cues[0].Start > duration * .1 || cues[^1].Start < duration * .8 ||
                cues.Any(c => !double.IsFinite(c.Start) || c.Start < 0 || c.Start > duration + 3) || cues.Zip(cues.Skip(1)).Any(p => p.First.Start > p.Second.Start)) return null;
            var words = Regex.Matches(string.Join(" ", cues.Select(c => c.Text)).ToLowerInvariant(), @"[\p{L}\p{Nd}]+").Select(m => m.Value).ToArray();
            if (words.Length < 256 || words.Distinct().Count() < 80) return null;
            return new(row, Path.GetFullPath(row.Text("path")), duration, string.Join(" ", words) + " ");
        } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or System.Text.Json.JsonException) { return null; }
    }
    public static IReadOnlyDictionary<string, CompilationProof> Find(IEnumerable<JsonObject> rows) {
        var transcripts = rows.Select(Read).OfType<Transcript>().ToList();
        var found = new Dictionary<string, CompilationProof>(StringComparer.OrdinalIgnoreCase);
        foreach (var whole in transcripts) {
            var shorter = transcripts.Where(t => t.Path != whole.Path && t.Duration < whole.Duration * .75 && t.Text.Length < whole.Text.Length)
                .GroupBy(t => t.Text).Select(g => g.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).First()).ToList();
            var memo = new Dictionary<int, List<Transcript>?>();
            List<Transcript>? Partition(int start, int depth) {
                if (start == whole.Text.Length) return [];
                if (depth >= 256) return null;
                if (memo.TryGetValue(start, out var saved)) return saved;
                List<Transcript>? best = null;
                foreach (var part in shorter) {
                    if (!whole.Text.AsSpan(start).StartsWith(part.Text, StringComparison.Ordinal)) continue;
                    var rest = Partition(start + part.Text.Length, depth + 1);
                    if (rest != null && (best == null || rest.Count + 1 > best.Count)) best = [part, ..rest];
                }
                return memo[start] = best;
            }
            var parts = Partition(0, 0);
            if (parts == null || parts.Count < 3 || parts.Select(p => p.Text).Distinct().Count() != parts.Count ||
                Math.Abs(parts.Sum(p => p.Duration) - whole.Duration) > Math.Max(3, whole.Duration * .005)) continue;
            found[whole.Path] = new(parts.Select(p => new CompilationPart(p.Path, p.Duration)).ToList());
        }
        return found;
    }
    internal static void Apply(List<RecordingGroup> groups) {
        var proofs = Find(groups.SelectMany(g => g.Rows));
        foreach (var group in groups.Where(g => g.HumanDecision == null && g.Status is not ("source_unavailable" or "stale_human_override") &&
                     g.Rows.All(r => proofs.ContainsKey(Path.GetFullPath(r.Text("path")))))) {
            group.Compilation = proofs[Path.GetFullPath(group.Rows[0].Text("path"))];
            group.Status = "compilation"; group.Claims.Clear(); group.Episodes.Clear();
        }
    }
}
