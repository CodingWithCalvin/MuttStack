using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Scout.Core;

static class ApiReferenceSearch {
 public static async Task Load(JsonNode fixture,List<Reference> references,JsonArray downloads,JsonArray failures,Action<string,object> save,CancellationToken ct,string? databasePath=null,string? settingsRoot=null) {
  var database=databasePath??Environment.GetEnvironmentVariable("SCOUT_DOGGO_DATABASE")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Scout","scout.db");
  var settings=settingsRoot??Environment.GetEnvironmentVariable("SCOUT_SETTINGS")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial");
  if(!Path.IsPathFullyQualified(database)||!Path.IsPathFullyQualified(settings))throw new InvalidOperationException("The Scout database and settings root must be absolute paths.");
  var key=SubtitleCredentials.ReadSubdl(database,settings);
  if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Save a SubDL key in Scout Settings > Subtitles before running this test.");
  using var api=new SubdlApi(key);
  string Label(string value)=>Regex.Replace(value.ToLowerInvariant(),@"[^\p{L}\p{N}]","");
  string Value(JsonNode? value)=>value is JsonValue v?v.ToString():"";
  var series=fixture.Array("cases").GroupBy(c=>c.Text("series")).OrderBy(g=>g.Key,StringComparer.Ordinal).ToArray();
  foreach(var group in series) {
   Console.WriteLine("SubDL series lookup: "+group.Key);
   var found=await api.Titles(group.Key,ct);
   var matches=found.Array("results").OfType<JsonObject>().Where(r=>r.Text("type")=="tv"&&(Label(r.Text("name"))==Label(group.Key)||Label(r.Text("original_name"))==Label(group.Key)))
    .GroupBy(r=>Value(r["sd_id"])).Where(g=>g.Key!="").ToArray();
   if(matches.Length!=1){failures.Add(new JsonObject{["page"]="https://api.subdl.com/api/v2/movies/search",["error"]="Series identity was ambiguous or absent for "+group.Key+"; no subtitles chosen."});continue;}
   var identity=matches[0].First();var id=matches[0].Key;
   save("search/"+CollectionResolver.Hash(group.Key)+"-identity.json",new{series=group.Key,sdId=id,name=identity.Text("name"),imdbId=Value(identity["imdb_id"]),tmdbId=Value(identity["tmdb_id"]),method="unique-exact-normalized-TV-series-name",providerMappingVerified=false});
   // Search every season in the saved guide, not the input filename or a saved
   // expected episode. One deterministic season pack supplies negative controls.
   var seasons=group.SelectMany(c=>c.Array("catalog")).Select(e=>e.Int("season")).Distinct().Order().ToArray();
   if(seasons.Length>30)throw new InvalidDataException("The reference test is limited to 30 seasons per series.");
   foreach(int season in seasons) {
    await Task.Delay(500,ct);Console.WriteLine($"SubDL reference search: {group.Key}, season {season}.");
    var response=await api.Subtitles(id,season,ct);
    var packs=new List<(JsonObject Row,Uri Url)>();
    foreach(var row in response.Array("subtitles").OfType<JsonObject>()) {
     var language=row.Text("lang",row.Text("language"));
     if(language!=""&&language.ToLowerInvariant() is not ("en" or "eng" or "english"))continue;
     try {packs.Add((row,SubdlApi.DownloadUri(row)));}
     catch(InvalidDataException){failures.Add(new JsonObject{["page"]="https://api.subdl.com/api/v2/subtitles/search",["error"]="An unsupported reference URL was excluded."});}
    }
    var selected=packs.OrderByDescending(p=>p.Row.Array("unpack_files").Count).ThenBy(p=>p.Url.AbsolutePath,StringComparer.Ordinal).FirstOrDefault();
    save($"search/{CollectionResolver.Hash(group.Key)}/season-{season}.json",new{series=group.Key,season,candidates=packs.Count,selection="most listed files, then stable download path; no dialogue/filename/reference labels used",selected=selected.Url==null?null:new UriBuilder(selected.Url){Query=""}.Uri.AbsoluteUri});
    if(selected.Url==null){failures.Add(new JsonObject{["page"]="https://api.subdl.com/api/v2/subtitles/search",["error"]=$"No English season pack available for {group.Key}, season {season}."});continue;}
    try {
     var bytes=await api.Download(selected.Url,ct);var sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
     save("archives/"+sha+".json",new{sha256=sha,bytes,series=group.Key,season,url=new UriBuilder(selected.Url){Query=""}.Uri.AbsoluteUri});
     using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
     if(zip.Entries.Count>150||zip.Entries.Sum(e=>e.Length)>24*1024*1024)throw new InvalidDataException("The subtitle archive exceeds the bounded test limits.");
     var imported=new List<Reference>();
     foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase))) {
      if(entry.Length>2*1024*1024)throw new InvalidDataException("Oversized subtitle entry.");
      using var input=entry.Open();var raw=await SubdlApi.Bounded(input,2*1024*1024,ct);
      var decoded=SubtitleText.Decode(raw);var cues=MediaPipeline.ParseSrt(decoded.Text);if(cues.Count<20)continue;
      var referenceId=CollectionResolver.Hash(group.Key+"|"+sha+"|"+entry.FullName);
      var url=new UriBuilder(selected.Url){Query=""}.Uri.AbsoluteUri;
      var reference=new Reference(referenceId,group.Key,entry.FullName,url,TranscriptComparison.Index(cues.Select(c=>c.Text)));
      imported.Add(reference);save("references/"+referenceId+".json",new{reference.Id,reference.Series,reference.Name,reference.Url,zipSha256=sha,cues,encoding=decoded.Encoding,subdlSeries=id,season,labelVerified=false});
     }
     if(imported.Count==0)throw new InvalidDataException("The season archive contained no usable SRT dialogue.");
     references.AddRange(imported.Where(r=>references.All(old=>old.Id!=r.Id)));downloads.Add(new JsonObject{["series"]=group.Key,["season"]=season,["page"]="https://subdl.com",["download"]=new UriBuilder(selected.Url){Query=""}.Uri.AbsoluteUri,["sha256"]=sha,["subtitle_files"]=imported.Count});
     Console.WriteLine($"Imported {imported.Count} reference subtitles for {group.Key}, season {season}.");
    }catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException) {
     failures.Add(new JsonObject{["page"]="https://api.subdl.com/api/v2/subtitles/search",["error"]=$"Unusable season pack for {group.Key}, season {season}: "+ex.Message});
    }
    save("downloads.json",new{downloads,failures});
   }
  }
 }
}
