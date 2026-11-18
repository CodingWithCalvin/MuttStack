using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class ResolutionTests
{
    private sealed class Progress(Action<string> action):IProgress<string> {public void Report(string value)=>action(value);}
    public static void Run(Action<string,Action> test,string temp) {
        var root=Path.Combine(temp,"resolution");Directory.CreateDirectory(root);
        Episode[] catalog=[new(1,2,9,"Teacher","A teacher becomes depressed after a play and his students try to help."),new(2,2,10,"Song","A musician develops a crush on his best friend's girlfriend while writing a song.")];
        const string endpoint="http://127.0.0.1:1234/v1/chat/completions";
        void Check(bool value,string message="Resolution assertion failed") {if(!value)throw new Exception(message);}
        void Reject(Action action) {try{action();}catch(InvalidOperationException){return;}catch(ArgumentException){return;}throw new Exception("Expected rejection");}
        List<Cue> Cues(string tag)=>Enumerable.Range(0,32).Select(i=>new Cue(i*40,$"{tag} scene{i} person{i} location{i} says original words about a separate event.")).ToList();
        JsonObject Decision(int id,bool supported=true,int first=0)=>new(){["assessment"]=supported?"supported":"uncertain",["coverage"]=supported?"complete":"unknown",["note"]="",
            ["checks"]=new JsonArray(new JsonObject{["episode_id"]=id,["verdict"]=supported?"supported":"contradicted",["content_type"]=supported?"complete_story":"unknown",["supporting_blocks"]=supported?new JsonArray(first,first+1):new JsonArray(),
                ["contradicting_blocks"]=supported?new JsonArray():new JsonArray(0,1),["reason"]=supported?"The defining events are present in the original dialogue.":"The dialogue describes a different task and consequence from the guide."})};
        JsonObject Row(string name,int[]? ids=null,List<Cue>? cues=null) {
            ids??=[1];cues??=Cues(name);var path=Path.Combine(root,name);File.WriteAllText(path,name);
            var raw=Decision(ids[0]);if(ids.Length==2){raw["coverage"]="combined";raw.Array("checks").Add(Decision(ids[1],first:2).Array("checks")[0]!.DeepClone());}
            var reasoning=CompactVerification.Expand(raw,DialogueMatcher.Blocks(cues),catalog);DialogueMatcher.Validate(reasoning,DialogueMatcher.Blocks(cues),catalog,true);
            reasoning["method"]=DialogueMatcher.Version;reasoning["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
            return new JsonObject{["path"]=path,["size"]=new FileInfo(path).Length,["sampled_digest"]=PreviewPlanner.SampledDigest(path),["source_mtime_ns"]=PreviewPlanner.MtimeNs(path),
                ["full_dialogue"]=true,["duration"]=1280d,["matching_method"]="reasoning",["reasoning"]=reasoning,["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)},
                ["candidates"]=new JsonArray(ids.Select(id=>(JsonNode)new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==id),Data.Options)}).ToArray()),
                ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                    ["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
        }
        JsonObject Report(params JsonObject[] rows)=>new(){["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray()),["series_identity"]=JsonSerializer.SerializeToNode(new SeriesIdentity("tvdb","123","Show"),Data.Options)};
        void Review(JsonObject report,FakeHandler handler,Action? save=null,ScoutPause? pause=null,IProgress<string>? progress=null) {
            using var http=new HttpClient(handler,false);
            ClaimReview.RunAsync(report,catalog,new DialogueMatcher(http),"model",endpoint,save??(()=>{}),progress??new Progress(_=>{}),default,pause).GetAwaiter().GetResult();
        }
        test("Human force assignment displaces competing claims without editing model output and undo restores conflict",()=>{
            var a=Row("force-a.mkv");var b=Row("force-b.mkv");var report=Report(a,b);string original=a.ToJsonString();
            var id=HumanResolution.Apply(report,a,catalog[0],catalog,"I reviewed the video.");var groups=CollectionResolver.Resolve(report);
            Check(groups.Single(g=>g.Rows.Contains(a)).Episode?.Id==1&&groups.Single(g=>g.Rows.Contains(b)).Status=="overridden_conflict");
            Check(original==a.ToJsonString()&&report.Bool("manual_corrections")&&IssueReview.Select(report).Count==0);
            Check(EvidenceReview.Create(groups[0]).Assessment.StartsWith("Human override"));
            HumanResolution.Undo(report,id);Check(!report.Bool("manual_corrections")&&report.Array("human_resolutions")[0].Bool("undone"));
            Check(CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict")&&a.ToJsonString()==original);
        });
        test("Latest explicit human assignment wins and undo restores the earlier human decision",()=>{
            var a=Row("latest-a.mkv");var b=Row("latest-b.mkv");var report=Report(a,b);
            HumanResolution.Apply(report,a,catalog[0],catalog,"");var id=HumanResolution.Apply(report,b,catalog[0],catalog,"");
            Check(CollectionResolver.Resolve(report).Single(g=>g.Episode!=null).Rows.Contains(b));HumanResolution.Undo(report,id);
            Check(CollectionResolver.Resolve(report).Single(g=>g.Episode!=null).Rows.Contains(a)&&report.Bool("manual_corrections"));
        });
        test("Forcing one episode releases the other claim of a mistaken combined suggestion",()=>{
            var a=Row("force-combined.mkv",[1,2]);var b=Row("force-single.mkv",[2]);var report=Report(a,b);
            HumanResolution.Apply(report,a,catalog[0],catalog,"");var groups=CollectionResolver.Resolve(report);
            Check(groups.All(g=>g.Episodes.Count==1)&&groups.Select(g=>g.Episode!.Id).ToHashSet().SetEquals([1,2]));
            Check(a.Array("candidates").Count==2&&a["reasoning"].Text("coverage")=="combined");
        });
        test("Overrides expire on evidence or series changes and reject unverified sources or foreign episodes",()=>{
            var a=Row("expire.mkv");var report=Report(a);HumanResolution.Apply(report,a,catalog[0],catalog,"");
            a["dialogue"]!["cues"]![0]!["text"]="New evidence";Check(CollectionResolver.Resolve(report).Single().Status=="stale_human_override");
            report["series_identity"]!["id"]="other";Check(CollectionResolver.Resolve(report).Single().Episode==null);
            Reject(()=>HumanResolution.Apply(report,a,new Episode(900,1,1,"Foreign"),catalog,""));
            File.AppendAllText(a.Text("path"),"changed");Reject(()=>HumanResolution.Apply(report,a,catalog[0],catalog,""));
        });
        test("Override previews retain source and occupied destination checks and invalidate cached plans",()=>{
            var a=Row("preview.mkv");var report=Report(a);var naming=new NamingOptions{PreserveCorrectNames=false};
            var before=PreviewCache.Signature(report,root,"Show",catalog,naming,null);HumanResolution.Apply(report,a,catalog[1],catalog,"");
            Check(before!=PreviewCache.Signature(report,root,"Show",catalog,naming,null));
            var item=PreviewPlanner.Create(report,root,"Show",catalog,naming:naming).Items.Single();Check(item.Episode?.Id==2&&item.Notes.Any(n=>n.Contains("forced by the user")));
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination!)!);File.WriteAllText(item.Destination!,"occupied");
            Check(PreviewPlanner.Create(report,root,"Show",catalog,naming:naming).Items.Single().Action=="hold");
            File.AppendAllText(a.Text("path"),"changed");Check(IssueReview.Select(report).Contains(a.Text("path")));
            Check(PreviewPlanner.Create(report,root,"Show",catalog,naming:naming).Items.Single().BlockedReasons.Any(r=>r.Contains("Source changed")));
        });
        test("Overrides survive pipeline checkpoints and restart without rerunning the model",()=>{
            var a=Row("persist.mkv");var report=Report(a);HumanResolution.Apply(report,a,catalog[1],catalog,"Confirmed");
            var output=Path.Combine(temp,"resolution-persist");var guide=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
            Data.Save(guide,new{episodes=catalog});Data.Save(reportPath,report);using var http=new HttpClient(new FakeHandler());
            var settings=new MatchSettings(root,guide,output,"model",endpoint,SelectedFiles:[a.Text("path")],SeriesIdentity:new("tvdb","123","Show"),Restart:true);
            new MediaPipeline(new DialogueMatcher(http)).RunAsync(settings,new Progress(_=>{}),default).GetAwaiter().GetResult();
            var saved=Data.Read(reportPath);Check(saved.Bool("manual_corrections")&&JsonNode.DeepEquals(saved["human_resolutions"],report["human_resolutions"]));
            Check(CollectionResolver.Resolve(saved).Single().Episode?.Id==2);
        });
        test("Reanalysis saves a new model result while retaining the explicit human decision",()=>{
            var a=Row("reanalyze.mkv");var report=Report(a);HumanResolution.Apply(report,a,catalog[1],catalog,"Confirmed by user");
            var initial=new JsonObject{["assessment"]="supported",["coverage"]="complete",["summary"]="The teacher story occurs in the dialogue.",
                ["candidate_checks"]=new JsonArray(new JsonObject{["episode_title"]=catalog[0].Title,["supporting_blocks"]=new JsonArray(0,1),["contradicting_blocks"]=new JsonArray(),["event_comparison"]="The defining guide events occur.",["guide_event"]=catalog[0].Overview}),
                ["matches"]=new JsonArray(new JsonObject{["episode_title"]=catalog[0].Title,["reason"]="The teacher story matches the source."}),["alternatives"]=new JsonArray()};
            var output=Path.Combine(temp,"resolution-reanalyze");var guide=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
            Data.Save(guide,new{episodes=catalog});Data.Save(reportPath,report);var handler=new FakeHandler{Results=[initial,initial.DeepClone().AsObject()]};using var http=new HttpClient(handler);
            var settings=new MatchSettings(root,guide,output,"model",endpoint,SelectedFiles:[a.Text("path")],SeriesIdentity:new("tvdb","123","Show"),Reanalyze:true);
            new MediaPipeline(new DialogueMatcher(http)).RunAsync(settings,new Progress(_=>{}),default).GetAwaiter().GetResult();
            var saved=Data.Read(reportPath);Check(handler.Requests.Count==2&&saved.Bool("manual_corrections")&&JsonNode.DeepEquals(saved["human_resolutions"],report["human_resolutions"]));
            Check(saved.Array("files")[0].Array("candidates")[0]!["episode"].Int("id")==1&&CollectionResolver.Resolve(saved).Single().Episode?.Id==2);
        });
        test("Pending moves prevent forcing or undoing an assignment",()=>{
            var a=Row("pending.mkv");var report=Report(a);var id=HumanResolution.Apply(report,a,catalog[0],catalog,"");
            report["moves"]=new JsonArray(new JsonObject{["state"]="prepared"});
            Reject(()=>HumanResolution.Apply(report,a,catalog[1],catalog,""));Reject(()=>HumanResolution.Undo(report,id));
            Check(!report.Array("human_resolutions")[0].Bool("undone"));
        });
        test("Missing sources retain history without blocking available claims and are restored when found",()=>{
            var a=Row("missing.mkv");var b=Row("available.mkv");var report=Report(a,b);File.Delete(a.Text("path"));
            RecordingRelocation.ReconnectAsync(report,root,"unused",new Progress(_=>{}),default).GetAwaiter().GetResult();
            var groups=CollectionResolver.Resolve(report);Check(groups.Single(g=>g.Rows.Contains(a)).Status=="source_unavailable"&&groups.Single(g=>g.Rows.Contains(b)).Episode?.Id==1);
            Check(a.Array("candidates").Count==1&&!IssueReview.Select(report).Contains(a.Text("path")));
            File.WriteAllText(a.Text("path"),"returned");RecordingRelocation.ReconnectAsync(report,root,"unused",new Progress(_=>{}),default).GetAwaiter().GetResult();
            Check(!RecordingAvailability.Missing(a)&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
        });
        test("An offline source root is not treated as individually missing recordings",()=>{
            var a=Row("offline.mkv");var report=Report(a);File.Delete(a.Text("path"));
            try{RecordingRelocation.ReconnectAsync(report,Path.Combine(temp,"offline-root"),"unused",new Progress(_=>{}),default).GetAwaiter().GetResult();throw new Exception("Expected offline failure");}catch(DirectoryNotFoundException){}
            Check(!RecordingAvailability.Missing(a));
        });
        test("Alternate encodings group only with identical complete normalized dialogue and compatible duration",()=>{
            var cues=Cues("common");var a=Row("copy-a.mkv",cues:cues);var b=Row("copy-b.mkv",cues:cues.Select(c=>c with{Text=c.Text.ToUpperInvariant()+"!"}).ToList());var report=Report(a,b);
            var group=CollectionResolver.Resolve(report).Single();Check(group.DialogueCopies&&group.Duplicate&&group.Episode?.Id==1);
            b["duration"]=1800d;Check(CollectionResolver.Resolve(report).Count==2);b["duration"]=1280d;
            b["dialogue"]!["cues"]![31]!["text"]="Different ending";b["reasoning"]!["dialogue_hash"]=MultipartEvidence.DialogueHash(b);
            Check(CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict"));
        });
        test("Short or partial dialogue never groups different recordings as copies",()=>{
            var cues=Cues("intro").Take(4).ToList();var a=Row("intro-a.mkv",cues:cues);var b=Row("intro-b.mkv",cues:cues);var report=Report(a,b);
            Check(CollectionResolver.Resolve(report).Count==2);a["full_dialogue"]=false;b["full_dialogue"]=false;
            Check(CollectionResolver.Resolve(report).All(g=>g.Episode==null));
        });
        test("Focused checks remove a rejected combined claim automatically without feeding previous outputs",()=>{
            var a=Row("combined.mkv",[1,2],Cues("teacher"));var b=Row("single.mkv",[2],Cues("song"));var report=Report(a,b);
            a["reasoning"]!["summary"]="DO NOT LEAK PREVIOUS SUMMARY";
            var handler=new FakeHandler{Results=[Decision(1),Decision(2,false),Decision(2)]};Review(report,handler);
            Check(handler.Requests.Count==3&&CollectionResolver.Resolve(report).All(g=>g.Episodes.Count==1));
            Check(a.Array("candidates").Count==1&&a["claim_resolution"]!["previous_candidates"]!.AsArray().Count==2&&!report.Bool("manual_corrections"));
            foreach(var request in handler.Requests){var text=request["messages"]![1].Text("content");var source=JsonNode.Parse(text)!;Check(source.Array("catalog").Count==1&&source.Array("dialogue_blocks").Count==32&&!text.Contains("DO NOT LEAK")&&!text.Contains("combined.mkv"));}
        });
        test("Focused review checkpoints each decision and resumes without repeating successful inference",()=>{
            var a=Row("pause-combined.mkv",[1,2]);var b=Row("pause-single.mkv",[2]);var report=Report(a,b);var pause=new ScoutPause();
            var handler=new FakeHandler{Results=[Decision(1),Decision(2,false),Decision(2)]};
            try{Review(report,handler,()=>{if(a["claim_resolution"].Array("checks").Count==1)pause.Request();},pause);throw new Exception("Expected pause");}catch(ScoutPausedException){}
            Check(handler.Requests.Count==1&&report.Array("files").All(r=>r.Bool("claim_review_pending"))&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
            Review(report,handler);Check(handler.Requests.Count==3&&report.Array("files").All(r=>!r.Bool("claim_review_pending"))&&CollectionResolver.Resolve(report).All(g=>g.Episode!=null));
            Review(report,handler);Check(handler.Requests.Count==3);
        });
        test("Unresolved separate checks remain conflicts without choosing the first recording",()=>{
            var a=Row("tie-a.mkv");var b=Row("tie-b.mkv");var report=Report(a,b);var handler=new FakeHandler{Results=[Decision(1),Decision(1)]};Review(report,handler);
            Check(CollectionResolver.Resolve(report).All(g=>g.Status=="unresolved_conflict"));Review(report,handler);Check(handler.Requests.Count==2);
        });
        test("Focused review holds inconclusive or partial combined claims instead of inventing complete coverage",()=>{
            foreach(bool partial in new[]{false,true}) {
                var a=Row($"ambiguous-{partial}.mkv",[1,2]);var b=Row($"competitor-{partial}.mkv",[2]);var report=Report(a,b);
                var second=Decision(2,partial,first:2);
                if(partial){second["coverage"]="partial";second.Array("checks")[0]!["content_type"]="partial_story";}else second.Array("checks")[0]!["verdict"]="insufficient_evidence";
                Review(report,new FakeHandler{Results=[Decision(1),second,Decision(2)]});
                Check(a.Text("claim_review_error")!=""&&a.Array("candidates").Count==2&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
            }
        });
        test("Issue-review pipeline reuses its full-guide passes before focused conflict resolution",()=>{
            var a=Row("pipeline-combined.mkv",[1,2],Cues("teacher"));var b=Row("pipeline-single.mkv",[2],Cues("song"));var report=Report(a,b);
            var combined=Decision(1);combined["coverage"]="combined";combined.Array("checks").Add(Decision(2,first:2).Array("checks")[0]!.DeepClone());
            var sourceCues=MultipartEvidence.Cues(a);
            var grounded=new JsonObject{["candidates"]=new JsonArray(Enumerable.Range(0,2).Select(i=>(JsonNode)new JsonObject{
                ["candidate"]=i,["statements"]=new JsonArray(new JsonObject{["statement"]=0,["verdict"]="supported",["evidence"]=new JsonArray(Enumerable.Range(i*2,2).Select(b=>(JsonNode)new JsonObject{["block"]=b,["quote"]=sourceCues[b].Text}).ToArray())}),
                ["recording_scope"]="one_of_multiple_stories",["content_type"]="complete_story",["story_blocks"]=new JsonArray(i*2,i*2+1),["reason"]="Original events support this complete story."}).ToArray())};
            var handler=new FakeHandler{Results=[combined,grounded,grounded.DeepClone().AsObject(),Decision(2),Decision(2),Decision(1),Decision(2,false),Decision(2)]};using var http=new HttpClient(handler);
            var output=Path.Combine(temp,"resolution-pipeline");var guide=Path.Combine(output,"catalog.json");var reportPath=Path.Combine(output,"report.json");
            Data.Save(guide,new{episodes=catalog});Data.Save(reportPath,report);
            new MediaPipeline(new DialogueMatcher(http)).RunAsync(new(root,guide,output,"model",endpoint,SelectedFiles:[a.Text("path"),b.Text("path")],SeriesIdentity:new("tvdb","123","Show"),ReviewIssues:true),new Progress(_=>{}),default).GetAwaiter().GetResult();
            var saved=Data.Read(reportPath);Check(handler.Requests.Count==8&&CollectionResolver.Resolve(saved).All(g=>g.Episodes.Count==1));
            Check(!saved["issue_review"].Bool("active")&&!saved.Bool("manual_corrections")&&saved.Array("files").All(r=>r!["claim_resolution"]!=null));
        });
        test("Failed or changed-source focused checks cannot release a conflicting assignment",()=>{
            var a=Row("failure-a.mkv");var b=Row("failure-b.mkv");var report=Report(a,b);var handler=new FakeHandler{Results=[Decision(1)]};
            Review(report,handler,progress:new Progress(_=>File.AppendAllText(a.Text("path"),"changed")));
            Check(a.Text("claim_review_error")!=""&&CollectionResolver.Resolve(report).All(g=>g.Episode==null));
        });
    }
}
