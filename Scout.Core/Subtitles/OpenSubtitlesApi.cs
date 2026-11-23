using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Scout.Core.Subtitles;

internal sealed class OpenSubtitlesAccessException(string message,HttpStatusCode? status=null,DateTimeOffset? retryAfter=null):IOException(message) {
 public HttpStatusCode? Status {get;}=status;
 public DateTimeOffset? RetryAfter {get;}=retryAfter;
}
internal sealed class OpenSubtitlesApi:IDisposable {
 readonly OpenSubtitlesCredentials credentials;readonly HttpClient http;string host="api.opensubtitles.com",token="";bool loggedIn;
 static readonly SemaphoreSlim RateGate=new(1,1);static DateTimeOffset nextRequest=DateTimeOffset.MinValue,nextLogin=DateTimeOffset.MinValue;
 public int? Remaining {get;private set;}
 public DateTimeOffset? ResetAt {get;private set;}
 public OpenSubtitlesApi(OpenSubtitlesCredentials credentials,HttpMessageHandler? handler=null) {
  credentials.Validate();this.credentials=credentials;
  http=new(handler??new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All}){Timeout=TimeSpan.FromSeconds(45)};
 }
 async Task Login(CancellationToken ct) {
  if(loggedIn)return;loggedIn=true;
  if(credentials.Username.Length==0)return;
  var result=await Json(HttpMethod.Post,"login",new JsonObject{["username"]=credentials.Username,["password"]=credentials.Password},ct);
  var selected=result.Text("base_url","api.opensubtitles.com");
  if(selected is not ("api.opensubtitles.com" or "vip-api.opensubtitles.com"))throw new InvalidDataException("OpenSubtitles returned an untrusted API host.");
  var value=result.Text("token");if(string.IsNullOrWhiteSpace(value)||value.Length>8192||value.Any(char.IsWhiteSpace)||value.Any(char.IsControl))throw new InvalidDataException("OpenSubtitles login returned no usable token.");
  host=selected;token=value;
 }
 public async Task<JsonObject> Search(string parentParameter,string parentId,int season,int page,CancellationToken ct) {
  await Login(ct);
  if(parentParameter is not ("parent_tmdb_id" or "parent_imdb_id")||!System.Text.RegularExpressions.Regex.IsMatch(parentId,"^[1-9][0-9]*$")||season<0||page<1)throw new ArgumentException("Invalid subtitle search identity.");
  var query=new SortedDictionary<string,string>(StringComparer.Ordinal){["ai_translated"]="exclude",["foreign_parts_only"]="exclude",["languages"]="en",[parentParameter]=parentId,["season_number"]=season.ToString(System.Globalization.CultureInfo.InvariantCulture),["type"]="episode"};
  if(page>1)query["page"]=page.ToString(System.Globalization.CultureInfo.InvariantCulture);
  return await Json(HttpMethod.Get,"subtitles?"+string.Join("&",query.Select(p=>p.Key+"="+p.Value)),null,ct);
 }
 public async Task<byte[]> Download(long fileId,CancellationToken ct) {
  await Login(ct);if(fileId<=0)throw new ArgumentException("Invalid OpenSubtitles file ID.");
  if(Remaining==0)throw new OpenSubtitlesAccessException("OpenSubtitles download quota reached.",HttpStatusCode.NotAcceptable,ResetAt);
  // This POST spends one download allowance. Never retry it automatically.
  var ticket=await Json(HttpMethod.Post,"download",new JsonObject{["file_id"]=fileId,["sub_format"]="srt"},ct);
  if(ticket["remaining"] is JsonValue remaining&&remaining.TryGetValue<double>(out var n)&&double.IsFinite(n)&&n>=0)Remaining=(int)Math.Min(n,int.MaxValue);
  ResetAt=Reset(ticket.Text("reset_time_utc"));
  if(!Uri.TryCreate(ticket.Text("link"),UriKind.Absolute,out var uri))throw new InvalidDataException("OpenSubtitles returned an invalid subtitle download.");
  return await Send(HttpMethod.Get,uri,null,false,12*1024*1024,ct);
 }
 static DateTimeOffset? Reset(string text)=>DateTimeOffset.TryParse(text,out var date)&&date>DateTimeOffset.UtcNow&&date<DateTimeOffset.UtcNow.AddDays(2)?date:null;
 async Task<JsonObject> Json(HttpMethod method,string path,JsonObject? body,CancellationToken ct) {
  var bytes=await Send(method,new Uri($"https://{host}/api/v1/"+path),body,true,4*1024*1024,ct);
  try{return JsonNode.Parse(Encoding.UTF8.GetString(bytes))?.AsObject()??throw new InvalidDataException("OpenSubtitles returned an empty response.");}
  catch(Exception ex) when(ex is System.Text.Json.JsonException or InvalidOperationException){throw new InvalidDataException("OpenSubtitles returned an invalid response.");}
 }
 static void Validate(Uri uri,bool api) {
  bool trusted=api?uri.Host is "api.opensubtitles.com" or "vip-api.opensubtitles.com":
   uri.Host is "opensubtitles.com" or "www.opensubtitles.com" or "dl.opensubtitles.com" or "dl.opensubtitles.org" or "dl.osdb.link";
  if(!trusted||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo!=""||uri.Fragment!=""||
   api&&uri.AbsolutePath is not ("/api/v1/login" or "/api/v1/subtitles" or "/api/v1/download"))
   throw new InvalidDataException("OpenSubtitles returned an untrusted address.");
 }
 async Task<byte[]> Send(HttpMethod method,Uri uri,JsonObject? body,bool api,int maximum,CancellationToken ct) {
  try {
   for(int redirect=0;redirect<3;redirect++) {
    Validate(uri,api);
    if(api) {
     await RateGate.WaitAsync(ct);
     try {var next=uri.AbsolutePath.EndsWith("/login",StringComparison.Ordinal)&&nextLogin>nextRequest?nextLogin:nextRequest;var delay=next-DateTimeOffset.UtcNow;
      if(delay>TimeSpan.Zero)await Task.Delay(delay,ct);nextRequest=DateTimeOffset.UtcNow.AddMilliseconds(250);
      if(uri.AbsolutePath.EndsWith("/login",StringComparison.Ordinal))nextLogin=DateTimeOffset.UtcNow.AddSeconds(1);
     }finally{RateGate.Release();}
    }
    using var request=new HttpRequestMessage(method,uri);request.Headers.UserAgent.ParseAdd("Scout v0.9.12");
    // Temporary download links are bearer capabilities, never durable provenance.
    // No API key, user token or password is forwarded to the file host.
    if(api){request.Headers.Add("Api-Key",credentials.ApiKey);if(token.Length>0)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);}
    if(body!=null)request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
    if((int)response.StatusCode is >=300 and <400) {
     if(method!=HttpMethod.Get||response.Headers.Location==null)throw new OpenSubtitlesAccessException("OpenSubtitles returned an unexpected redirect.");
     uri=new Uri(uri,response.Headers.Location);continue;
    }
    if(!response.IsSuccessStatusCode) {
     var retry=response.Headers.RetryAfter?.Date??(response.Headers.RetryAfter?.Delta is {} delta?DateTimeOffset.UtcNow+delta:null);
     throw new OpenSubtitlesAccessException(response.StatusCode switch {
      HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"OpenSubtitles credentials were rejected; check the API key and account login.",
      HttpStatusCode.NotAcceptable=>"OpenSubtitles download quota reached.",HttpStatusCode.TooManyRequests=>"OpenSubtitles request limit reached.",
      _=>$"OpenSubtitles returned HTTP {(int)response.StatusCode}."},response.StatusCode,retry);
    }
    if(response.Content.Headers.ContentLength>maximum)throw new InvalidDataException("OpenSubtitles response exceeds the size limit.");
    await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[16384];int count;
    while((count=await input.ReadAsync(buffer,ct))>0){if(output.Length+count>maximum)throw new InvalidDataException("OpenSubtitles response exceeds the size limit.");output.Write(buffer,0,count);}
    return output.ToArray();
   }
   throw new OpenSubtitlesAccessException("OpenSubtitles redirected too many times.");
  }catch(HttpRequestException){throw new OpenSubtitlesAccessException("OpenSubtitles could not be reached; saved references remain available.");}
 }
 public void Dispose()=>http.Dispose();
}
