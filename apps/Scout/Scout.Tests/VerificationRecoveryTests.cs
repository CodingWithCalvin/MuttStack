using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class VerificationRecoveryTests
{
    const string Endpoint="http://127.0.0.1:1234/v1/chat/completions";
    static readonly Episode[] Parts=[new(10,1,1,"The Rescue","The crew rescues the stranded captain."),new(11,1,2,"The Rescue - Part 2","The crew rescues the stranded captain.")];
    static readonly Cue[] Cues=[new(0,"The captain is stranded beyond the canyon."),new(40,"The crew launches their rescue mission."),new(80,"They reach the captain and clear the debris."),new(120,"The captain is safe aboard the rescue ship.")];
    static void Check(bool ok,string message="Verification recovery regression"){if(!ok)throw new Exception(message);}
    static void Reject(Action act){try{act();throw new Exception("Invalid evidence accepted");}catch(InvalidDataException){}}
    static JsonObject Audit()=>new(){["candidates"]=new JsonObject{["c0"]=new JsonObject{["role"]="main_story",["statements"]=new JsonObject{
        ["g0"]=new JsonObject{["verdict"]="supported",["evidence"]=new JsonArray("s0","s3")}}}},
        ["recording"]=new JsonObject{["kind"]="single_episode",["candidates"]=new JsonArray("c0")}};
    static JsonObject Shared(bool complete=true) {
        var r=Audit();r["recording"]!["kind"]=complete?"continuous_parts":"unresolved";r["recording"]!["candidates"]=complete?new JsonArray("c0","c1"):new JsonArray();
        r["installments"]=new JsonObject{["c0"]=new JsonArray("s0","s1"),["c1"]=new JsonArray("s2","s3")};return r;
    }
    sealed class Handler(Func<JsonObject,int,JsonObject> reply):HttpMessageHandler {
        public List<JsonObject> Sources=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var source=JsonNode.Parse(body["messages"]![1].Text("content"))!.AsObject();Sources.Add(source);
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=reply(source,Sources.Count).ToJsonString()}})}.ToJsonString())};
        }
    }
    static JsonObject Expand(JsonObject raw)=>SharedGuideGrounding.Expand(raw,DialogueEvidence.Spans(DialogueMatcher.Blocks(Cues)),DialogueMatcher.Blocks(Cues),Parts);
    public static void Run(Action<string,Action> test) {
        test("Evidence schema variants retain strict object contracts and require grounded verdict citations",()=>{
            using var resource=typeof(DialogueMatcher).Assembly.GetManifestResourceStream("Scout.Core.Assets.span-grounded-schema.json")!;
            using var reader=new StreamReader(resource);var template=JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            var spans=DialogueEvidence.Spans(DialogueMatcher.Blocks(Cues));
            foreach(var schema in new[]{CompactGrounding.Schema(template,spans,[Parts[0]]),SharedGuideGrounding.Schema(template,spans,Parts)}) {
                void Visit(JsonNode? node) {
                    if(node is JsonObject obj) {
                        if(obj["type"]?.GetValue<string>()=="object")Check(!obj.Bool("additionalProperties")&&obj["additionalProperties"]!=null&&obj["properties"]!.AsObject().Select(p=>p.Key).Order().SequenceEqual(obj.Array("required").Select(n=>n!.GetValue<string>()).Order()));
                        foreach(var pair in obj)Visit(pair.Value);
                    }else if(node is JsonArray list)foreach(var child in list)Visit(child);
                }
                Visit(schema);
                var fact=schema["properties"]!["candidates"]!["properties"]!["c0"]!["properties"]!["statements"]!["properties"]!["g0"]!;
                var variants=fact.Array("anyOf");Check(variants.Count==2&&variants[0]!["properties"]!["evidence"].Int("minItems")==1&&variants[1]!["properties"]!["verdict"]!.Array("enum").Single()!.GetValue<string>()=="insufficient_evidence");
            }
        });
        test("Bare first titles and explicit second parts share a catalog relationship",()=>{
            Check(EpisodeCoverage.AreNumberedParts(Parts)&&EpisodeCoverage.HasSharedDescription(Parts));
            Check(EpisodeCoverage.AreNumberedParts([Parts[0] with{Title="The Rescue, part 1"},Parts[1]]));
            Check(!EpisodeCoverage.AreNumberedParts([Parts[0],Parts[1] with{Number=3}]));
            Check(!EpisodeCoverage.AreNumberedParts([Parts[0],Parts[1] with{Title="The Escape - Part 2"}]));
            Check(!EpisodeCoverage.AreNumberedParts([Parts[0],Parts[1] with{Title="The Rescue"}]));
            Check(!EpisodeCoverage.HasSharedDescription([Parts[0],Parts[1] with{Overview="A different installment synopsis."}]));
            Check(!EpisodeCoverage.HasSharedDescription([Parts[0] with{Title="The Rescue (2)"},Parts[1] with{Title="The Rescue (3)"}]));
        });
        test("A shared-guide candidate expands to companions for audit without assigning them",()=>{
            Check(EpisodeCoverage.SharedGroup([Parts[1]],Parts).Select(e=>e.Id).SequenceEqual(new[]{10,11}));
            Check(EpisodeCoverage.SharedGroup([Parts[0]],[Parts[0],Parts[1] with{Number=3}]).Count==1);
        });
        test("Shared synopsis grounding retains independent ordered installment evidence",()=>{
            var result=Expand(Shared());Check(result.Text("coverage")=="combined"&&result.Array("matches").Count==2);
            Check(result["shared_guide_grounding"]!.Array("candidate_checks").Count==1&&result.Array("installment_evidence").Count==2);
            Check(result.Array("candidate_checks")[0]!.Array("supporting_blocks").Select(n=>n!.GetValue<int>()).SequenceEqual(new[]{0,1}));
            result["method"]=DialogueMatcher.Version;result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(Cues,Data.Options));
            var row=new JsonObject{["reasoning"]=result,["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(Cues,Data.Options)},
                ["candidates"]=new JsonArray(Parts.Select(e=>(JsonNode)new JsonObject{["episode"]=JsonSerializer.SerializeToNode(e,Data.Options)}).ToArray())};
            Check(MultipartEvidence.Combined(row));row["reasoning"]!["shared_guide_audit"]!["installments"]!["c1"]=new JsonArray("s0","s1");Check(!MultipartEvidence.Combined(row));
        });
        var corrupt=new Action<JsonObject>[] {
            r=>r["installments"]!["c1"]=new JsonArray("s0","s1"),
            r=>r["installments"]!["c1"]=new JsonArray("s3","s2"),
            r=>r["installments"]!["c1"]=new JsonArray("s3"),
            r=>r["installments"]!["c1"]=new JsonArray("s2","s999"),
            r=>r["installments"]!.AsObject().Remove("c1"),
            r=>r["recording"]!["candidates"]=new JsonArray("c0"),
            r=>r["recording"]!["kind"]="single_episode",
            r=>r["candidates"]!["c0"]!["statements"]!["g0"]!["verdict"]="insufficient_evidence",
            r=>r["candidates"]!["c0"]!["role"]="preview_or_promo"
        };
        for(int i=0;i<corrupt.Length;i++){var mutation=corrupt[i];test("Shared guides reject unsupported range or extent "+i,()=>{var r=Shared();mutation(r);Reject(()=>Expand(r));});}
        test("Shared synopsis evidence alone cannot force a combined recording",()=>Check(Expand(Shared(false)).Array("matches").Count==0));
        test("Concentrated shared-part quotes cannot masquerade as whole installments",()=>{
            var cues=new[]{Cues[0],Cues[1] with{Start=40},Cues[2] with{Start=80},Cues[3] with{Start=800}};
            var blocks=DialogueMatcher.Blocks(cues);Reject(()=>SharedGuideGrounding.Expand(Shared(),DialogueEvidence.Spans(blocks),blocks,Parts));
        });
        test("Shared-guide verification uses two independent full-dialogue audits",()=>{
            using var handler=new Handler((s,_)=>{Check(s["catalog"]==null&&s["candidates"]!.AsObject().Count==1&&s.Text("dialogue").Contains("Original block b3"));return Shared();});
            using var http=new HttpClient(handler);var candidate=new TitleCandidate("release.mkv",Parts,"fixture");
            var r=new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,candidate,"m",Endpoint).GetAwaiter().GetResult();
            Check(handler.Sources.Count==2&&TitleCandidates.Accepted(r,candidate,Cues)&&r["grounded_verification"].Int("passes")==2);
        });
        test("Disagreeing shared-guide audits remain unresolved",()=>{
            using var handler=new Handler((_,n)=>Shared(n==1));using var http=new HttpClient(handler);
            Reject(()=>new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,new("release",Parts,"fixture"),"m",Endpoint).GetAwaiter().GetResult());Check(handler.Sources.Count==2);
        });
        foreach(bool abstain in new[]{false,true})test("An invalid citation gets one focused same-candidate retry: abstain="+abstain,()=>{
            using var handler=new Handler((s,n)=>{
                Check(s["catalog"]==null);var r=Audit();
                if(n==1)r["candidates"]!["c0"]!["statements"]!["g0"]!["evidence"]=new JsonArray("s0");
                else {Check(s.Text("validation_feedback").Contains("two distinct"));if(abstain){r["recording"]!["kind"]="unresolved";r["recording"]!["candidates"]=new JsonArray();}}
                return r;
            });using var http=new HttpClient(handler);var r=new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint).GetAwaiter().GetResult();
            Check(handler.Sources.Count==2&&r.Array("matches").Count==(abstain?0:1)&&r["grounded_verification"]!.Array("raw_audits")[0].Text("error").Length>0);
        });
        test("Invalid recording decisions can abstain without a full catalog search",()=>{
            using var handler=new Handler((s,n)=>{var r=Audit();r["candidates"]!["c0"]!["statements"]!["g0"]!["verdict"]="insufficient_evidence";
                if(n==2){Check(s.Text("validation_feedback").Length>0);r["recording"]!["kind"]="unresolved";r["recording"]!["candidates"]=new JsonArray();}return r;});
            using var http=new HttpClient(handler);var r=new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint).GetAwaiter().GetResult();Check(handler.Sources.Count==2&&r.Array("matches").Count==0);
        });
        test("Repeated invalid evidence stops after one retry and preserves candidates",()=>{
            using var handler=new Handler((_,_)=>{var r=Audit();r["candidates"]!["c0"]!["statements"]!["g0"]!["evidence"]=new JsonArray();return r;});using var http=new HttpClient(handler);
            try{new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint).GetAwaiter().GetResult();throw new Exception("Invalid response accepted");}
            catch(InvalidDataException ex){Check(handler.Sources.Count==2&&ex.Data["model_candidate_ids"] is string ids&&ids=="[10]"&&JsonNode.Parse((string)ex.Data["model_attempts"]!)!.AsArray().Count==2);}
        });
        test("Focused retries obey the model request budget",()=>{
            using var handler=new Handler((_,n)=>{var r=Audit();if(n>1)r["candidates"]!["c0"]!["statements"]!["g0"]!["evidence"]=new JsonArray();return r;});using var http=new HttpClient(handler);
            var matcher=new DialogueMatcher(http,new(){MaxRequests=2});matcher.VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint).GetAwaiter().GetResult();
            try{matcher.VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint).GetAwaiter().GetResult();throw new Exception("Budget ignored");}
            catch(UsageLimitException){Check(handler.Sources.Count==2);}
        });
        test("Focused retries obey cancellation",()=>{
            using var stop=new CancellationTokenSource();using var handler=new Handler((_,_)=>{stop.Cancel();return Audit();});using var http=new HttpClient(handler);
            try{new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,new("release",[Parts[0]],"fixture"),"m",Endpoint,stop.Token).GetAwaiter().GetResult();throw new Exception("Cancellation ignored");}
            catch(OperationCanceledException){Check(handler.Sources.Count==1);}
        });
    }
}
