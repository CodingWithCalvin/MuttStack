using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;

static class VerificationTests
{
    // Adapter for synthetic fixtures only, never real saved model responses.
    public static JsonObject RecordingResponse(JsonObject fixture) {
        if(fixture["recording"]!=null)return fixture;
        var members=fixture.Array("candidates").Where(c=>c.Array("statements").All(f=>f.Text("verdict")=="supported")&&
            EpisodeCoverage.Scope(c.Text("recording_scope"))!="not_established"&&EpisodeCoverage.Content(c.Text("content_type")) is "complete_episode" or "incomplete_episode").ToList();
        var scope=members.Count==0?"":EpisodeCoverage.Scope(members[0].Text("recording_scope"));
        string kind=scope switch {"entire_episode"=>"single_episode","one_of_separate_episodes"=>"separate_episodes","one_of_continuous_parts"=>"continuous_parts","incomplete_episode"=>"incomplete_episode",_=>"unresolved"};
        var result=fixture.DeepClone().AsObject();foreach(var c in result.Array("candidates")) {c!.AsObject().Remove("recording_scope");c["content_type"]=EpisodeCoverage.Content(c.Text("content_type"));}
        result["recording"]=new JsonObject{["kind"]=kind,["candidates"]=new JsonArray(members.Select(c=>c!["candidate"]!.DeepClone()).ToArray()),["reason"]="Synthetic recording decision based on the original fixture's episode coverage."};return result;
    }
    // Existing integration fixtures use the persisted review format. Encode them as
    // the new wire format when the actual request asks for compact verification.
    public static JsonObject WireResponse(JsonNode request,JsonObject legacy) {
        if(request["response_format"]?["json_schema"]?["schema"]?["properties"]?["candidates"]!=null) {
            if(legacy["candidates"]!=null)return RecordingResponse(legacy);
            var groundedSource=JsonNode.Parse(request["messages"]![1].Text("content"))!;var audits=new JsonArray();
            foreach(var candidate in groundedSource.Array("candidates")) {
                int index=candidate.Int("candidate");var c=legacy.Array("candidate_checks")[index]!;
                var ids=c.Array("supporting_blocks");
                var evidence=new JsonArray(ids.Take(2).Select(id=>{var text=groundedSource.Array("dialogue_blocks")[id!.GetValue<int>()].Text("text");return (JsonNode)new JsonObject{["block"]=id.DeepClone(),["quote"]=text[..Math.Min(160,text.Length)]};}).ToArray());
                audits.Add(new JsonObject{["candidate"]=index,["statements"]=new JsonArray(candidate.Array("statements").Select(s=>(JsonNode)new JsonObject{["statement"]=s!["statement"]!.DeepClone(),["verdict"]="supported",["evidence"]=evidence.DeepClone()}).ToArray()),
                    ["recording_scope"]=legacy.Array("matches").Count==2?"one_of_multiple_stories":"entire_story",["content_type"]="complete_story",["story_blocks"]=ids.DeepClone(),["reason"]="Distinctive original story events agree."});
            }
            return RecordingResponse(new JsonObject{["candidates"]=audits});
        }
        if(request["response_format"]?["json_schema"]?["schema"]?["properties"]?["checks"]==null) {
            var initial=legacy.DeepClone().AsObject();
            if(request["response_format"]?["json_schema"]?["schema"]?["properties"]?["candidate_checks"]!=null)
                foreach(var check in initial.Array("candidate_checks"))check!["content_type"]??=initial.Array("matches").Any(m=>m.Text("episode_title")==check.Text("episode_title"))?(initial.Text("coverage")=="partial"?"partial_story":"complete_story"):"unknown";
            return initial;
        }
        if(legacy["checks"]!=null)return legacy;
        var source=JsonNode.Parse(request["messages"]![1].Text("content"))!;
        var checks=new JsonArray();
        foreach(var c in legacy.Array("candidate_checks")) {
            var episode=source.Array("catalog").Single(e=>e.Text("title")==c.Text("episode_title"));
            bool selected=legacy.Array("matches").Any(m=>m.Text("episode_title")==c.Text("episode_title"));
            checks.Add(new JsonObject{["episode_id"]=episode.Int("id"),["verdict"]=selected?"supported":c.Array("contradicting_blocks").Count>0?"contradicted":"insufficient_evidence",["content_type"]=c.Text("content_type",selected?(legacy.Text("coverage")=="partial"?"partial_story":"complete_story"):"unknown"),
                ["supporting_blocks"]=c!["supporting_blocks"]!.DeepClone(),["contradicting_blocks"]=c["contradicting_blocks"]!.DeepClone(),["reason"]=c["event_comparison"]!.DeepClone()});
        }
        return new(){["assessment"]=legacy["assessment"]!.DeepClone(),["coverage"]=legacy["coverage"]!.DeepClone(),["checks"]=checks,["note"]=checks.Count==0?legacy.Text("summary"):""};
    }
    public static string Evidence(JsonNode request) {
        var source=JsonNode.Parse(request["messages"]![1].Text("content"))!.AsObject();source.Remove("task");return source.ToJsonString();
    }
    static void Check(bool value){if(!value)throw new Exception("Assertion failed.");}
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid decision accepted.");}
    public static void Run(Action<string,Action> test) {
        test("Prompt JSON preserves literal dialogue without losing JSON escaping or source content",()=>{
            var source=new JsonObject{["dialogue"]=">> I'M Tori & Jade <dialogue> – café \"hello\"\nC:\\folder",["nested"]=new JsonArray("one",2)};
            foreach(var provider in new[]{"managed","compatible","openai"}) {
                var handler=new Handler([new JsonObject()],provider);using var http=new HttpClient(handler);
                var endpoint=provider=="managed"?"http://127.0.0.1:1234/v1/chat/completions":provider=="openai"?"https://api.openai.com/v1/responses":"https://example.com/v1/chat/completions";
                new ModelTransport(http,new(){Provider=provider,AllowCloudDialogue=true,ApiKey="fake"}).CompleteAsync("test",endpoint,"rules",source,new JsonObject(),default).GetAwaiter().GetResult();
                var text=handler.Requests[0][provider=="openai"?"input":"messages"]![1].Text("content");
                Check(text.Contains(">> I'M Tori & Jade")&&text.Contains("café")&&!text.Contains("\\u003E",StringComparison.OrdinalIgnoreCase));
                Check(JsonNode.DeepEquals(JsonNode.Parse(text),source));
            }
        });
        test("Managed CPU Gemma retains its full attention cache without changing other profiles",()=>{
            var s=new AppSettings{Provider="managed",DownloadProfile="gemma4-31b",GpuLayers=0,ThinkingEnabled=false};Check(s.ManagedFullSwaCache&&!s.ManagedThinking);
            s.GpuLayers=8;Check(!s.ManagedFullSwaCache);s.GpuLayers=0;s.Provider="local";Check(!s.ManagedFullSwaCache);s.Provider="managed";
            s.DownloadProfile="qwen36-35b";Check(!s.ManagedFullSwaCache);s.ModelFile=Path.Combine("models","gemma-4-31B-it-Q4_K_M.gguf");Check(s.ManagedFullSwaCache);
            s.ModelFile=Path.Combine("models","custom.gguf");Check(!s.ManagedFullSwaCache);
        });
        var cues=Enumerable.Range(0,32).Select(i=>new Cue(i*5,"Original evidence "+i)).ToArray();var blocks=DialogueMatcher.Blocks(cues);
        Episode[] episodes=[new(1,1,1,"First","First guide."),new(2,1,2,"Second","Second guide.")];
        JsonObject Decision()=>JsonNode.Parse("""{"assessment":"supported","coverage":"complete","checks":[{"episode_id":1,"verdict":"supported","content_type":"complete_story","supporting_blocks":[0,1],"contradicting_blocks":[],"reason":"Distinctive events agree."}],"note":""}""")!.AsObject();
        test("Compact verification validates IDs evidence contradictions and abstention",()=>{
            var valid=CompactVerification.Expand(Decision(),blocks,episodes);DialogueMatcher.Validate(valid,blocks,episodes,true);
            Check(valid.Array("matches")[0].Int("episode_id")==1&&valid.Array("candidate_checks")[0].Text("guide_event")=="First guide.");
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                d=>d["checks"]![0]!["episode_id"]=999,
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0),
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0,999),
                d=>d["checks"]![0]!["contradicting_blocks"]=new JsonArray(2),
                d=>d["checks"]![0]!["supporting_blocks"]=new JsonArray(0,0),
                d=>d["assessment"]="uncertain",
                d=>d["checks"]![0]!["reason"]=string.Concat(Enumerable.Repeat("loop ",20)),
                d=>d["checks"]!.AsArray().Add(d["checks"]![0]!.DeepClone())
            }) {var d=Decision();change(d);Reject(()=>CompactVerification.Expand(d,blocks,episodes));}
            var abstain=Decision();abstain["assessment"]="uncertain";abstain["coverage"]="unknown";abstain["checks"]![0]!["verdict"]="insufficient_evidence";
            Check(CompactVerification.Expand(abstain,blocks,episodes).Array("matches").Count==0);
            abstain["checks"]![0]!["verdict"]="contradicted";Reject(()=>CompactVerification.Expand(abstain,blocks,episodes));
            abstain["checks"]=new JsonArray();Reject(()=>CompactVerification.Expand(abstain,blocks,episodes));abstain["note"]="An interview, with no supported episode framing.";
            abstain["assessment"]="non_episode";Check(CompactVerification.Expand(abstain,blocks,episodes).Text("assessment")=="non_episode");
        });
        test("Compact combined decisions require consecutive distinct ordered evidence",()=>{
            var d=Decision();d["coverage"]="combined";var second=d["checks"]![0]!.DeepClone();second["episode_id"]=2;second["supporting_blocks"]=new JsonArray(2,3);d["checks"]!.AsArray().Add(second);
            Check(CompactVerification.Expand(d,blocks,episodes).Array("matches").Count==2);
            second["supporting_blocks"]=new JsonArray(0,1);Reject(()=>CompactVerification.Expand(d,blocks,episodes));
        });
        test("Repeated candidate checks trigger a fresh model decision without merging the rejected output",()=>{
            var repeated=Decision();var subplot=repeated["checks"]![0]!.DeepClone();subplot["reason"]="A separate subplot also agrees.";
            repeated["checks"]!.AsArray().Add(subplot);
            var handler=new Handler([repeated,Decision()],"managed");using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http).DirectAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions",true).GetAwaiter().GetResult();
            Check(handler.Requests.Count==2&&result.Array("matches").Count==1&&result.Array("validation_retries").Count==1);
            Check(result.Array("validation_retries")[0]!["response"].Array("checks").Count==2);
            var retry=JsonNode.Parse(handler.Requests[1]["messages"]![1].Text("content"))!;
            Check(retry.Text("task").Contains("ONE check per episode ID")&&Evidence(handler.Requests[0])==Evidence(handler.Requests[1]));
            Check(handler.Requests[0]["response_format"]!["json_schema"]!["schema"]!["properties"]!["checks"].Int("maxItems")==episodes.Length);
            Check(!retry.Text("task").Contains("Use season and episode numbers to check adjacency"));
        });
        test("Nonconsecutive combined suggestions cannot be retried into an adjacent identity",()=>{
            var third=new Episode(3,3,12,"Third","An unrelated film story.");
            var bad=Decision();bad["coverage"]="combined";var extra=Decision()["checks"]![0]!.DeepClone();extra["episode_id"]=3;extra["supporting_blocks"]=new JsonArray(2,3);bad.Array("checks").Add(extra);
            var repaired=bad.DeepClone().AsObject();repaired["checks"]![1]!["episode_id"]=2;
            var handler=new Handler([bad,repaired],"managed");using var http=new HttpClient(handler);
            try{new DialogueMatcher(http).DirectAsync(cues,[..episodes,third],"m","http://127.0.0.1:1234/v1/chat/completions",true).GetAwaiter().GetResult();throw new Exception("Unsafe identity repair accepted.");}
            catch(InvalidDataException ex){Check(AssignmentValidation.IsTerminal(ex)&&ex.Data[AssignmentValidation.ReasonKey]?.ToString()==nameof(AssignmentIssue.NonConsecutiveEpisodes));Check(JsonNode.Parse((string)ex.Data["model_attempts"]!)!.AsArray()[0]!["response"]!["checks"]![1].Int("episode_id")==3);}
            Check(handler.Requests.Count==1);
        });
        test("Preview promo recap partial and unknown content cannot form a second complete episode",()=>{
            foreach(var type in new[]{"preview_or_promo","recap","incomplete_episode","partial_story","unknown"}) {
                var bad=Decision();bad["coverage"]="combined";var extra=Decision()["checks"]![0]!.DeepClone();extra["episode_id"]=2;extra["content_type"]=type;extra["supporting_blocks"]=new JsonArray(2,3);bad.Array("checks").Add(extra);
                var handler=new Handler([bad,Decision()],"managed");using var http=new HttpClient(handler);
                try{new DialogueMatcher(http).DirectAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions",true).GetAwaiter().GetResult();throw new Exception("Invalid story accepted: "+type);}
                catch(InvalidDataException ex){Check(AssignmentValidation.IsTerminal(ex));}
                Check(handler.Requests.Count==1);
            }
        });
        test("A complete episode with a rejected next-episode promo keeps only the complete episode",()=>{
            var d=Decision();var promo=d["checks"]![0]!.DeepClone();promo["episode_id"]=2;promo["content_type"]="preview_or_promo";promo["verdict"]="insufficient_evidence";promo["supporting_blocks"]=new JsonArray();promo["reason"]="Only a next-episode preview is present.";d.Array("checks").Add(promo);
            var result=CompactVerification.Expand(d,blocks,episodes);DialogueMatcher.Validate(result,blocks,episodes,true,requireContentType:true);
            Check(result.Array("matches").Count==1&&result.Array("matches")[0].Int("episode_id")==1&&result.Array("candidate_checks")[1].Text("content_type")=="preview_or_promo");
            var selectedPromo=Decision();selectedPromo["checks"]![0]!["content_type"]="preview_or_promo";Reject(()=>CompactVerification.Expand(selectedPromo,blocks,episodes));
        });
        test("Missing content classification is rejected and overlapping combined evidence is terminal",()=>{
            var missing=Decision();missing.Array("checks")[0]!.AsObject().Remove("content_type");Reject(()=>CompactVerification.Expand(missing,blocks,episodes));
            var overlap=Decision();overlap["coverage"]="combined";var second=overlap.Array("checks")[0]!.DeepClone();second["episode_id"]=2;overlap.Array("checks").Add(second);
            try{CompactVerification.Expand(overlap,blocks,episodes);throw new Exception("Overlapping stories accepted.");}catch(InvalidDataException ex){Check(AssignmentValidation.IsTerminal(ex));}
        });
        test("Both passes share rules and full dialogue prefix without leaking generated decisions",()=>{
            foreach(var provider in new[]{"managed","compatible","openai"}) {
                var initial=JsonNode.Parse("""{"assessment":"supported","coverage":"complete","summary":"INITIAL GENERATED SUMMARY","candidate_checks":[{"episode_title":"First","content_type":"complete_story","supporting_blocks":[0,1],"contradicting_blocks":[],"event_comparison":"Distinctive events agree."}],"matches":[{"episode_title":"First","reason":"First distinctive events."}],"alternatives":[]}""")!.AsObject();
                var handler=new Handler([initial,Decision()],provider);using var http=new HttpClient(handler);
                var matcher=new DialogueMatcher(http,new(){Provider=provider,AllowCloudDialogue=true,ApiKey="fake",EnableThinking=false});
                var endpoint=provider=="managed"?"http://127.0.0.1:1234/v1/chat/completions":provider=="openai"?"https://api.openai.com/v1/responses":"https://example.com/v1/chat/completions";
                var result=matcher.IdentifyAsync(cues,episodes,"test",endpoint).GetAwaiter().GetResult();
                var field=provider=="openai"?"input":"messages";var first=handler.Requests[0];var second=handler.Requests[1];
                Check(first[field]![0].Text("content")==second[field]![0].Text("content"));
                var a=first[field]![1].Text("content");var b=second[field]![1].Text("content");
                int prefix=a.IndexOf(",\"catalog\":",StringComparison.Ordinal);Check(prefix>500&&a[..prefix]==b[..prefix]);
                Check(!b.Contains("INITIAL GENERATED SUMMARY")&&result.Text("summary")=="INITIAL GENERATED SUMMARY");
                Check(provider=="managed"?first.Bool("cache_prompt")&&second.Bool("cache_prompt"):!first.ContainsKey("cache_prompt"));
                Check(result.Text("summary_source")=="initial_pass"&&result["verification_decision"]!=null);
            }
        });
        test("A changed verifier assignment never inherits the original summary",()=>{
            var initial=JsonNode.Parse("""{"assessment":"supported","coverage":"complete","summary":"WRONG INITIAL SUMMARY","candidate_checks":[{"episode_title":"First","content_type":"complete_story","supporting_blocks":[0,1],"contradicting_blocks":[],"event_comparison":"Distinctive events agree."}],"matches":[{"episode_title":"First","reason":"First events."}],"alternatives":[{"episode_title":"Second","reason_against":"Other events."}]}""")!.AsObject();
            var changed=Decision();changed["checks"]![0]!["episode_id"]=2;
            using var http=new HttpClient(new Handler([initial,changed],"managed"));var result=new DialogueMatcher(http).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches")[0].Int("episode_id")==2&&!result.Text("summary").Contains("WRONG INITIAL"));
        });
    }
    sealed class Handler(List<JsonObject> replies,string provider):HttpMessageHandler {
        public List<JsonObject> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());var content=replies[Requests.Count-1].ToJsonString();
            var response=provider=="openai"?new JsonObject{["status"]="completed",["output"]=new JsonArray(new JsonObject{["type"]="message",["content"]=new JsonArray(new JsonObject{["type"]="output_text",["text"]=content})})}:
                new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=content}})};
            return new(HttpStatusCode.OK){Content=new StringContent(response.ToJsonString())};
        }
    }
}
