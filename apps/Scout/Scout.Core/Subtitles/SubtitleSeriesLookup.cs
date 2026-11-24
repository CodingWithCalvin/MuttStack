using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core.Subtitles;
internal static class SubtitleSeriesLookup {
 public static async Task<string> ImdbAsync(SeriesIdentity series,string? metadataKey,Func<HttpMessageHandler>? metadataHandler,CancellationToken ct) {
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
