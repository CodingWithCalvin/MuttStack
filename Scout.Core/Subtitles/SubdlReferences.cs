using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scout.Core.Subtitles;

// All persisted documents go through ScoutStorage, so the desktop cache stays in
// the application's registered database. No subtitle files are extracted to disk.
public sealed class SubdlReferences(string cacheRoot,string? apiKey,string? metadataKey=null,
 Func<HttpMessageHandler>? apiHandler=null,Func<HttpMessageHandler>? metadataHandler=null):ISubtitleReferenceSource {
 const string CacheVersion="subdl-references-v1";
 const string RetrievalPolicy="normalized-series-and-episode-fallback-v5";
 public async Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct) {
  var gate=CacheGates.GetOrAdd(Path.GetFullPath(cacheRoot)+"|"+series.Key,_=>new(1,1));
  await gate.WaitAsync(ct).ConfigureAwait(false);
  try{return await LoadCoreAsync(series,catalog,progress,ct).ConfigureAwait(false);}finally{gate.Release();}
 }
 static readonly ConcurrentDictionary<string,SemaphoreSlim> CacheGates=new(StringComparer.OrdinalIgnoreCase);
 async Task<SubtitleReferenceSet> LoadCoreAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct) {
  series.Validate();ct.ThrowIfCancellationRequested();
  var root=Path.Combine(cacheRoot,CacheVersion,CollectionResolver.Hash(series.Key));var manifestPath=Path.Combine(root,"manifest.json");
  string guideHash=SubtitleResult.CatalogHash(catalog);
  var references=new List<SubtitleReference>();JsonObject? cached=null;
  if(ScoutStorage.Exists(manifestPath))try {
   cached=Data.Read(manifestPath);
   if(cached["series"]?.Deserialize<SeriesIdentity>(Data.Options)?.Key!=series.Key)throw new InvalidDataException("Subtitle cache series mismatch.");
   foreach(var id in cached.Array("references").Select(n=>n!.GetValue<string>())) {
    if(!Regex.IsMatch(id,"^[0-9a-f]{64}$"))throw new InvalidDataException("Invalid subtitle cache identifier.");
    var reference=Data.Read(Path.Combine(root,"references",id+".json")).Deserialize<SubtitleReference>(Data.Options)!;
    if(reference==null||reference.Id!=id||reference.Cues==null||reference.Cues.Count==0)throw new InvalidDataException("Incomplete subtitle reference cache.");
    references.Add(reference);
   }
   if(references.Count>0&&(string.IsNullOrWhiteSpace(apiKey)||cached.Text("retrieval_policy")==RetrievalPolicy&&cached.Bool("complete")&&CoveredEpisodes(references,catalog).Count==catalog.Select(e=>e.Id).Distinct().Count()&&cached.Text("guide_hash")==guideHash&&DateTimeOffset.TryParse(cached.Text("updated"),out var updated)&&DateTimeOffset.UtcNow-updated<TimeSpan.FromDays(7))) {
    var checkedCache=ReferenceValidation.Check(references,catalog);
    var coverage=CoverageNote(checkedCache.References,catalog)+" "+checkedCache.Note;
    progress.Report($"Reusing {checkedCache.References.Count} saved English subtitle references for {series.Name}. "+coverage);return new(series,checkedCache.References,coverage.Trim());
   }
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException){references.Clear();cached=null;}
  if(string.IsNullOrWhiteSpace(apiKey))return Checked("No SubDL key configured; unresolved files remain available for review or optional AI fallback.");
  using var api=new SubdlApi(apiKey,apiHandler?.Invoke());
  bool complete=true;var notes=new List<string>();int searches=0,downloads=0,referenceRevision=0,coverageRevision=-1;var visited=new HashSet<string>();HashSet<int> coverageIds=[];
  HashSet<int> Coverage() {
   if(coverageRevision!=referenceRevision){coverageIds=CoveredEpisodes(references,catalog);coverageRevision=referenceRevision;}
   return coverageIds;
  }
  try {
   progress.Report("Verifying subtitle series identity with the configured metadata provider…");
   var imdb=await SubtitleSeriesLookup.ImdbAsync(series,metadataKey,metadataHandler,ct);
   var found=await SubtitleSearchCache.Get(root,$"subdl-title:{RetrievalPolicy}:{imdb}",()=>api.Titles(imdb,ct),r=>r["results"] is JsonArray rows&&rows.Count>0&&rows.All(x=>x.Text("type")=="tv"&&x.Text("imdb_id")==imdb&&SubdlIdentity.Normalize(x?["sd_id"])!=null&&SubdlIdentity.Matches(x,SubdlIdentity.Normalize(x?["sd_id"])!,imdb,series)),progress,ct);
   var choices=found.Array("results").Where(r=>r.Text("type")=="tv"&&r.Text("imdb_id")==imdb).GroupBy(r=>SubdlIdentity.Normalize(r?["sd_id"])).Where(g=>g.Key!=null).ToArray();
   if(choices.Length!=1||choices[0].Any(r=>!SubdlIdentity.Matches(r,choices[0].Key!,imdb,series)))return Checked("Subtitle series identity is ambiguous; unresolved files remain available for review.");
   var sdId=choices[0].Key!;
   var seasons=catalog.Select(e=>e.Season).Distinct().Order().ToArray();
   if(seasons.Length>30)return Checked("Series exceeds the automatic subtitle retrieval limit; unresolved files remain available for review.");
   foreach(var season in seasons) {
    ct.ThrowIfCancellationRequested();progress.Report($"Loading subtitle references for {series.Name}, season {season:00}…");
    if(!await SearchAndImport(season,null))break;
    // Number labels guide retrieval only; they never establish provider identity.
    var covered=Coverage();
    var missing=catalog.Where(e=>e.Season==season&&!covered.Contains(e.Id)).Select(e=>e.Number).Distinct().Order().ToArray();
    if(missing.Length>0)progress.Report($"Checking individual English subtitle releases for {missing.Length} missing season {season:00} reference label(s)…");
    foreach(int episode in missing) {
     if(catalog.Where(e=>e.Season==season&&e.Number==episode).All(e=>covered.Contains(e.Id)))continue;
     if(!await SearchAndImport(season,episode))break;
     covered=Coverage();
    }
   }
   async Task<bool> SearchAndImport(int season,int? episode) {
    if(searches>=60||downloads>=48){complete=false;if(!notes.Any(n=>n.Contains("retrieval limit")))notes.Add("The bounded subtitle retrieval limit was reached; missing coverage remains visible.");return false;}
    if(episode.HasValue)progress.Report($"Searching English subtitle releases for S{season:00}E{episode.Value:00} reference coverage…");
    var search=await SubtitleSearchCache.Get(root,$"subdl:{RetrievalPolicy}:{sdId}:{season}:{episode}",()=>{searches++;return api.Subtitles(sdId,season,ct,episode);},
     r=>r["subtitles"] is JsonArray&&(r["results"]==null||r["results"] is JsonArray)&&r.Array("results").All(x=>SubdlIdentity.Matches(x,sdId,imdb,series)),progress,ct);
    // Some endpoints broaden empty searches. Reject explicit mismatched series
    // results instead of trusting the request parameters to describe the response.
    if(search.Array("results").Any(r=>!SubdlIdentity.Matches(r,sdId,imdb,series))) {
     complete=false;notes.Add("A subtitle search returned another series and was ignored.");return true;
    }
    var packs=new List<(Uri Url,JsonObject Row,string[] Files,int Titles)>();
    foreach(var row in search.Array("subtitles").OfType<JsonObject>()) {
     if(!English(row.Text("lang",row.Text("language"))))continue;
     try {
      var files=row.Array("unpack_files").Select(n=>n is JsonValue?n.ToString():n.Text("name",n.Text("filename"))).ToArray();
      var titles=files.Count(name=>TitleMapping.Find(name,catalog,"")!=null);
      packs.Add((SubdlApi.DownloadUri(row),row,files,titles));
     }catch(Exception ex) when(ex is InvalidDataException or UriFormatException){complete=false;}
    }
    // Include numbered single-episode releases as well as packs. The old
    // largest-pack-plus-titled-only selection silently discarded these.
    foreach(var pack in packs.OrderByDescending(p=>p.Titles).ThenByDescending(p=>p.Files.Length).ThenBy(p=>p.Url.AbsolutePath,StringComparer.Ordinal)) {
     ct.ThrowIfCancellationRequested();var url=pack.Url.GetLeftPart(UriPartial.Path);
     for(int i=0;i<references.Count;i++)if(references[i].Url==url) {
      var old=references[i];var names=MetadataTitles(pack.Row,old.Name,pack.Files.Length);
      if(names.Except(old.TitleNames??[],StringComparer.OrdinalIgnoreCase).Any()) {
       referenceRevision++;
       references[i]=old with{TitleNames=(old.TitleNames??[]).Concat(names).Distinct(StringComparer.OrdinalIgnoreCase).ToList()};
       Data.Save(Path.Combine(root,"references",old.Id+".json"),references[i]);
      }
     }
     if(references.Any(r=>r.Url==url)||!visited.Add(url))continue;
     if(downloads>=48){complete=false;notes.Add("The bounded subtitle retrieval limit was reached; missing coverage remains visible.");return false;}
     downloads++;
     try {
      var bytes=await api.Download(pack.Url,ct);var archive=Convert.ToHexStringLower(SHA256.HashData(bytes));
      using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
      if(zip.Entries.Count>150||zip.Entries.Sum(e=>e.Length)>24*1024*1024)throw new InvalidDataException("Subtitle archive exceeds limits.");
      var imported=new List<SubtitleReference>();
      foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase))) {
       var metadata=pack.Row.Array("unpack_files").OfType<JsonObject>().Where(f=>SameEntry(f.Text("name",f.Text("filename")),entry.FullName)).ToArray();
       if(metadata.Any(f=>!English(f.Text("language",f.Text("lang")))))continue;
       ct.ThrowIfCancellationRequested();using var stream=entry.Open();var text=SubtitleText.Decode(await SubdlApi.Bounded(stream,2*1024*1024,ct));
       var cues=MediaPipeline.ParseSrt(text.Text);if(cues.Count<20)continue;
       var id=CollectionResolver.Hash(series.Key+"|"+archive+"|"+entry.FullName);
       imported.Add(new(id,entry.FullName,url,archive,season,cues,MetadataTitles(pack.Row,entry.FullName,zip.Entries.Count(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase)))));
      }
      foreach(var reference in imported.Where(r=>references.All(old=>old.Id!=r.Id))) {
       Data.Save(Path.Combine(root,"references",reference.Id+".json"),reference);references.Add(reference);referenceRevision++;
      }
      if(imported.Count==0){complete=false;notes.Add("A subtitle download contained no usable English SRT files.");}
      Save(false);
     }catch(SubdlAccessException ex) when(ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone){complete=false;notes.Add("An unavailable subtitle download was skipped; other releases were checked.");}
     catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException){complete=false;notes.Add("An unusable subtitle archive was skipped; other releases were checked.");}
    }
    return true;
   }
  }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException) {
   complete=false;notes.Add("Subtitle lookup was unavailable; saved references were retained and unresolved files remain available for review.");
  }
  ct.ThrowIfCancellationRequested();
  if(Coverage().Count!=catalog.Select(e=>e.Id).Distinct().Count())complete=false;
  Save(complete);
  var validation=ReferenceValidation.Check(references,catalog);
  notes.Add(CoverageNote(validation.References,catalog));notes.Add(validation.Note);
  var note=string.Join(" ",notes.Distinct());progress.Report($"{validation.References.Count} English subtitle references available. "+note);return new(series,validation.References,note);
  SubtitleReferenceSet Checked(string note) {
   var result=ReferenceValidation.Check(references,catalog);return new(series,result.References,note+" "+result.Note);
  }
  void Save(bool finished) {
   var checkedReferences=ReferenceValidation.Check(references,catalog);
   Data.Save(manifestPath,new{series,guideHash,retrievalPolicy=RetrievalPolicy,validationRevision=ReferenceValidation.Revision,quarantined=checkedReferences.Rejected,complete=finished&&checkedReferences.Rejected.Count==0,updated=DateTimeOffset.UtcNow,references=references.Select(r=>r.Id)});
  }
 }
 internal static HashSet<int> CoveredEpisodes(IReadOnlyList<SubtitleReference> references,IReadOnlyList<Episode> catalog) {
  var checkedReferences=ReferenceValidation.Check(references,catalog).References;
  var entries=checkedReferences.Select(r=>{var link=ReferenceTitles.Find(r,catalog,out bool conflict);return (Reference:r,Label:TitleMapping.Label(r.Name),Index:TranscriptComparison.Index(r.Cues),Link:link,Conflict:conflict);}).ToArray();
  var common=ReferenceMatcher.Common(entries.Select(e=>e.Index).ToArray());var covered=new HashSet<int>();
  foreach(var entry in entries) {
   var link=entry.Link;if(entry.Conflict)continue;
   if(link?.Complete==true) {
    if(entries.Any(other=>other.Reference.Id!=entry.Reference.Id&&other.Link!=null&&other.Link.Key!=link.Key&&ReferenceMatcher.SameContent(entry.Index,other.Index,common)))continue;
    covered.UnionWith(link.Episodes.Select(e=>e.Id));continue;
   }
   if(link!=null||entry.Label==null||entry.Label.Part>0)continue;
   // Different numbered files containing the same story are not evidence that
   // both episode slots have references. Ask for alternative releases.
   if(entries.Any(other=>other.Reference.Id!=entry.Reference.Id&&other.Label!=null&&
    (other.Label.Season,other.Label.First,other.Label.Last)!=(entry.Label.Season,entry.Label.First,entry.Label.Last)&&ReferenceMatcher.SameContent(entry.Index,other.Index,common)))continue;
   covered.UnionWith(catalog.Where(e=>e.Season==entry.Label.Season&&e.Number>=entry.Label.First&&e.Number<=entry.Label.Last).Select(e=>e.Id));
  }
  return covered;
 }
 static bool English(string value)=>value.Trim().ToLowerInvariant() is "" or "en" or "eng" or "english";
 static bool SameEntry(string a,string b)=>a.Replace('\\','/').Equals(b.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
 static List<string> MetadataTitles(JsonObject row,string entry,int fileCount) {
  var matches=row.Array("unpack_files").OfType<JsonObject>().Where(f=>SameEntry(f.Text("name",f.Text("filename")),entry)).ToArray();
  // Never spread a season-pack title or another entry's metadata across a ZIP.
  IEnumerable<string> names=matches.Select(f=>f.Text("release_name"));
  if(fileCount==1&&row.Array("unpack_files").Count<=1)names=names.Append(row.Text("release_name"));
  return names.Where(n=>!string.IsNullOrWhiteSpace(n)&&n.Length<=512).Select(n=>n.EndsWith(".srt",StringComparison.OrdinalIgnoreCase)?n:n+".srt").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
 }
 static int SeasonReferences(IEnumerable<SubtitleReference> references,int season)=>references.Count(r=>(TitleMapping.Label(r.Name)?.Season??r.Season)==season);
 static string CoverageNote(IReadOnlyList<SubtitleReference> references,IReadOnlyList<Episode> catalog) {
  var seasons=catalog.Select(e=>e.Season).Distinct().Order().ToArray();
  var covered=seasons.Select(s=>(Season:s,Count:SeasonReferences(references,s))).ToArray();
  var available=covered.Where(s=>s.Count>0).Select(s=>$"{s.Season:00}: {s.Count}");
  var missing=covered.Where(s=>s.Count==0).Select(s=>s.Season.ToString("00")).ToArray();
  var note=available.Any()?"English reference files by subtitle season — "+string.Join("; ",available)+".":"";
  if(missing.Length>0)note+=" No English reference files loaded for subtitle season(s) "+string.Join(", ",missing)+"; unresolved files remain available for review.";
  var mapped=references.Select(r=>ReferenceTitles.Find(r,catalog,out _)).Where(l=>l?.Complete==true).SelectMany(l=>l!.Episodes).Select(e=>e.Id).Distinct().Count();
  note+=$" {mapped} of {catalog.Count} provider episodes have title-linked references. File counts do not establish provider episode coverage.";
  return note.Trim();
 }

}
