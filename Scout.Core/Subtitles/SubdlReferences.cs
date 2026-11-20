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
 const string RetrievalPolicy="packs-and-titled-episodes-v2";
 public async Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct) {
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
    if(reference.Id!=id||reference.Cues.Count==0)throw new InvalidDataException("Incomplete subtitle reference cache.");
    references.Add(reference);
   }
   if(references.Count>0&&(string.IsNullOrWhiteSpace(apiKey)||cached.Text("retrieval_policy")==RetrievalPolicy&&cached.Bool("complete")&&catalog.Select(e=>e.Season).Distinct().All(s=>SeasonReferences(references,s)>0)&&cached.Text("guide_hash")==guideHash&&DateTimeOffset.TryParse(cached.Text("updated"),out var updated)&&DateTimeOffset.UtcNow-updated<TimeSpan.FromDays(7))) {
    var coverage=CoverageNote(references,catalog);
    progress.Report($"Reusing {references.Count} saved English subtitle references for {series.Name}. "+coverage);return new(series,references,coverage);
   }
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException){references.Clear();cached=null;}
  if(string.IsNullOrWhiteSpace(apiKey))return new(series,references,"No SubDL key configured; using AI for unresolved dialogue.");
  using var api=new SubdlApi(apiKey,apiHandler?.Invoke());
  bool complete=true;var notes=new List<string>();
  try {
   progress.Report("Verifying subtitle series identity with the configured metadata provider…");
   var imdb=await ImdbAsync(series,ct);
   var found=await api.Titles(imdb,ct);
   var choices=found.Array("results").Where(r=>r.Text("type")=="tv"&&r.Text("imdb_id")==imdb).GroupBy(r=>r?["sd_id"]?.ToString()).Where(g=>!string.IsNullOrWhiteSpace(g.Key)).ToArray();
   if(choices.Length!=1)return new(series,references,"Subtitle series identity is ambiguous; using AI for unresolved dialogue.");
   var sdId=choices[0].Key!;
   var seasons=catalog.Select(e=>e.Season).Distinct().Order().ToArray();
   if(seasons.Length>30)return new(series,references,"Series exceeds the automatic subtitle retrieval limit; using AI for unresolved dialogue.");
   foreach(var season in seasons) {
    ct.ThrowIfCancellationRequested();progress.Report($"Loading subtitle references for {series.Name}, season {season:00}…");
    var search=await api.Subtitles(sdId,season,ct);
    var packs=new List<(Uri Url,int Files,int Titles)>();
    foreach(var row in search.Array("subtitles").OfType<JsonObject>()) {
     if(row.Text("lang",row.Text("language")).ToLowerInvariant() is not ("" or "en" or "eng" or "english"))continue;
     try {
      var files=row.Array("unpack_files").Select(n=>n is JsonValue?n.ToString():n.Text("name",n.Text("filename"))).ToArray();
      packs.Add((SubdlApi.DownloadUri(row),files.Length,files.Count(name=>TitleMapping.Find(name,catalog,"")!=null)));
     }catch(InvalidDataException){/* An unsupported download is never followed. */}
    }
    var basePack=packs.OrderByDescending(p=>p.Files).ThenBy(p=>p.Url.AbsolutePath,StringComparer.Ordinal).Take(1);
    var titlePacks=packs.Where(p=>p.Titles>0).OrderByDescending(p=>p.Titles).ThenBy(p=>p.Url.AbsolutePath,StringComparer.Ordinal).Take(8);
    var selected=basePack.Concat(titlePacks).DistinctBy(p=>p.Url.AbsolutePath).ToArray();
    // A pack indexed under one season can contain several seasons. Assess the
    // actual reference files after all packs have loaded, not each search page.
    if(selected.Length==0)continue;
    foreach(var pack in selected) {
     if(references.Any(r=>r.Url==pack.Url.GetLeftPart(UriPartial.Path)))continue;
     try {
      var bytes=await api.Download(pack.Url,ct);var archive=Convert.ToHexStringLower(SHA256.HashData(bytes));
      using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
      if(zip.Entries.Count>150||zip.Entries.Sum(e=>e.Length)>24*1024*1024)throw new InvalidDataException("Subtitle archive exceeds limits.");
      var imported=new List<SubtitleReference>();
      foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase))) {
       ct.ThrowIfCancellationRequested();using var stream=entry.Open();var text=SubtitleText.Decode(await SubdlApi.Bounded(stream,2*1024*1024,ct));
       var cues=MediaPipeline.ParseSrt(text.Text);if(cues.Count<20)continue;
       var id=CollectionResolver.Hash(series.Key+"|"+archive+"|"+entry.FullName);
       imported.Add(new(id,entry.FullName,pack.Url.GetLeftPart(UriPartial.Path),archive,season,cues));
      }
      foreach(var reference in imported.Where(r=>references.All(old=>old.Id!=r.Id))) {
       Data.Save(Path.Combine(root,"references",reference.Id+".json"),reference);references.Add(reference);
      }
      // Keep successful downloads if a later request fails or the Scout pauses.
      Save(false);
     }catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException){complete=false;notes.Add("An unusable subtitle archive was skipped.");}
    }
   }
  }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException) {
   complete=false;notes.Add("Subtitle lookup was unavailable; saved references were retained and unresolved files will use AI.");
  }
  ct.ThrowIfCancellationRequested();
  if(catalog.Select(e=>e.Season).Distinct().Any(s=>SeasonReferences(references,s)==0))complete=false;
  Save(complete);
  notes.Add(CoverageNote(references,catalog));
  var note=string.Join(" ",notes);progress.Report($"{references.Count} English subtitle references available. "+note);return new(series,references,note);
  void Save(bool finished)=>Data.Save(manifestPath,new{series,guideHash,retrievalPolicy=RetrievalPolicy,complete=finished,updated=DateTimeOffset.UtcNow,references=references.Select(r=>r.Id)});
 }
 static int SeasonReferences(IEnumerable<SubtitleReference> references,int season)=>references.Count(r=>(TitleMapping.Label(r.Name)?.Season??r.Season)==season);
 static string CoverageNote(IReadOnlyList<SubtitleReference> references,IReadOnlyList<Episode> catalog) {
  var seasons=catalog.Select(e=>e.Season).Distinct().Order().ToArray();
  var covered=seasons.Select(s=>(Season:s,Count:SeasonReferences(references,s))).ToArray();
  var available=covered.Where(s=>s.Count>0).Select(s=>$"{s.Season:00}: {s.Count}");
  var missing=covered.Where(s=>s.Count==0).Select(s=>s.Season.ToString("00")).ToArray();
  var note=available.Any()?"English reference files by subtitle season — "+string.Join("; ",available)+".":"";
  if(missing.Length>0)note+=" No English reference files loaded for subtitle season(s) "+string.Join(", ",missing)+"; unresolved dialogue will use AI.";
  return note.Trim();
 }
 async Task<string> ImdbAsync(SeriesIdentity series,CancellationToken ct) {
  using var http=new HttpClient(metadataHandler?.Invoke()??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
  if(series.Provider=="tmdb") {
   if(string.IsNullOrWhiteSpace(metadataKey))throw new InvalidOperationException("Metadata credential unavailable.");
   using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.themoviedb.org/3/tv/{series.Id}/external_ids");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",metadataKey);
   using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
   if(body["id"]?.ToString()!=series.Id)throw new InvalidDataException("Metadata series mismatch.");return Imdb(body.Text("imdb_id"));
  }
  var url=new Uri(series.Provider=="tvdb"?$"https://api.tvmaze.com/lookup/shows?thetvdb={series.Id}":$"https://api.tvmaze.com/shows/{series.Id}");
  using var lookup=await http.GetAsync(url,ct);JsonNode show;
  if(lookup.StatusCode==HttpStatusCode.MovedPermanently) {
   var target=new Uri(url,lookup.Headers.Location??throw new InvalidDataException("Missing metadata redirect."));
   if(target.Scheme!="https"||target.Host!="api.tvmaze.com"||!target.IsDefaultPort||target.UserInfo!=""||target.Query!=""||target.Fragment!=""||!Regex.IsMatch(target.AbsolutePath,@"^/shows/[1-9]\d*$"))throw new InvalidDataException("Invalid metadata redirect.");
   using var response=await http.GetAsync(target,ct);response.EnsureSuccessStatusCode();show=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
  }else {lookup.EnsureSuccessStatusCode();show=JsonNode.Parse(await lookup.Content.ReadAsStringAsync(ct))!;}
  if((series.Provider=="tvdb"?show["externals"]?["thetvdb"]:show["id"])?.ToString()!=series.Id)throw new InvalidDataException("Subtitle external series identity mismatch.");
  return Imdb(show["externals"].Text("imdb"));
 }
 static string Imdb(string value)=>Regex.IsMatch(value,@"^tt\d+$")?value:throw new InvalidDataException("No verified IMDb series identity.");
}
