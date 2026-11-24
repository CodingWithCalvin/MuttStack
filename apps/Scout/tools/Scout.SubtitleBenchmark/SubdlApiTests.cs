using System.Net;
using System.Text.Json.Nodes;

static class SubdlApiTests {
 public static async Task Run() {
  const string secret="fake-subdl-unit-key";int passed=0;
  void Check(bool value){if(!value)throw new Exception("SubDL transport assertion failed.");}
  async Task Reject(Func<Task> action) {try{await action();}catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException){Check(!ex.ToString().Contains(secret));return;}throw new Exception("Unsafe request was accepted.");}
  using(var handler=new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent("{\"results\":[],\"echo\":\""+secret+"\"}")}))using(var api=new SubdlApi(secret,handler)) {
   var reply=await api.Titles("A Show & Another",default);
   Check(handler.Urls.Single().Host=="api.subdl.com"&&!handler.Urls.Single().AbsoluteUri.Contains(secret)&&handler.Auth.Single()=="Bearer "+secret&&!reply.ToJsonString().Contains(secret));passed++;
  }
  using(var handler=new Handler(r=>new(HttpStatusCode.Found){Headers={Location=new Uri("https://untrusted.example/subtitle/a.zip")}}))using(var api=new SubdlApi(secret,handler)) {
   await Reject(()=>api.Download(new Uri("https://dl.subdl.com/subtitle/a.zip"),default));Check(handler.Urls.Count==1);passed++;
  }
  using(var handler=new Handler(r=>new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2])}))using(var api=new SubdlApi(secret,handler)) {
   foreach(var url in new[]{"http://dl.subdl.com/subtitle/a.zip","https://api.subdl.com.untrusted.example/subtitle/a.zip","https://user@dl.subdl.com/subtitle/a.zip","https://dl.subdl.com:8443/subtitle/a.zip","https://api.subdl.com/api/v2/ai/translations"})await Reject(()=>api.Download(new Uri(url),default));
   Check(handler.Urls.Count==0);passed++;
  }
  using(var handler=new Handler(r=>r.RequestUri!.Host=="api.subdl.com"?new(HttpStatusCode.Found){Headers={Location=new Uri("https://dl.subdl.com/subtitle/a.zip")}}:new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])}))using(var api=new SubdlApi(secret,handler)) {
   Check((await api.Download(new Uri("https://api.subdl.com/api/v2/subtitles/abc/download?format=zip"),default)).Length==3&&handler.Urls.Count==2&&handler.Auth.SequenceEqual(new[]{"Bearer "+secret,"X-API-Key "+secret}));passed++;
  }
  using(var handler=new Handler(r=>new(HttpStatusCode.Unauthorized){Content=new StringContent(secret)}))using(var api=new SubdlApi(secret,handler)) {await Reject(()=>api.Titles("Show",default));Check(handler.Urls.Count==1);passed++;}
  using(var handler=new Handler(r=>new(HttpStatusCode.OK){Content=new DeclaredLargeContent()}))using(var api=new SubdlApi(secret,handler)) {await Reject(()=>api.Titles("Show",default));passed++;}
  using(var stream=new MemoryStream(new byte[11])){await Reject(()=>SubdlApi.Bounded(stream,10,default));passed++;}
  Check(SubdlApi.DownloadUri(new JsonObject{["n_id"]="abc-123"}).AbsoluteUri=="https://api.subdl.com/api/v2/subtitles/abc-123/download?format=zip");passed++;
  Check(SubdlApi.DownloadUri(new JsonObject{["url"]="/subtitle/abc.zip?api_key="+secret}).Query=="");passed++;
  Console.WriteLine($"{passed} SubDL credential, redirect, error and size-limit checks passed.");
 }
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler {
  public List<Uri> Urls=[];public List<string> Auth=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {Urls.Add(request.RequestUri!);Auth.Add(request.Headers.Authorization?.ToString()??(request.Headers.TryGetValues("X-API-Key",out var values)?"X-API-Key "+values.Single():""));return Task.FromResult(respond(request));}
 }
 sealed class DeclaredLargeContent:HttpContent {
  public DeclaredLargeContent(){Headers.ContentLength=5*1024*1024;}
  protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new Exception("Oversized response body must not be consumed.");
  protected override bool TryComputeLength(out long length){length=5*1024*1024;return true;}
 }
}
