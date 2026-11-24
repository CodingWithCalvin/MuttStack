namespace Scout.Core.Subtitles;

public interface ISupplementalSubtitleReferenceSource:ISubtitleReferenceSource {
 Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IReadOnlyList<SubtitleReference> existing,IProgress<string> progress,CancellationToken ct);
}
public sealed class CombinedSubtitleReferences(params (string Name,ISubtitleReferenceSource Source)[] sources):ISubtitleReferenceSource {
 public async Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct) {
  var references=new List<SubtitleReference>();var notes=new List<string>();
  foreach(var (name,source) in sources) {
   ct.ThrowIfCancellationRequested();progress.Report($"Checking {name} English subtitle references…");
   try {
    var set=source is ISupplementalSubtitleReferenceSource supplemental
     ?await supplemental.LoadAsync(series,catalog,references,progress,ct):await source.LoadAsync(series,catalog,progress,ct);
    if(set.Series.Key!=series.Key)throw new InvalidDataException("Reference series mismatch.");
    references.AddRange(set.References);notes.Add($"{name}: {set.References.Count} references. "+set.Note);
   }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
   catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException) {
    notes.Add($"{name} could not be checked; references from other providers remain available.");
   }
  }
  // Keep independent provenance and conflicting identities. ReferenceMatcher
  // already groups equivalent dialogue without counting copies as corroboration.
  var note=string.Join(" ",notes);progress.Report(note);
  return new(series,references,note);
 }
}
