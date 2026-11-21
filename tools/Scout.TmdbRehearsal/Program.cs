using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;
using Scout.Core.Subtitles;

// Offline regression replay. Reads only audit JSON and a copied SQLite snapshot;
// never opens a recorded media path or contacts a service.
if(args.Length!=2||!args.All(Path.IsPathFullyQualified))throw new ArgumentException("Usage: <isolated-comparison-directory> <new .build/tmdb-rehearsal-* directory>");
var audit=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);
if(!output.Contains("\\.build\\tmdb-rehearsal-",StringComparison.OrdinalIgnoreCase)||Directory.Exists(output))throw new ArgumentException("Choose a new isolated .build/tmdb-rehearsal-* directory.");
for(var dir=new DirectoryInfo(output);dir!=null;dir=dir.Parent)if(dir.Exists&&dir.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Rehearsal output cannot traverse junctions.");
JsonObject Read(string path)=>JsonNode.Parse(File.ReadAllText(path))!.AsObject();
var comparison=Read(Path.Combine(audit,"comparison.json"));
if(!comparison.Bool("api_verified")||comparison.Bool("live_changes_applied"))throw new InvalidDataException("An isolated API-verified comparison is required.");
var official=Read(Path.Combine(audit,"tmdb","series.json"));
if(official.Int("id")!=5371||official["external_ids"].Int("tvdb_id")!=82493||official["external_ids"].Text("imdb_id")!="tt0972534")throw new InvalidDataException("The copied catalogs must identify the original iCarly.");
var series=new SeriesIdentity("tmdb","5371","iCarly","2007");
var catalog=Enumerable.Range(1,6).SelectMany(s=>Read(Path.Combine(audit,"tmdb",$"season-{s:00}.json")).Array("episodes"))
    .Select(e=>new Episode(e.Int("id"),e.Int("season_number"),e.Int("episode_number"),e.Text("name"),e.Text("overview"))).ToArray();
if(catalog.Length!=107||catalog.Select(e=>e.Id).Distinct().Count()!=107)throw new InvalidDataException("Unexpected copied TMDB catalog.");
var referenceIds=Read(Path.Combine(audit,"subtitle-input.json")).Array("references").Select(r=>r.Text("id")).ToHashSet();
var references=new List<SubtitleReference>();
using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(audit,"doggo.snapshot.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())) {
    db.Open();using var command=db.CreateCommand();command.CommandText="SELECT content FROM scout_documents WHERE path LIKE '%subdl-references-v1%references%json'";
    using var reader=command.ExecuteReader();while(reader.Read()) {
        var reference=JsonSerializer.Deserialize<SubtitleReference>((byte[])reader.GetValue(0),Data.Options)!;
        if(referenceIds.Contains(reference.Id))references.Add(reference);
    }
}
if(references.Count!=referenceIds.Count)throw new InvalidDataException("Incomplete copied reference cache.");
var reports=Directory.EnumerateDirectories(Path.Combine(audit,"scout")).ToDictionary(d=>Path.GetFileName(d),d=>Read(Path.Combine(d,"report.json")));
Directory.CreateDirectory(output);var media=Directory.CreateDirectory(Path.Combine(output,"media")).FullName;
var rows=new JsonArray();var findings=new JsonArray();int index=0;
foreach(var candidate in comparison.Array("files").Where(r=>r.Text("status").StartsWith("Unresolved source")||r.Text("status")=="Review full-story coverage")) {
    var original=reports[candidate.Text("analysis_generation_run")].Array("files").Single(r=>r.Text("path")==candidate.Text("original_source",candidate.Text("path"))||r.Text("path")==candidate.Text("path"))!.AsObject();
    var cues=MultipartEvidence.Cues(original);if(cues.Count==0)throw new InvalidDataException("No copied dialogue for replay.");
    var expected=candidate.Array("tmdb_codes").Select(c=>catalog.Single(e=>e.Code==c!.GetValue<string>())).ToArray();
    var cache=Path.Combine(output,"cache",(++index).ToString());
    SubtitleMatcher Matcher()=>new(new OfflineSource(new(series,references)),cache);
    var baseline=await Matcher().IdentifyAsync(cues,catalog,series,new SilentProgress(),default);
    var local=Path.Combine(media,$"fixture-{index:00}.mkv");File.WriteAllText(local,"Synthetic fixture only; copied dialogue is analyzed separately.");
    var row=SubtitleResult.Row(cues,catalog,baseline);row["path"]=local;row["duration"]=original["duration"]?.DeepClone();
    var stat=new FileInfo(local);row["size"]=stat.Length;row["source_mtime_ns"]=PreviewPlanner.MtimeNs(local);row["sampled_digest"]=PreviewPlanner.SampledDigest(local);
    row["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(local))),["size"]=stat.Length,["mtime_ns"]=PreviewPlanner.MtimeNs(local),["sampled_digest"]=PreviewPlanner.SampledDigest(local)};
    var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
    var reviewable=ReviewedReferenceMapping.CanReview(row);bool after=false;
    if(reviewable) {
        var resolution=HumanResolution.Apply(report,row,expected,catalog,"Simulated explicit confirmation in isolated replay; no live assignment.");
        ReviewedReferenceMapping.Save(report,row,expected,catalog,resolution);
        var matcher=Matcher();matcher.UseReviewedReferences(report);
        var reviewed=await matcher.IdentifyAsync(cues,catalog,series,new SilentProgress(),default);
        SubtitleResult.Apply(row,catalog,reviewed);after=SubtitleResult.Valid(row,catalog,series)&&row.Array("candidates").Select(c=>c!["episode"].Int("id")).SequenceEqual(expected.Select(e=>e.Id));
        if(!after)throw new InvalidDataException("Reviewed replay did not retain the expected provider assignment.");
        // Preview the automatic subtitle assignment without a force-assignment shortcut.
        var automatic=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row.DeepClone())};
        var plan=PreviewPlanner.Create(automatic,media,series.Name,catalog,identity:series,destinationRoot:Path.Combine(output,"library"));
        if(plan.Items.Single().Action=="hold")throw new InvalidDataException("A verified replay remained held: "+string.Join("; ",plan.Items.Single().BlockedReasons));
        findings.Add(new JsonObject{["source_name"]=Path.GetFileName(candidate.Text("path")),["baseline_accepted"]=baseline.Result!=null,["reviewed_accepted"]=after,["codes"]=JsonSerializer.SerializeToNode(expected.Select(e=>e.Code)),["proposed_path"]=plan.Items.Single().Destination,["simulated_review_only"]=true});
        HumanResolution.Undo(report,resolution);var undone=Matcher();undone.UseReviewedReferences(report);var unreviewed=await undone.IdentifyAsync(cues,catalog,series,new SilentProgress(),default);
        if(unreviewed.Result!=null&&baseline.Result==null)throw new InvalidDataException("An undone mapping remained cached.");
    }else findings.Add(new JsonObject{["source_name"]=Path.GetFileName(candidate.Text("path")),["baseline_accepted"]=baseline.Result!=null,["reviewed_accepted"]=false,["reason"]=baseline.Details.Text("reason"),["simulated_review_only"]=true});
    rows.Add(row.DeepClone());
    Console.WriteLine($"Replayed {index}: {string.Join(",",expected.Select(e=>e.Code))}; explicit review accepted={after}");
}
Data.Save(Path.Combine(output,"results.json"),new{officialCatalogEntries=catalog.Length,referenceFiles=references.Count,findings,liveMediaOpened=false,networkRequests=0,liveWrites=0});
Console.WriteLine($"Completed {findings.Count} copied-dialogue cases; no live media opened or changed.");

sealed class OfflineSource(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(set);}
sealed class SilentProgress:IProgress<string>{public void Report(string value){}}
