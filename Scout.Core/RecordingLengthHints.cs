using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

// A triage clue only. These estimates never create episode assignments.
internal static class RecordingLengthHints {
 public static Dictionary<string,string> ForReport(JsonObject report,IReadOnlyList<RecordingGroup>? groups=null) {
  var singles=(groups??CollectionResolver.Resolve(report)).Where(g=>g.Episodes.Count==1&&!g.Split&&g.Compilation==null&&!g.Feature)
   .Select(g=>g.Rows.First()).ToArray();
  var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  double Duration(JsonObject r)=>r["duration"]?.Deserialize<double>()??0;
  double Size(JsonObject r)=>r["primary_fingerprint"]?["size"]?.Deserialize<double>()??r["size"]?.Deserialize<double>()??0;
  double Median(IEnumerable<double> values) {var v=values.Where(x=>double.IsFinite(x)&&x>0).Order().ToArray();return v.Length<5?0:(v[(v.Length-1)/2]+v[v.Length/2])/2;}
  int Count(double ratio) {var n=(int)Math.Round(ratio);return n is >=2 and <=4&&Math.Abs(ratio-n)<=.2*n?n:0;}
  foreach(var row in report.Array("files").OfType<JsonObject>()) {
   var folder=Path.GetDirectoryName(row.Text("path"));
   var peers=singles.Where(r=>!PreviewPlanner.Same(r.Text("path"),row.Text("path"))&&string.Equals(Path.GetDirectoryName(r.Text("path")),folder,StringComparison.OrdinalIgnoreCase)).ToArray();
   double duration=Duration(row),typicalDuration=Median(peers.Select(Duration));
   double size=Size(row),typicalSize=Median(peers.Where(r=>string.Equals(Path.GetExtension(r.Text("path")),Path.GetExtension(row.Text("path")),StringComparison.OrdinalIgnoreCase)).Select(Size));
   if(duration>0&&typicalDuration>0) {
    int count=Count(duration/typicalDuration);if(count==0)continue;
    result[row.Text("path")]=$"Possible combined recording: {duration/60:0.#} minutes is about {count} typical episodes in this folder (median {typicalDuration/60:0.#} minutes)."+
     (typicalSize>0?$" File size is {size/typicalSize:0.#}× the single-episode median.":"")+" Subtitle/dialogue evidence must establish the titles and provider range; a long special may be one catalog episode.";
   } else if(typicalSize>0&&Count(size/typicalSize) is >0 and var count)
    result[row.Text("path")]=$"Possible combined recording: file size is {size/typicalSize:0.#}× the single-episode median in this folder (roughly {count} episodes). Size is a weak clue because compression varies; verify duration and dialogue before assigning episodes.";
  }
  return result;
 }
}
