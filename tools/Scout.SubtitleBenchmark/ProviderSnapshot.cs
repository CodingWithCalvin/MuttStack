using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Scout.Core;

sealed record ProviderSnapshot(SeriesIdentity Series,List<Episode> Episodes,string ImdbId,string EvidenceUrl) {
 public static async Task<ProviderSnapshot> Fetch(string provider,JsonNode identity,string database,string settings,CancellationToken ct) {
  var imdb=identity.Text("imdb_id");
  if(!Regex.IsMatch(imdb,@"^tt\d+$"))throw new InvalidDataException("SubDL series lacks an IMDb identity for cross-provider verification.");
  using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
  var url=new Uri("https://api.tvmaze.com/lookup/shows?imdb="+imdb);
  using var lookup=await http.GetAsync(url,ct);JsonNode show;
  if(lookup.StatusCode==HttpStatusCode.MovedPermanently) {
   var target=new Uri(url,lookup.Headers.Location??throw new InvalidDataException("Missing series redirect."));
   if(target.Scheme!="https"||target.Host!="api.tvmaze.com"||!target.IsDefaultPort||target.UserInfo!=""||target.Query!=""||target.Fragment!=""||!Regex.IsMatch(target.AbsolutePath,@"^/shows/[1-9]\d*$"))throw new InvalidDataException("Untrusted series redirect.");
   using var response=await http.GetAsync(target,ct);response.EnsureSuccessStatusCode();show=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
  }else {lookup.EnsureSuccessStatusCode();show=JsonNode.Parse(await lookup.Content.ReadAsStringAsync(ct))!;}
  if(show["externals"].Text("imdb")!=imdb||TitleMapping.Key(show.Text("name"))!=TitleMapping.Key(identity.Text("series")))throw new InvalidDataException("External series identity did not agree.");
  string id=provider switch {"tvdb"=>show["externals"]?["thetvdb"]?.ToString()??"","tmdb"=>identity.Text("tmdb_id"),"tvmaze"=>show["id"]!.ToString(),_=>throw new InvalidDataException("Unsupported configured provider.")};
  var key=provider=="tvmaze"?null:SubtitleCredentials.ReadMetadata(database,settings,provider);
  var catalog=new MetadataCatalog(http,provider,key,provider=="tvdb"?SubtitleCredentials.ReadMetadata(database,settings,"tvdb-pin"):null);
  var series=await catalog.GetAsync(id,ct)??throw new InvalidDataException("The configured provider did not return this series.");
  if(TitleMapping.Key(series.Name)!=TitleMapping.Key(identity.Text("series")))throw new InvalidDataException("Provider series title did not agree with external identity.");
  return new(series,await catalog.EpisodesAsync(id,ct),imdb,url.AbsoluteUri);
 }
}
