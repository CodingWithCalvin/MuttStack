using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

// Duration corroborates completeness, never episode identity. Only independently
// matched, complete single episodes from this run establish the comparison.
public sealed record RecordingExtent(double DurationSeconds,double[] SingleEpisodeSeconds) {
 public double TypicalSeconds=>SingleEpisodeSeconds.Length==0?0:SingleEpisodeSeconds.Order().ElementAt(SingleEpisodeSeconds.Length/2);
 public bool Supports(int count)=>count is >=2 and <=4&&SingleEpisodeSeconds.Length>=5&&
  double.IsFinite(DurationSeconds)&&SingleEpisodeSeconds.All(d=>double.IsFinite(d)&&d>=600&&d<=7200)&&
  SingleEpisodeSeconds.Count(d=>Math.Abs(d/TypicalSeconds-1)<=.12)>=SingleEpisodeSeconds.Length*.8&&
  Math.Abs(DurationSeconds/TypicalSeconds-count)<=count*.12;
 public static RecordingExtent FromRow(JsonObject row,JsonObject report,IReadOnlyList<Episode> catalog) {
  var singles=CollectionResolver.Resolve(report).Where(g=>g.Status=="suggested"&&g.Episodes.Count==1&&!g.Split&&g.Compilation==null&&g.HumanDecision==null)
   .Where(g=>catalog.Any(e=>e.Id==g.Episodes[0].Id&&e.Season==g.Episodes[0].Season&&e.Number==g.Episodes[0].Number&&e.Title==g.Episodes[0].Title&&e.Overview==g.Episodes[0].Overview)).Select(g=>g.Rows[0])
   .Where(r=>!PreviewPlanner.Same(r.Text("path"),row.Text("path"))&&r["reasoning"].Text("coverage")=="complete"&&
    r.Bool("full_dialogue")&&r["reasoning"].Text("dialogue_hash")==MultipartEvidence.DialogueHash(r)&&
    (Subtitles.SubtitleResult.IsSubtitle(r)||r["reasoning"].Text("method")==DialogueMatcher.Version))
   .Select(r=>r["duration"]?.GetValue<double>()??0).Where(d=>double.IsFinite(d)&&d>=600&&d<=7200).Order().ToArray();
  return new(row["duration"]?.GetValue<double>()??0,singles);
 }
}

public sealed partial class DialogueMatcher {
 private readonly AsyncLocal<RecordingExtent?> recordingExtent=new();
 public IDisposable ForRecording(RecordingExtent extent) {
  var previous=recordingExtent.Value;recordingExtent.Value=extent;
  return new ExtentScope(()=>recordingExtent.Value=previous);
 }
 private sealed class ExtentScope(Action restore):IDisposable {public void Dispose()=>restore();}
 private string ExtentCacheScope=>recordingExtent.Value is {} extent&&Enumerable.Range(2,3).Any(extent.Supports)?JsonSerializer.Serialize(extent,Data.Options):"";
}
