using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class DescriptionTests
{
    static void Check(bool condition,string message="Description recovery assertion failed") {if(!condition)throw new Exception(message);}
    static HttpResponseMessage Json(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text)};
    static HttpResponseMessage Missing()=>new(HttpStatusCode.NotFound);
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();Calls++;return Task.FromResult(reply(request));}
    }
    static readonly Episode[] Parts=[new(11,1,1,"The Crossing (1)","The crew begins a difficult journey."),new(12,1,2,"The Crossing (2)")];
    const string Supplement="""[{"id":101,"season":1,"number":1,"name":"The Crossing","summary":"The crew begins a difficult journey."},{"id":102,"season":1,"number":2,"name":"The Crossing","summary":"<p>The crew frees a captured passenger and reaches the destination.</p>"}]""";
    public static void Run(Action<string,Action> test,string root) {
        const string wrong="A light thief leaves the city dark. The rescue team installs special lights to track the thief and restore the missing lights.";
        const string correct="The racers discover a medieval kingdom and compete in a royal tournament.";
        Episode[] mixed=[new(21,2,7,"Royal Riders",wrong),new(34,3,4,"Light Riders",wrong)];
        const string fixedGuide="""[{"id":201,"season":2,"number":6,"name":"Royal Riders","summary":"The racers discover a medieval kingdom and compete in a royal tournament."},{"id":202,"season":3,"number":4,"name":"Light Riders","summary":"A light thief leaves the city dark. The rescue team installs special lights to track the thief and restore the missing lights."}]""";
        test("Misplaced nonempty summaries recover by verified series and unique title without renumbering",()=>{
            using var handler=new Handler(r=>{
                if(r.RequestUri!.Host=="api.themoviedb.org") {Check(r.Headers.Authorization?.Parameter=="fixture");return Json("""{"id":70,"imdb_id":"tt1234567"}""");}
                Check(r.RequestUri.Host=="api.tvmaze.com"&&r.Headers.Authorization==null);
                return r.RequestUri.AbsolutePath=="/lookup/shows"?Json("""{"id":9,"externals":{"imdb":"tt1234567"}}"""):Json(fixedGuide);
            });using var http=new HttpClient(handler);var catalog=new MetadataCatalog(http,"tmdb","fixture");
            var result=catalog.CompleteDescriptionsAsync("70",mixed,default).GetAwaiter().GetResult();
            Check(result[0].Overview==correct&&result[0].Id==21&&result[0].Code=="S02E07"&&result[0].Title=="Royal Riders");
            Check(result[0].OriginalDescription?.Overview==wrong&&result[0].DescriptionSource is {Provider:"tvmaze",EpisodeId:"201"});
            Check(result[1].Overview==wrong&&result[1].OriginalDescription==null);
            int before=handler.Calls;catalog.CompleteDescriptionsAsync("70",result,default).GetAwaiter().GetResult();Check(handler.Calls==before);
        });
        test("Supplemental description repair rejects ambiguous titles and weak disagreements",()=>{
            Episode[] supplemental=[new(201,2,6,"Royal Riders",correct),new(202,3,4,"Light Riders",wrong)];
            Check(DescriptionRepair.Replacement(mixed[0],mixed,supplemental)?.Id==201);
            Check(DescriptionRepair.Replacement(mixed[0],mixed,[..supplemental,supplemental[0] with{Id=203}])==null);
            Check(DescriptionRepair.Replacement(mixed[0],[..mixed,mixed[0] with{Id=23}],supplemental)==null);
            Check(DescriptionRepair.Replacement(mixed[0],mixed,[supplemental[0] with{Overview=wrong},supplemental[1]])==null);
            Check(DescriptionRepair.Replacement(mixed[0],mixed,[supplemental[0],supplemental[1] with{Overview="An unrelated adventure."}])==null);
            Check(!DescriptionRepair.Suspect(mixed[0] with{Title="Journey (1)"},[mixed[0] with{Title="Journey (1)"},mixed[1] with{Season=2,Number=8,Title="Journey (2)"}]));
        });
        foreach(bool unavailable in new[]{false,true})test("Failed or unverified metadata leaves nonempty guides unchanged: "+unavailable,()=>{
            using var http=new HttpClient(new Handler(r=>r.RequestUri!.Host=="api.themoviedb.org"?Json("""{"id":70,"imdb_id":"tt1234567"}"""):
                unavailable?new(HttpStatusCode.ServiceUnavailable):Json("""{"id":9,"externals":{"imdb":"tt9999999"}}""")));
            var result=new MetadataCatalog(http,"tmdb","fixture").CompleteDescriptionsAsync("70",mixed,default).GetAwaiter().GetResult();Check(result.SequenceEqual(mixed));
        });
        test("Missing descriptions use provider details and persist their provenance",()=>{
            using var handler=new Handler(r=>r.RequestUri!.AbsolutePath.EndsWith("login")?Json("""{"data":{"token":"secret"}}"""):
                Json("""{"data":{"id":12,"seriesId":70,"overview":"","translations":{"overviewTranslations":[{"language":"eng","overview":"The crew completes its journey."}]}}}"""));
            using var http=new HttpClient(handler);var client=new MetadataCatalog(http,"tvdb","key");
            var path=Path.Combine(root,"descriptions","catalog.json");var identity=new SeriesIdentity("tvdb","70","Show");
            Data.Save(path,new{series_identity=identity,episodes=Parts,untouched="preserved"});
            var result=EpisodeGuidePreparation.EnsureAsync(path,identity,client,default).GetAwaiter().GetResult();
            Check(result[0]==Parts[0]&&result[1].Overview=="The crew completes its journey.");
            Check(result[1].Id==12&&result[1].Title==Parts[1].Title&&result[1].DescriptionSource?.Provider=="tvdb");
            var calls=handler.Calls;EpisodeGuidePreparation.EnsureAsync(path,identity,client,default).GetAwaiter().GetResult();
            Check(handler.Calls==calls&&Data.Read(path).Text("untouched")=="preserved");
            Check(SavedGuide.Identity(path,"").Key==identity.Key&&Data.Catalog(path)[1].DescriptionSource?.EpisodeId=="12");
        });
        test("TVDB English translation is retrieved only for the verified episode ID",()=>{
            using var http=new HttpClient(new Handler(r=>r.RequestUri!.AbsolutePath.EndsWith("login")?Json("""{"data":{"token":"secret"}}"""):
                r.RequestUri.AbsolutePath.EndsWith("extended")?Json("""{"data":{"id":12,"seriesId":70,"overview":null}}"""):
                Json("""{"data":{"language":"eng","overview":"The expedition reaches its goal."}}""")));
            var result=new MetadataCatalog(http,"tvdb","key").CompleteDescriptionsAsync("70",Parts,default).GetAwaiter().GetResult();
            Check(result[1].Overview=="The expedition reaches its goal.");
        });
        test("TVmaze fills aligned multipart descriptions without replacing TVDB identity or leaking credentials",()=>{
            using var http=new HttpClient(new Handler(r=>{
                if(r.RequestUri!.Host=="api4.thetvdb.com") {
                    if(r.RequestUri.AbsolutePath.EndsWith("login"))return Json("""{"data":{"token":"secret"}}""");
                    Check(r.Headers.Authorization?.Parameter=="secret");return Missing();
                }
                Check(r.RequestUri.Host=="api.tvmaze.com"&&r.Headers.Authorization==null);
                if(r.RequestUri.AbsolutePath=="/lookup/shows") {var redirect=new HttpResponseMessage(HttpStatusCode.MovedPermanently);redirect.Headers.Location=new Uri("https://api.tvmaze.com/shows/9");return redirect;}
                if(r.RequestUri.AbsolutePath=="/shows/9")return Json("""{"id":9,"externals":{"thetvdb":70}}""");
                return Json(Supplement);
            }));
            var result=new MetadataCatalog(http,"tvdb","key").CompleteDescriptionsAsync("70",Parts,default).GetAwaiter().GetResult();
            Check(result[0]==Parts[0]&&result[1].Overview.StartsWith("The crew frees"));
            Check(result[1].Id==12&&result[1].Code=="S01E02"&&result[1].Title=="The Crossing (2)");
            Check(result[1].DescriptionSource is {Provider:"tvmaze",SeriesId:"9",EpisodeId:"102"});
        });
        test("TMDB can supplement descriptions through its verified IMDb series mapping",()=>{
            using var http=new HttpClient(new Handler(r=>{
                if(r.RequestUri!.Host=="api.themoviedb.org") {
                    Check(r.Headers.Authorization?.Parameter=="tmdb-token");
                    return r.RequestUri.AbsolutePath.EndsWith("external_ids")?Json("""{"id":70,"imdb_id":"tt1234567"}"""):
                        Json("""{"id":12,"season_number":1,"episode_number":2,"overview":""}""");
                }
                Check(r.Headers.Authorization==null);
                return r.RequestUri.AbsolutePath=="/lookup/shows"?Json("""{"id":9,"externals":{"imdb":"tt1234567"}}"""):Json(Supplement);
            }));
            Check(new MetadataCatalog(http,"tmdb","tmdb-token").CompleteDescriptionsAsync("70",Parts,default).GetAwaiter().GetResult()[1].DescriptionSource?.Provider=="tvmaze");
        });
        test("Ambiguous merged shifted or incomplete multipart mappings are never borrowed",()=>{
            var other=new[]{new Episode(101,1,1,"The Crossing","A"),new Episode(102,1,2,"The Crossing","B")};
            Check(DescriptionMapping.Find(Parts[1],Parts,other)?.Id==102);
            Check(DescriptionMapping.Find(Parts[1],Parts,other.Take(1).ToArray())==null);
            Check(DescriptionMapping.Find(Parts[1],Parts,other.Select(e=>e with {Number=e.Number+1}).ToArray())==null);
            Check(DescriptionMapping.Find(Parts[1],Parts,other.Concat([other[1] with {Id=103}]).ToArray())==null);
            Check(DescriptionMapping.Find(Parts[1],[Parts[1]],other)==null);
            Check(DescriptionMapping.Find(Parts[1],Parts,[other[0],other[1] with {Title="Different Story"}])==null);
        });
        test("Wrong provider episode identity and unverified supplemental series remain missing",()=>{
            using var http=new HttpClient(new Handler(r=>r.RequestUri!.AbsolutePath.EndsWith("login")?Json("""{"data":{"token":"secret"}}"""):
                r.RequestUri.Host=="api4.thetvdb.com"?Json("""{"data":{"id":999,"seriesId":70,"overview":"Wrong episode."}}"""):
                Json("""{"id":9,"externals":{"thetvdb":999}}""")));
            var result=new MetadataCatalog(http,"tvdb","key").CompleteDescriptionsAsync("70",Parts,default).GetAwaiter().GetResult();
            Check(result[1].Overview==""&&result[1].DescriptionSource==null&&result[1].DescriptionIssue!=null);
        });
        test("Supplemental metadata redirects cannot leave the fixed provider host",()=>{
            using var handler=new Handler(r=>{
                Check(r.RequestUri!.Host=="api.tvmaze.com");
                var response=new HttpResponseMessage(HttpStatusCode.MovedPermanently);response.Headers.Location=new Uri("https://invalid.example/steal");return response;
            });
            using var http=new HttpClient(handler);
            var result=new MetadataCatalog(http,"tvdb").CompleteDescriptionsAsync("70",Parts,default).GetAwaiter().GetResult();
            Check(handler.Calls==1&&result[1].Overview==""&&result[1].DescriptionIssue!=null);
        });
        test("Cancellation never persists a partially recovered episode guide",()=>{
            var path=Path.Combine(root,"description-cancel","catalog.json");var identity=new SeriesIdentity("tvdb","70","Show");Data.Save(path,new{series_identity=identity,episodes=Parts});
            var before=File.ReadAllBytes(path);using var stop=new CancellationTokenSource();stop.Cancel();using var http=new HttpClient(new Handler(_=>throw new Exception("Unexpected HTTP")));
            try{EpisodeGuidePreparation.EnsureAsync(path,identity,new MetadataCatalog(http,"tvdb","key"),stop.Token).GetAwaiter().GetResult();throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
            Check(before.SequenceEqual(File.ReadAllBytes(path)));
        });
        test("Unavailable descriptions become a specific terminal hold instead of a model repair retry",()=>{
            try{GroundedVerification.Statements(Parts[1]);throw new Exception("Missing description accepted");}
            catch(InvalidDataException ex){Check(AssignmentValidation.IsTerminal(ex)&&ex.Message.Contains("S01E02")&&ex.Message.Contains("catalog ID 12"));}
        });
    }
}
