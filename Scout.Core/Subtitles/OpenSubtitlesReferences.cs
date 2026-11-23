using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scout.Core.Subtitles;

public sealed class OpenSubtitlesReferences(string cacheRoot,OpenSubtitlesCredentials? credentials,Func<HttpMessageHandler>? apiHandler=null,Func<HttpMessageHandler>? metadataHandler=null):ISupplementalSubtitleReferenceSource {
 const string Revision="opensubtitles-references-v1";
 static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
 public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>LoadAsync(series,catalog,[],progress,ct);
 public async Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IReadOnlyList<SubtitleReference> existing,IProgress<string> progress,CancellationToken ct) {
  series.Validate();var gate=Gates.GetOrAdd(Path.GetFullPath(cacheRoot),_=>new(1,1));await gate.WaitAsync(ct);
  try{return await LoadCore(series,catalog,existing,progress,ct);}finally{gate.Release();}
 }
 async Task<SubtitleReferenceSet> LoadCore(SeriesIdentity series,IReadOnlyList<Episode> catalog,IReadOnlyList<SubtitleReference> existing,IProgress<string> progress,CancellationToken ct) {
  ct.ThrowIfCancellationRequested();var root=Path.Combine(cacheRoot,Revision,CollectionResolver.Hash(series.Key));var manifestPath=Path.Combine(root,"manifest.json");
  var guideHash=SubtitleResult.CatalogHash(catalog);var references=new List<SubtitleReference>();var notes=new List<string>();JsonObject? cached=null;
  if(ScoutStorage.Exists(manifestPath))try {
   cached=Data.Read(manifestPath);if(cached["series"]?.Deserialize<SeriesIdentity>(Data.Options)?.Key!=series.Key)throw new InvalidDataException("Reference cache series mismatch.");
   foreach(var entry in cached.Array("references")) {
    var id=entry!.GetValue<string>();if(!Regex.IsMatch(id,"^[0-9a-f]{64}$"))throw new InvalidDataException("Invalid cached reference ID.");
    var reference=Data.Read(Path.Combine(root,"references",id+".json")).Deserialize<SubtitleReference>(Data.Options);
    if(reference==null||reference.Id!=id||reference.Cues==null)throw new InvalidDataException("Invalid cached reference.");references.Add(reference);
   }
   if(credentials==null||cached.Bool("complete")&&cached.Text("guide_hash")==guideHash&&DateTimeOffset.TryParse(cached.Text("updated"),out var updated)&&DateTimeOffset.UtcNow-updated<TimeSpan.FromDays(1)) {
    notes.Add("Reusing saved OpenSubtitles references. "+cached.Text("note"));return Finish();
   }
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException){references.Clear();cached=null;}
  if(credentials==null){notes.Add("API key not configured; saved references remain available.");return Finish();}
  credentials.Validate();
  // A quota/authentication pause is shared across Scouts, but scoped to the
  // credentials so correcting an account does not inherit an old rejection.
  var account=CollectionResolver.Hash(JsonSerializer.Serialize(credentials,Data.Options));var limitPath=Path.Combine(cacheRoot,Revision,"limits",account+".json");
  if(ScoutStorage.Exists(limitPath))try {
   var limit=Data.Read(limitPath);if(DateTimeOffset.TryParse(limit.Text("retry_after"),out var retry)&&retry>DateTimeOffset.UtcNow){notes.Add(limit.Text("reason")+" Retry after "+retry.ToLocalTime().ToString("g")+".");return Finish();}
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException){/* Invalid local limit record does not block a fresh check. */}
  bool complete=true;int searches=0,downloads=0;var attempted=new HashSet<long>();
  try {
   string parent=series.Provider=="tmdb"?"parent_tmdb_id":"parent_imdb_id";
   string id=series.Provider=="tmdb"?series.Id:(await SubtitleSeriesLookup.ImdbAsync(series,null,metadataHandler,ct))[2..].TrimStart('0');
   using var api=new OpenSubtitlesApi(credentials,apiHandler?.Invoke());
   var seasons=catalog.Select(e=>e.Season).Distinct().OrderBy(s=>existing.Any(r=>r.Season==s)?1:0).ThenBy(s=>s).ToArray();
   if(seasons.Length>30)throw new InvalidDataException("OpenSubtitles automatic season limit exceeded.");
   var covered=ReferenceValidation.Check(existing.Concat(references).ToList(),catalog).References.Select(r=>ReferenceTitles.Find(r,catalog,out _)).Where(l=>l?.Complete==true).Select(l=>l!.Key).ToHashSet();
   foreach(int season in seasons) {
    progress.Report($"OpenSubtitles: checking English references for season {season:00}…");int pages=1;
    for(int page=1;page<=pages;page++) {
     ct.ThrowIfCancellationRequested();if(searches>=60||downloads>=48){complete=false;notes.Add("OpenSubtitles retrieval budget reached; missing coverage remains visible.");goto Done;}
     searches++;var result=await api.Search(parent,id,season,page,ct);
     if(result["data"] is not JsonArray data||data.Count>1000)throw new InvalidDataException("Invalid OpenSubtitles search results.");
     pages=Math.Clamp(result.Int("total_pages",1),1,1000);
     if(result.Int("page",page)!=page)throw new InvalidDataException("OpenSubtitles returned a different results page.");
     foreach(var item in data) {
      if(item?["attributes"] is not JsonObject attributes||attributes.Text("language")!="en"||attributes.Bool("foreign_parts_only")||attributes.Bool("machine_translated")||attributes.Bool("ai_translated"))continue;
      var feature=attributes["feature_details"];
      if(feature.Text("feature_type")!="Episode"||Number(feature?[parent])!=id){notes.Add("References with another or unverified series identity were ignored.");continue;}
      // Upload numbering is used to find references, never to assign TMDB episodes.
      var episodeId=series.Provider=="tmdb"?Number(feature?["tmdb_id"]):null;
      var episode=episodeId==null?null:catalog.SingleOrDefault(e=>e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)==episodeId);
      if(episodeId!=null&&episode==null)continue; // Outside the selected provider catalog.
      var files=attributes.Array("files");
      if(files.Count!=1||files[0].Int("cd_number",1)>1){notes.Add("Split subtitle files were skipped because their whole-episode coverage is unverified.");continue;}
      var file=files[0];if(!long.TryParse(Number(file?["file_id"]),out var fileId)||!attempted.Add(fileId))continue;
      var subtitleId=Number(item?["id"]);if(subtitleId==null)continue;
      var name=file.Text("file_name");if(string.IsNullOrWhiteSpace(name)||name.Length>512)continue;
      name=Path.GetFileName(name.Replace('/',Path.DirectorySeparatorChar));
      var titles=new[]{feature.Text("title"),attributes.Text("release")}.Where(t=>!string.IsNullOrWhiteSpace(t)&&t.Length<=512).Select(t=>t.EndsWith(".srt",StringComparison.OrdinalIgnoreCase)?t:t+".srt").ToList();
      if(episode!=null) {
       // Conflicting uploaded titles cannot be silently overwritten by an ID.
       var links=new[]{name}.Concat(titles).Select(t=>TitleMapping.Find(t,catalog,"")).OfType<ProviderLink>().ToArray();
       if(links.Any(l=>!l.Episodes.Any(e=>e.Id==episode.Id))){notes.Add("Conflicting OpenSubtitles episode identities were ignored.");continue;}
       // A bare first-part title stays ambiguous when the provider splits a story.
       titles.Add(episode.Title+".srt");
      }
      var referenceId=CollectionResolver.Hash("opensubtitles:"+series.Key+":"+fileId);
      var reference=new SubtitleReference(referenceId,name,"https://www.opensubtitles.com/en/subtitles/"+subtitleId,
       "opensubtitles:"+series.Key+":"+season,season,[],titles.Distinct().ToList());
      var old=references.FindIndex(r=>r.Id==referenceId);
      if(old>=0){references[old]=reference with{Cues=references[old].Cues};Data.Save(Path.Combine(root,"references",referenceId+".json"),references[old]);continue;}
      var link=ReferenceTitles.Find(reference,catalog,out bool conflict);if(conflict){notes.Add("Conflicting OpenSubtitles title metadata was ignored.");continue;}
      if(link?.Complete==true&&covered.Contains(link.Key))continue;
      if(downloads>=48){complete=false;notes.Add("OpenSubtitles download budget reached.");goto Done;}
      downloads++;
      try {
       var bytes=await api.Download(fileId,ct);var cues=MediaPipeline.ParseSrt(SubtitleText.Decode(bytes).Text);
       if(cues.Count==0){complete=false;notes.Add("An OpenSubtitles file contained no readable SRT dialogue.");}
       else {
        reference=reference with{Cues=cues};var valid=ReferenceValidation.Check([reference],catalog);
        if(valid.References.Count==1){ct.ThrowIfCancellationRequested();references.Add(reference);Data.Save(Path.Combine(root,"references",referenceId+".json"),reference);if(link?.Complete==true)covered.Add(link.Key);Save(false);}
        else{complete=false;notes.Add("An invalid OpenSubtitles reference was quarantined.");}
       }
      }catch(OpenSubtitlesAccessException ex) when(ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone){complete=false;notes.Add("An unavailable OpenSubtitles file was skipped.");}
      catch(Exception ex) when(ex is InvalidDataException or System.Text.DecoderFallbackException){complete=false;notes.Add("An unusable OpenSubtitles file was skipped.");}
      if(api.Remaining==0){complete=false;Limit("OpenSubtitles download quota reached.",api.ResetAt??DateTimeOffset.UtcNow.AddDays(1));goto Done;}
     }
    }
   }
  }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(OpenSubtitlesAccessException ex) {
   complete=false;notes.Add(ex.Message);
   if(ex.Status is HttpStatusCode.NotAcceptable or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    Limit(ex.Message,ex.RetryAfter??(ex.Status==HttpStatusCode.NotAcceptable?DateTimeOffset.UtcNow.AddDays(1):DateTimeOffset.UtcNow.AddMinutes(15)));
  }
  catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException){complete=false;notes.Add("OpenSubtitles lookup was unavailable; saved references were retained.");}
  Done:ct.ThrowIfCancellationRequested();Save(complete);return Finish();
  void Limit(string reason,DateTimeOffset retry){notes.Add(reason);var now=DateTimeOffset.UtcNow;if(retry<=now)retry=now.AddMinutes(1);if(retry>now.AddDays(2))retry=now.AddDays(2);Data.Save(limitPath,new{reason,retryAfter=retry});}
  void Save(bool finished)=>Data.Save(manifestPath,new{series,guideHash,complete=finished&&ReferenceValidation.Check(references,catalog).Rejected.Count==0,updated=DateTimeOffset.UtcNow,note=string.Join(" ",notes.Distinct()),references=references.Select(r=>r.Id)});
  SubtitleReferenceSet Finish() {
   var valid=ReferenceValidation.Check(references,catalog);notes.Add(valid.Note);var note=string.Join(" ",notes.Where(n=>n.Length>0).Distinct());
   progress.Report($"OpenSubtitles: {valid.References.Count} English references available. "+note);return new(series,valid.References,note);
  }
 }
 static string? Number(JsonNode? node) {
  if(node is not JsonValue)return null;var text=node.ToString().TrimStart('0');return Regex.IsMatch(text,"^[1-9][0-9]{0,18}$")?text:null;
 }
}
