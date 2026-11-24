using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core.Subtitles;

sealed class SubdlAccessException(string message,HttpStatusCode? status=null):IOException(message) {public HttpStatusCode? Status {get;}=status;}
sealed class SubdlApi:IDisposable {
 readonly HttpClient http;readonly string key;
 public SubdlApi(string key,HttpMessageHandler? handler=null) {
  if(string.IsNullOrWhiteSpace(key)||key.Length>4096||key.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||c>126))throw new ArgumentException("The saved SubDL key has an invalid format.");
  this.key=key;http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
 }
 public Task<JsonObject> Titles(string series,CancellationToken ct)=>Json("movies/search?q="+Uri.EscapeDataString(series)+"&type=tv&limit=30",ct);
 public Task<JsonObject> Subtitles(string id,int season,CancellationToken ct,int? episode=null)=>Json("subtitles/search?sd_id="+Uri.EscapeDataString(id)+"&type=tv&languages=en&season="+season+"&unpack=1&subs_per_page=30&releases=1"+(episode.HasValue?"&episode="+episode.Value:""),ct);
 async Task<JsonObject> Json(string relative,CancellationToken ct) {
  var bytes=await Get(new Uri("https://api.subdl.com/api/v2/"+relative),4*1024*1024,false,ct);
  try {
   var result=JsonNode.Parse(Encoding.UTF8.GetString(bytes).Replace(key,"[redacted]",StringComparison.Ordinal))?.AsObject()??throw new InvalidDataException("SubDL returned an empty response.");
   if(result["status"] is JsonValue value&&value.TryGetValue<bool>(out var ok)&&!ok)throw new SubdlAccessException("SubDL rejected the API request. Check your key and account limits.");
   return result;
  }catch(System.Text.Json.JsonException){throw new InvalidDataException("SubDL returned invalid JSON.");}
   catch(InvalidOperationException){throw new InvalidDataException("SubDL returned an unexpected response structure.");}
 }
 public static Uri DownloadUri(JsonObject row) {
  string Text(string name)=>row[name] is JsonValue value?value.ToString():"";
  var id=Text("n_id");if(id=="")id=Text("nId");
  if(Regex.IsMatch(id,"^[A-Za-z0-9_-]{1,128}$"))return new("https://api.subdl.com/api/v2/subtitles/"+id+"/download?format=zip");
  var link=Text("url");if(link.Length>2048)throw new InvalidDataException("SubDL supplied an invalid download address.");
  var uri=new Uri(new Uri("https://dl.subdl.com"),link);Validate(uri,true);
  // Legacy responses append an API-key query even when header auth was used.
  // Never forward that query; downloads use the request-scoped header instead.
  return new UriBuilder(uri){Query=""}.Uri;
 }
 public Task<byte[]> Download(Uri uri,CancellationToken ct)=>Get(uri,12*1024*1024,true,ct);
 static void Validate(Uri uri,bool download) {
  bool host=download?uri.Host is "api.subdl.com" or "dl.subdl.com":uri.Host=="api.subdl.com";
  bool path=!download?uri.AbsolutePath is "/api/v2/movies/search" or "/api/v2/subtitles/search":
   uri.Host=="dl.subdl.com"?uri.AbsolutePath.StartsWith("/subtitle/",StringComparison.Ordinal):Regex.IsMatch(uri.AbsolutePath,@"^/api/v2/subtitles/[A-Za-z0-9_-]+/download$");
  if(!host||!path||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.Fragment!="")throw new InvalidDataException("SubDL supplied an untrusted endpoint; no credential was sent.");
 }
 async Task<byte[]> Get(Uri uri,int limit,bool download,CancellationToken ct) {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(60));var requestToken=timeout.Token;
  try {
   for(int redirects=0;redirects<3;redirects++) {
    Validate(uri,download);
    if(download)uri=new UriBuilder(uri){Query=uri.Host=="api.subdl.com"?"format=zip":""}.Uri;
    using var request=new HttpRequestMessage(HttpMethod.Get,uri);
    if(uri.Host=="dl.subdl.com")request.Headers.Add("X-API-Key",key);
    else request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
    request.Headers.UserAgent.ParseAdd("Scout-SubtitleBenchmark/1.0");
    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,requestToken);
    if((int)response.StatusCode is >=300 and <400) {
     if(!download||response.Headers.Location is not Uri location)throw new SubdlAccessException("SubDL returned an unexpected redirect.");
     uri=new Uri(uri,location);Validate(uri,true);continue;
    }
    if(!response.IsSuccessStatusCode)throw new SubdlAccessException($"SubDL returned HTTP {(int)response.StatusCode} ({response.StatusCode})."+((int)response.StatusCode is 401 or 403?" Check that the API key is valid and the account permits access.":response.StatusCode==HttpStatusCode.TooManyRequests?" Account or download limit reached; no automatic retry was attempted.":""),response.StatusCode);
    if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("SubDL response exceeds the test's size limit.");
    await using var input=await response.Content.ReadAsStreamAsync(requestToken);return await Bounded(input,limit,requestToken);
   }
   throw new SubdlAccessException("SubDL exceeded the permitted redirect count.");
  }catch(HttpRequestException){throw new SubdlAccessException("SubDL network request failed. No response or credential was logged.");}
   catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new SubdlAccessException("SubDL request timed out after 60 seconds.");}
 }
 public static async Task<byte[]> Bounded(Stream input,int limit,CancellationToken ct) {
  using var output=new MemoryStream();var buffer=new byte[65536];int count;
  while((count=await input.ReadAsync(buffer,ct))>0){if(output.Length+count>limit)throw new InvalidDataException("Subtitle data exceeds the test's size limit.");output.Write(buffer,0,count);}
  return output.ToArray();
 }
 public void Dispose()=>http.Dispose();
}
