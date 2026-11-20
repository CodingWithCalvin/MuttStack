using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core.Subtitles;

public sealed record SubtitleAttempt(JsonObject Details,JsonObject? Result);
public sealed class SubtitleMatcher(ISubtitleReferenceSource source,string cacheRoot) {
 string? scope;List<ReferenceCandidate> candidates=[];HashSet<string> common=[];string note="",referenceHash="";
 public async Task<SubtitleAttempt> IdentifyAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,SeriesIdentity series,IProgress<string> progress,CancellationToken ct) {
  var current=series.Key+":"+SubtitleResult.CatalogHash(catalog);ct.ThrowIfCancellationRequested();
  if(scope!=current) {
   var loaded=await source.LoadAsync(series,catalog,progress,ct);
   if(loaded.Series.Key!=series.Key)throw new InvalidDataException("Subtitle references belong to another provider series.");
   var refs=loaded.References.Select(r=>new MappedReference(new(r.Id,series.Name,r.Name,r.Url,TranscriptComparison.Index(r.Cues)),r.Archive,r.Season,TitleMapping.Label(r.Name),TitleMapping.Find(r.Name,catalog,r.Id))).ToArray();
   common=TranscriptComparison.Common(refs.Select(r=>r.Reference.Index).ToArray());
   candidates=ReferenceMatcher.Candidates(refs,common,ct);ct.ThrowIfCancellationRequested();
   referenceHash=CollectionResolver.Hash(JsonSerializer.Serialize(loaded.References.OrderBy(r=>r.Id),Data.Options));note=loaded.Note;scope=current;
  }
  var cacheKey=CollectionResolver.Hash(JsonSerializer.Serialize(new{SubtitleResult.Version,comparisonRevision=TranscriptComparison.Revision,current,referenceHash,cues},Data.Options));
  var path=Path.Combine(cacheRoot,"decisions",cacheKey+".json");
  if(ScoutStorage.Exists(path))try {
   var saved=Data.Read(path);var savedResult=saved["result"] as JsonObject;
   var attempt=new SubtitleAttempt(saved["details"]!.AsObject(),savedResult);
   if(savedResult==null||SubtitleResult.Valid(SubtitleResult.Row(cues,catalog,attempt),catalog,series)) {progress.Report("Reusing exact-input subtitle comparison.");return attempt;}
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException){/* Recalculate invalid/stale cached decisions. */}
  progress.Report("Comparing saved dialogue with subtitle references…");
  var index=TranscriptComparison.Index(cues);var decision=ReferenceMatcher.Match(index,candidates,common,ct);ct.ThrowIfCancellationRequested();
  var best=decision.Best;
  var episodes=decision.ProviderMapped?best!.Candidate.Link!.Episodes:[];
  // The app currently represents at most two consecutive catalog episodes.
  bool supported=decision.ProviderMapped&&episodes.Length is 1 or 2&&(episodes.Length==1||episodes[0].Season==episodes[1].Season&&episodes[0].Number+1==episodes[1].Number);
  var details=JsonSerializer.SerializeToNode(new{version=SubtitleResult.Version,comparisonRevision=TranscriptComparison.Revision,accepted=supported,provider=series.Provider,seriesId=series.Id,
   catalogHash=SubtitleResult.CatalogHash(catalog),dialogueHash=SubtitleResult.DialogueHash(cues),referenceHash,referenceFiles=candidates.Count(c=>c.Parts.Length==1),
   reason=decision.Reason,note,decision.Margin,selectedEpisodes=episodes,
   best=best==null?null:new{best.Candidate.Id,best.Candidate.Name,best.Candidate.Link,best.Overlap,best.OrderedParts,best.CompleteExtent,best.Parts,
    references=best.Candidate.Parts.Select(p=>new{p.Reference.Id,p.Reference.Name,p.Reference.Url})},
   alternatives=decision.Ranking.Skip(1).Take(3).Select(r=>new{r.Candidate.Name,r.Overlap.Score,episodeIds=r.Candidate.Link?.Episodes.Select(e=>e.Id)})},Data.Options)!.AsObject();
  JsonObject? result=null;
  if(supported) {
   var evidence=cues.Where(c=>TranscriptComparison.Index([c.Text]).Phrases.Keys.Any(p=>!common.Contains(p)&&best!.Candidate.Index.Phrases.ContainsKey(p))).ToArray();
   var quotes=Enumerable.Range(0,Math.Min(4,evidence.Length)).Select(i=>evidence[i*(evidence.Length-1)/Math.Max(1,Math.Min(4,evidence.Length)-1)]).Distinct().ToArray();
   result=new JsonObject{["assessment"]="supported",["coverage"]=episodes.Length==2?"combined":"complete",["method"]=SubtitleResult.Version,["dialogue_hash"]=SubtitleResult.DialogueHash(cues),
    ["summary"]="Distinctive dialogue matches subtitle references linked to the configured provider's episode titles. This is subtitle evidence, not an AI assessment.",
    ["matches"]=JsonSerializer.SerializeToNode(episodes.Select(e=>new{episodeId=e.Id,episodeTitle=e.Title,reason="Subtitle dialogue and provider-title mapping support this assignment.",evidence=quotes.Select(c=>new{start=c.Start,sourceText=c.Text})}),Data.Options),
    ["candidate_checks"]=new JsonArray(),["alternatives"]=new JsonArray()};
  }
  var answer=new SubtitleAttempt(details,result);ct.ThrowIfCancellationRequested();Data.Save(path,answer);return answer;
 }
}

