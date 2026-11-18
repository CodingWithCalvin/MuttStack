using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

// Exercise the exact library used by the desktop using only saved cues/references.
// Fallback decisions are recorded, not submitted to an actual model.
static class AppReplay {
 public static async Task Run(string fixturePath,string prepared,string output) {
  output=Path.GetFullPath(output);if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new InvalidOperationException("Use an empty replay directory.");
  var fixtureBytes=File.ReadAllBytes(fixturePath);var fixture=JsonNode.Parse(fixtureBytes)!;
  using var saved=new SavedSubtitleStore(prepared);var docs=saved.ReadAll();
  if(docs.Single(d=>d.Path.EndsWith("\\manifest.json")).Value.Text("fixture_sha256")!=Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)))throw new InvalidDataException("Fixture identity mismatch.");
  Directory.CreateDirectory(output);var db=new ScoutDatabase(Path.Combine(output,"replay.db"));var logical=Path.Combine(output,"data");ScoutStorage.Register(logical,db);
  var watch=Stopwatch.StartNew();var rows=new List<JsonObject>();int groupsCount=0,conflicts=0,combined=0;
  try {
   foreach(var group in fixture.Array("cases").GroupBy(c=>c.Text("series"))) {
    var guide=docs.Single(d=>d.Path.Contains("\\providers\\")&&d.Value["series"].Text("name")==group.Key).Value.Deserialize<ProviderSnapshot>(Data.Options)!;
    var references=docs.Where(d=>d.Path.Contains("\\references\\")&&d.Value.Text("series")==group.Key).Select(d=>new SubtitleReference(d.Value.Text("id"),d.Value.Text("name"),d.Value.Text("url"),d.Value.Text("zip_sha256"),d.Value.Int("season"),d.Value["cues"]!.Deserialize<List<Cue>>(Data.Options)!)).ToList();
    var matcher=new SubtitleMatcher(new Source(new(guide.Series,references)),Path.Combine(logical,"cache"));var groupRows=new List<JsonObject>();
    foreach(var c in group) {
     var cues=c!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var attempt=await matcher.IdentifyAsync(cues,guide.Episodes,guide.Series,new Progress<string>(),default);
     var row=SubtitleResult.Row(cues,guide.Episodes,attempt);row["path"]=c["source"]!.DeepClone();row["case_id"]=c["case_id"]!.DeepClone();row["full_dialogue"]=true;row["duration"]=cues.Max(cue=>cue.Start)+5.0;
     bool accepted=attempt.Result!=null&&SubtitleResult.Valid(row,guide.Episodes,guide.Series);row["route"]=accepted?"subtitles":"ai_fallback";
     if(!accepted)row["candidates"]=new JsonArray();
     if(attempt.Result!=null&&!accepted)throw new InvalidDataException("App rejected the subtitle adapter output for "+c.Text("case_id"));
     groupRows.Add(row);rows.Add(row);
    }
    var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(guide.Series,Data.Options),["files"]=new JsonArray(groupRows.Select(r=>r.DeepClone()).ToArray())};
    var groups=CollectionResolver.Resolve(report);groupsCount+=groups.Count;conflicts+=groups.Count(g=>g.Status=="unresolved_conflict");combined+=groups.Count(g=>g.Combined);
   }
   var summary=new{files=rows.Count,subtitleMatches=rows.Count(r=>r.Text("route")=="subtitles"),aiFallback=rows.Count(r=>r.Text("route")=="ai_fallback"),groups=groupsCount,conflicts,combined,seconds=watch.Elapsed.TotalSeconds,modelCalls=0,referenceNetworkCalls=0,appDatabaseWrites=false,mediaAccess=false,manualCorrections=false};
   Data.Save(Path.Combine(logical,"results.json"),new{summary,files=rows});Console.WriteLine(JsonSerializer.Serialize(summary,Data.Options));
  }finally{ScoutStorage.Unregister(logical);}
 }
 sealed class Source(SubtitleReferenceSet value):ISubtitleReferenceSource {
  public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(value);
 }
}
