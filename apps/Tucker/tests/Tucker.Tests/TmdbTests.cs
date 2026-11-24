using Tucker;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
static class TmdbTests {
 public static async Task<int> Run() {
  int count=0;
  void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
  var data=JsonNode.Parse("""{"results":[{"id":5371,"name":"iCarly","first_air_date":"2007-09-08","overview":"Original series"},{"id":108360,"name":"iCarly","first_air_date":"2021-06-17"},{"id":0,"name":"Bad"},{"id":5371,"name":"Duplicate"}]}""");
  var shows=MetadataSearch.ReadResults(data,"tmdb");
  Check(shows.Count==2&&shows[0].Provider=="tmdb"&&shows[0].Year=="2007"&&shows[1].Year=="2021","TMDB results distinguish original and revival and reject invalid or duplicate IDs");
  Check(shows[0].Display.Contains("TMDB 5371"),"Selected series displays its actual provider");
  var context=new FolderContext(shows[0],"6","S6 D1",new DateTime(2026,1,2));
  Check(FolderNames.Expand("{series} {{{provider}-{id}}}\\Season {season:00}",context)=="iCarly {tmdb-5371}\\Season 06","TMDB provider tokens render Plex-compatible braces and series identity");
  Check(FolderNames.Expand("{series_id}-{tmdb_id}",context)=="5371-5371","TMDB-specific and provider-neutral ID aliases agree");
  bool wrong=false;try{FolderNames.Expand("{tvdb_id}",context);}catch(IOException){wrong=true;}
  Check(wrong,"TMDB IDs cannot silently fill TVDB-only tokens");
  Check(FolderNames.Expand("{tvdb_id}",context with{Series=shows[0] with{Provider="tvdb",Id="82493"}})=="82493","Legacy TVDB formats remain supported");
  using var handler=new Handler(request=>{
   Check(request.RequestUri!.Host=="api.themoviedb.org"&&request.RequestUri.AbsolutePath=="/3/search/tv"&&request.RequestUri.Query.Contains("language=en-US"),"TMDB lookup targets the official API and English catalog");
   Check(request.Headers.Authorization?.Scheme=="Bearer"&&request.Headers.Authorization.Parameter=="fixture-only-token","TMDB authentication stays in the authorization header");
   Check(!request.RequestUri.ToString().Contains("fixture-only-token"),"TMDB token is absent from the URL");
   return new(HttpStatusCode.OK){Content=new StringContent(data!.ToJsonString())};
  });
  using var http=new HttpClient(handler);
  var results=await MetadataSearch.SearchAsync(http,"iCarly & friends","tmdb","fixture-only-token");
  Check(results.Count==2,"TMDB lookup parses independent provider results");
  using var refused=new HttpClient(new Handler(_=>new(HttpStatusCode.Unauthorized){Content=new StringContent("server payload must not leak")}));
  bool failed=false;try{await MetadataSearch.SearchAsync(refused,"iCarly","tmdb","fixture-only-token");}catch(IOException ex){failed=ex.Message.Contains("401")&&!ex.Message.Contains("fixture-only-token")&&!ex.Message.Contains("server payload");}
  Check(failed,"Credential failures report status without token or response payload");
  return count;
 }
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));
 }
}