public static class SubtitleResult {
 public static string FallbackSummary(JsonObject details) {
  var reason=details.Text("reason","No supported subtitle assignment.");
  if(details["best"] is not JsonObject best||best["overlap"] is not JsonObject overlap)return reason;
  return reason+$" Best reference: {best.Text("name")} · source sequences {overlap["source_coverage"]!.GetValue<double>():P0}, reference sequences {overlap["reference_coverage"]!.GetValue<double>():P0}"+
   (best["link"]==null?" · no verified provider-title mapping.":".");
 }
 public const string Version="subtitle-provider-v1";
 public static string CatalogHash(IReadOnlyList<Episode> catalog)=>CollectionResolver.Hash(JsonSerializer.Serialize(catalog.OrderBy(e=>e.Id),Data.Options));
 public static string DialogueHash(IReadOnlyList<Cue> cues)=>CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
 public static bool IsSubtitle(JsonObject row)=>row.Text("matching_method")=="subtitles"||row["reasoning"].Text("method")==Version;
 public static JsonObject Row(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,SubtitleAttempt attempt) {
  var row=new JsonObject{["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)}};Apply(row,catalog,attempt);return row;
 }
 public static void Apply(JsonObject row,IReadOnlyList<Episode> catalog,SubtitleAttempt attempt) {
  row["subtitle_match"]=attempt.Details.DeepClone();
  if(attempt.Result==null)return;
  row["matching_method"]="subtitles";row["reasoning"]=attempt.Result.DeepClone();
  row["candidates"]=new JsonArray(attempt.Result.Array("matches").Select(m=>(JsonNode)new JsonObject {
   ["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==m.Int("episode_id")),Data.Options),["method"]="subtitles",["reason"]=m!["reason"]!.DeepClone(),
   ["evidence"]=new JsonArray(m.Array("evidence").Select(q=>(JsonNode)new JsonObject{["start"]=q!["start"]!.DeepClone(),["text"]=q["source_text"]!.DeepClone()}).ToArray())}).ToArray());
 }
 public static bool Valid(JsonObject row,IReadOnlyList<Episode>? catalog=null,SeriesIdentity? identity=null) {
  try {
   var proof=row["subtitle_match"]!;var result=row["reasoning"]!;var cues=MultipartEvidence.Cues(row);
   if(!IsSubtitle(row)||proof.Text("version")!=Version||!proof.Bool("accepted")||result.Text("method")!=Version||cues.Count==0)return false;
   var hash=DialogueHash(cues);if(proof.Text("dialogue_hash")!=hash||result.Text("dialogue_hash")!=hash)return false;
   if(catalog!=null&&proof.Text("catalog_hash")!=CatalogHash(catalog))return false;
   if(identity!=null&&(proof.Text("provider")!=identity.Provider||proof.Text("series_id")!=identity.Id))return false;
   new SeriesIdentity(proof.Text("provider"),proof.Text("series_id"),"Saved subtitle series").Validate();
   var selected=proof["selected_episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
   var candidates=row.Array("candidates").Select(c=>Data.Episode(c!["episode"]!)).ToArray();
   if(selected.Count is <1 or >2||!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(selected,Data.Options),JsonSerializer.SerializeToNode(candidates,Data.Options)))return false;
   if(catalog!=null&&selected.Any(e=>!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(e,Data.Options),JsonSerializer.SerializeToNode(catalog.SingleOrDefault(c=>c.Id==e.Id),Data.Options))))return false;
   if(selected.Count==2&&(selected[0].Season!=selected[1].Season||selected[0].Number+1!=selected[1].Number))return false;
   if(result.Text("assessment")!="supported"||result.Text("coverage")!=(selected.Count==2?"combined":"complete")||!result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(selected.Select(e=>e.Id)))return false;
   var best=proof["best"]!;var score=best["overlap"]!.Deserialize<Overlap>(Data.Options)!;
   if(!ReferenceMatcher.Strong(score)||proof["margin"]!.GetValue<double>()<.15||!best.Bool("ordered_parts")||!best.Bool("complete_extent")||!best["link"].Bool("complete")||best.Array("references").Count==0)return false;
   if(!best["link"]!.Array("episodes").Select(e=>e.Int("id")).SequenceEqual(selected.Select(e=>e.Id)))return false;
   foreach(var m in result.Array("matches"))foreach(var q in m.Array("evidence"))if(!cues.Any(c=>c.Start==q!["start"]!.GetValue<double>()&&c.Text==q.Text("source_text")))return false;
   return true;
  }catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException or JsonException){return false;}
 }
}
