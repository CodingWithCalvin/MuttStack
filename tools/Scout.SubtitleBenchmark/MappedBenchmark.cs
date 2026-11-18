using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class MappedBenchmark {
 static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
 static Action<string,object> Writer(string root) {
  var logical=Path.Combine(root,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
  return (name,data)=>Data.Save(Path.Combine(logical,name),data);
 }
 public static async Task Run(string fixturePath,string previous,string output,string database,string settings,bool prepareOnly=false) {
  var root=Path.GetFullPath(output);
  if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Use an empty output directory.");
  var fixture=JsonNode.Parse(File.ReadAllText(fixturePath))!;var provider=fixture["settings"].Text("metadata_provider");
  using var saved=new SavedSubtitleStore(previous);var documents=saved.ReadAll();
  var manifest=documents.Single(d=>d.Path.EndsWith("\\manifest.json",StringComparison.OrdinalIgnoreCase)).Value;
  if(manifest.Text("fixture_sha256")!=Hash(fixturePath))throw new InvalidDataException("Saved subtitle results belong to a different dialogue fixture.");
  Directory.CreateDirectory(root);var save=Writer(root);var watch=Stopwatch.StartNew();
  save("manifest.json",new{fixtureSha256=Hash(fixturePath),referenceDatabaseSha256=Hash(Path.Combine(previous,"benchmark.db")),started=DateTimeOffset.UtcNow,provider,llmCalls=0,mediaAccess=false,appDatabaseWrites=false,manualCorrections=false,policyVersion=1,
   policy="Existing overlap thresholds unchanged. Numeric upload labels alone cannot assign provider IDs. Exact unique titles or a one-character typo in a long multipart title with an agreeing explicit range; dialogue links to title-bearing references; two ordered, substantial parts. No case-specific rules or inherited reference labels used."});
  var referenceDocs=documents.Where(d=>d.Path.Contains("\\references\\",StringComparison.OrdinalIgnoreCase)).ToArray();
  foreach(var d in referenceDocs)save("references/"+d.Value.Text("id")+".json",d.Value);
  var key=SubtitleCredentials.ReadSubdl(database,settings)??throw new InvalidOperationException("No SubDL key configured.");
  using var api=new SubdlApi(key);var failures=new List<object>();var all=new List<MappedReference>();
  foreach(var group in fixture.Array("cases").GroupBy(c=>c.Text("series"))) {
   string series=group.Key;var identity=documents.Single(d=>(d.Path.EndsWith("-identity.json")||d.Path.Contains("\\identities\\"))&&d.Value.Text("series")==series).Value;
   save("identities/"+CollectionResolver.Hash(series)+".json",identity);
   try {
    Console.WriteLine("Verifying configured provider identity: "+series);
    var cachedGuide=documents.SingleOrDefault(d=>d.Path.Contains("\\providers\\")&&d.Value["series"].Text("name")==series).Value?.Deserialize<ProviderSnapshot>(Data.Options);
    var guide=cachedGuide?.Series.Provider==provider?cachedGuide:await ProviderSnapshot.Fetch(provider,identity,database,settings,CancellationToken.None);
    var savedCatalog=group.First()!["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
    if(savedCatalog.Any(e=>guide.Episodes.All(p=>p.Id!=e.Id)))throw new InvalidDataException("Saved episode IDs do not all belong to the live configured-provider guide.");
    save("providers/"+CollectionResolver.Hash(series)+".json",guide);
    var refs=referenceDocs.Where(d=>d.Value.Text("series")==series).Select(d=>ReferenceEnrichment.Read(d.Value,guide.Episodes)).ToList();all.AddRange(refs);
    int before=all.Count;
    var cachedSearches=documents.Where(d=>d.Path.Contains("\\anchor-search\\")).ToDictionary(d=>d.Path[(d.Path.IndexOf("\\anchor-search\\",StringComparison.Ordinal)+1)..].Replace('\\','/'),d=>d.Value);
    await ReferenceEnrichment.AddAnchors(api,identity,guide,all,save,CancellationToken.None,cachedSearches);
    Console.WriteLine($"Provider {guide.Series.Key}: {refs.Count} cached references, {all.Count-before} additional references.");
   }catch(Exception ex) when(ex is HttpRequestException or InvalidDataException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException) {
    // No request bodies, tokens, credentials or inner exceptions are recorded.
    failures.Add(new{series,error=ex is HttpRequestException?"Metadata HTTP request failed; series mapping is unavailable.":ex.Message});
    Console.WriteLine("Reference mapping preparation incomplete for "+series+" ("+ex.GetType().Name+").");
   }
  }
  save("preparation.json",new{seconds=watch.Elapsed.TotalSeconds,cachedReferences=referenceDocs.Length,additionalReferences=all.Count-referenceDocs.Length,failures});
  save("status.json",new{state="prepared",updated=DateTimeOffset.UtcNow});
  if(!prepareOnly)Score(fixturePath,root);
 }
 public static void Offline(string fixturePath,string prepared,string output) {
  var root=Path.GetFullPath(output);if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Use an empty output directory.");
  List<(string Path,JsonNode Value)> docs;using(var saved=new SavedSubtitleStore(prepared))docs=saved.ReadAll();
  var manifest=docs.Single(d=>d.Path.EndsWith("\\manifest.json")).Value;
  if(manifest.Text("fixture_sha256")!=Hash(fixturePath))throw new InvalidDataException("Prepared evidence belongs to another fixture.");
  Directory.CreateDirectory(root);var save=Writer(root);
  foreach(var d in docs.Where(d=>new[]{"\\references\\","\\providers\\","\\identities\\","\\anchor-search\\","\\anchor-selection\\"}.Any(segment=>d.Path.Contains(segment)))) {
   var name=d.Path[(d.Path.IndexOf("\\data\\",StringComparison.Ordinal)+6)..];save(name,d.Value);
  }
  manifest["prepared_database_sha256"]=Hash(Path.Combine(prepared,"benchmark.db"));manifest["offline_replay"]=true;
  manifest["coverage_policy"]="Shared dialogue 10th/90th percentile must reach the first/last quarters of both texts. Each explicit reference part needs 60 unique shared sequences, 15% of source, 45% of reference, three reference sections and ordered 10th–90th percentile spans at least 15% of source width. Numeric labels and inherited suggestions never choose assignments.";
  save("manifest.json",manifest);save("preparation.json",new{seconds=0,cachedReferences=docs.Count(d=>d.Path.Contains("\\references\\")),additionalReferences=0,failures=docs.Single(d=>d.Path.EndsWith("\\preparation.json")).Value.Array("failures"),allEvidenceReused=true});
  Score(fixturePath,root);
 }
 public static void Score(string fixturePath,string output) {
  var root=Path.GetFullPath(output);var fixture=JsonNode.Parse(File.ReadAllText(fixturePath))!;
  List<(string Path,JsonNode Value)> docs;using(var saved=new SavedSubtitleStore(root))docs=saved.ReadAll();
  var manifest=docs.Single(d=>d.Path.EndsWith("\\manifest.json")).Value;
  if(manifest.Text("fixture_sha256")!=Hash(fixturePath))throw new InvalidDataException("Prepared fixture differs.");
  if(docs.Any(d=>d.Path.EndsWith("\\results.json")))throw new InvalidOperationException("This isolated evaluation already has outcomes. Use a new output directory for another evaluation.");
  var save=Writer(root);var watch=Stopwatch.StartNew();var rows=new List<JsonObject>();
  foreach(var group in fixture.Array("cases").GroupBy(c=>c.Text("series"))) {
   var guideDoc=docs.SingleOrDefault(d=>d.Path.Contains("\\providers\\")&&d.Value["series"].Text("name")==group.Key).Value;
   var guide=guideDoc?.Deserialize<ProviderSnapshot>(Data.Options);
   var refs=guide==null?[]:docs.Where(d=>d.Path.Contains("\\references\\")&&d.Value.Text("series")==group.Key).Select(d=>ReferenceEnrichment.Read(d.Value,guide.Episodes)).ToArray();
   var common=TranscriptComparison.Common(refs.Select(r=>r.Reference.Index).ToArray());
   var candidates=ReferenceMatcher.Candidates(refs,common);
   save("reference-mappings/"+CollectionResolver.Hash(group.Key)+".json",candidates.Select(c=>new{c.Id,c.Name,c.Link,parts=c.Parts.Select(p=>p.Reference.Id)}));
   foreach(var input in group) {
    var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var index=TranscriptComparison.Index(cues.Select(c=>c.Text));
    var decision=ReferenceMatcher.Match(index,candidates,common);var best=decision.Best;
    var episodes=decision.ProviderMapped?best!.Candidate.Link!.Episodes:[];
    var partsOnly=candidates.Where(c=>c.Parts.Length==2).ToArray();
    // Exercise multipart matching independently of any full-length reference.
    // This is a diagnostic only and cannot replace the main decision.
    var partCheck=ReferenceMatcher.Match(index,partsOnly,common);
    bool partAgrees=decision.ProviderMapped&&partCheck.ProviderMapped&&partCheck.Best?.Candidate.Link?.Key==best?.Candidate.Link?.Key;
    var row=new JsonObject{["case_id"]=input["case_id"]!.DeepClone(),["source"]=input["source"]!.DeepClone(),["series"]=input["series"]!.DeepClone(),
     ["dialogue_identity"]=CollectionResolver.Hash(string.Join(" ",index.Words)),["status"]=decision.ProviderMapped?"mapped":"held",["reason"]=decision.Reason,
     ["text_supported"]=decision.TextSupported,["provider"]=guide?.Series.Provider,["series_id"]=guide?.Series.Id,
     ["episodes"]=JsonSerializer.SerializeToNode(episodes.Select(e=>new{e.Id,e.Code,e.Title}),Data.Options),["margin"]=decision.Margin,
     ["reference_parts"]=best?.Candidate.Parts.Length??0,["mapping"]=JsonSerializer.SerializeToNode(best?.Candidate.Link,Data.Options),
     ["combined_reference_check"]=JsonSerializer.SerializeToNode(new{supported=partAgrees,partCheck.Reason,partCheck.Margin,candidate=partCheck.Best?.Candidate.Name,providerIds=partCheck.ProviderMapped?partCheck.Best!.Candidate.Link!.Episodes.Select(e=>e.Id).ToArray():[],evidence=partCheck.Best?.Parts},Data.Options),
     ["ranking"]=JsonSerializer.SerializeToNode(decision.Ranking.Select(s=>new{s.Candidate.Id,s.Candidate.Name,s.Candidate.Link,s.Overlap,s.OrderedParts,s.Parts,s.CompleteExtent}),Data.Options)};
    rows.Add(row);
   }
  }
  // Collection conflicts are never silently forced into one-to-one winners.
  var claims=rows.Where(r=>r.Text("status")=="mapped").SelectMany(r=>r.Array("episodes").Select(e=>new{Row=r,Key=r.Text("provider")+":"+r.Text("series_id")+":"+e.Int("id")})).GroupBy(x=>x.Key);
  foreach(var claim in claims.Where(g=>g.Select(x=>x.Row.Text("dialogue_identity")).Distinct().Count()>1))foreach(var member in claim) {
   member.Row["status"]="conflict";member.Row["reason"]="Different saved dialogues claim the same provider episode; no automatic winner selected.";
  }
  foreach(var copies in rows.GroupBy(r=>r.Text("dialogue_identity")))foreach(var row in copies)row["duplicate_group_size"]=copies.Count();
  // Inherited labels are evaluated only AFTER every decision and conflict check.
  foreach(var row in rows) {
   var input=fixture.Array("cases").Single(c=>c.Text("case_id")==row.Text("case_id"))!;
   var expected=input.Array("reference_ids").Select(n=>n!.GetValue<int>()).Order().ToArray();
   row["inherited_reference_comparison"]=row.Text("status")!="mapped"?"held, not scored":expected.Length==0?"unscored":row.Array("episodes").Select(e=>e.Int("id")).Order().SequenceEqual(expected)?"agrees with inherited suggestion":"differs from inherited suggestion";
   save("cases/"+row.Text("case_id")+".json",row);
  }
  var prep=docs.Single(d=>d.Path.EndsWith("\\preparation.json")).Value;
  var summary=new{cases=rows.Count,uniqueDialogues=rows.Select(r=>r.Text("dialogue_identity")).Distinct().Count(),mapped=rows.Count(r=>r.Text("status")=="mapped"),held=rows.Count(r=>r.Text("status")=="held"),conflicts=rows.Count(r=>r.Text("status")=="conflict"),
   textSupported=rows.Count(r=>r.Bool("text_supported")),combinedReferences=rows.Count(r=>r.Text("status")=="mapped"&&r.Int("reference_parts")==2),combinedReferenceChecks=rows.Count(r=>r["combined_reference_check"].Bool("supported")),multiEpisode=rows.Count(r=>r.Text("status")=="mapped"&&r.Array("episodes").Count>1),
   referenceFiles=docs.Count(d=>d.Path.Contains("\\references\\")),comparisonSeconds=watch.Elapsed.TotalSeconds,preparationSeconds=prep["seconds"]!.GetValue<double>(),
   inheritedAgreement=rows.Count(r=>r.Text("inherited_reference_comparison")=="agrees with inherited suggestion"),inheritedDisagreement=rows.Count(r=>r.Text("inherited_reference_comparison")=="differs from inherited suggestion"),
   llmCalls=0,manualCorrections=false,appDatabaseWrites=false,mediaAccess=false,
   limitations="Provider mapping is automated metadata reconciliation, not independently labelled accuracy. Subtitle uploads can be mislabeled; mapping requires title anchors and/or content bridges, never bare upload numbering. Fixed coverage and margin rules are feasibility heuristics, not calibrated confidence. Inherited episode suggestions are unverified and never used to make decisions."};
  save("results.json",new{cases=rows});save("summary.json",summary);save("status.json",new{state="complete",updated=DateTimeOffset.UtcNow});
  WriteReport(root,rows,JsonSerializer.SerializeToNode(summary,Data.Options)!,prep);Console.WriteLine(JsonSerializer.Serialize(summary,Data.Options));
 }
 static void WriteReport(string root,List<JsonObject> rows,JsonNode summary,JsonNode preparation) {
  string E(string x)=>WebUtility.HtmlEncode(x);
  var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Scout subtitle provider matches</title><style>body{font:16px/1.5 Segoe UI;background:#111b28;color:#e4edfb;margin:28px}a{color:#91c3ff}table{border-collapse:collapse;width:100%}th,td{padding:12px;text-align:left;border-bottom:1px solid #405269;vertical-align:top}pre{white-space:pre-wrap;overflow-wrap:anywhere}.scroll{overflow:auto}.held,.conflict{color:#ffd79c}details{margin:15px 0}</style><h1>Subtitle matches in the configured episode guide</h1>");
  html.Append($"<p>{summary.Int("mapped")} mapped · {summary.Int("held")} held · {summary.Int("conflicts")} conflicts · {summary.Int("unique_dialogues")} unique dialogues / {rows.Count} saved files</p><p>Saved evidence only. No models, manual corrections, Scout result changes or video changes. Duplicate saved dialogue is grouped below. Provider mappings remain subject to subtitle-label quality.</p><div class=scroll><table><tr><th>Recording</th><th>Provider episode assignment</th><th>Dialogue reference</th><th>Outcome</th></tr>");
  foreach(var group in rows.GroupBy(r=>r.Text("dialogue_identity"))) {
   var r=group.First();var best=r.Array("ranking").FirstOrDefault();
   var assignment=r.Text("status")=="mapped"?string.Join("<br>",r.Array("episodes").Select(e=>E(e.Text("code")+" · "+e.Text("title"))+"<small> · "+E(r.Text("provider")+":"+e.Int("id"))+"</small>")):"Unresolved";
   html.Append("<tr><td>").Append(string.Join("<br>",group.Select(x=>E(Path.GetFileName(x.Text("source")))))).Append(group.Count()>1?$"<br><small>{group.Count()} copies of identical saved dialogue</small>":"").Append("</td><td>").Append(assignment).Append("</td><td>").Append(E(best.Text("name","No decisive reference"))).Append(r["combined_reference_check"].Bool("supported")?"<p>Also passes using Part 1 + Part 2 references without a full-length reference.</p>":"").Append("</td><td class='").Append(E(r.Text("status"))).Append("'>").Append(E(r.Text("status"))).Append("<br>").Append(E(r.Text("reason"))).Append("</td></tr>");
  }
  html.Append("</table></div><p>").Append(E(summary.Text("limitations"))).Append("</p><details><summary>Measurements</summary><pre>").Append(E(summary.ToJsonString(Data.Options))).Append("</pre></details><details><summary>Reference retrieval</summary><pre>").Append(E(preparation.ToJsonString(Data.Options))).Append("</pre></details>");
  foreach(var row in rows)html.Append("<details><summary>").Append(E(row.Text("case_id")+" · "+Path.GetFileName(row.Text("source")))).Append("</summary><pre>").Append(E(row.ToJsonString(Data.Options))).Append("</pre></details>");
  File.WriteAllText(Path.Combine(root,"results.html"),html.Append("</html>").ToString());
 }
}
