using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;
static class GroundingTests
{
    static void Check(bool value){if(!value)throw new Exception("Grounding assertion failed.");}
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Unsafe grounded output accepted.");}
    public static void Run(Action<string,Action> test)
    {
        Cue[] cues=[new(0,"Today is our teacher's tenth anniversary."),new(40,"We are taking our teacher to a play."),new(80,"Are we making a student film? No, we are not."),new(120,"This is another attempt to help our teacher.")];
        Episode[] episodes=[new(101,2,9,"A title never sent","A teacher celebrates an anniversary. Friends take the teacher to a play."),new(102,3,12,"Another hidden title","They are making a student film.")];
        var blocks=DialogueMatcher.Blocks(cues);
        JsonObject Fact(int statement,string verdict,params int[] indices)=>new(){["statement"]=statement,["verdict"]=verdict,["evidence"]=new JsonArray(indices.Select(i=>(JsonNode)new JsonObject{["block"]=i,["quote"]=cues[i].Text}).ToArray())};
        JsonObject Audit()=>new(){["candidates"]=new JsonArray(
            new JsonObject{["candidate"]=0,["statements"]=new JsonArray(Fact(0,"supported",0),Fact(1,"supported",1)),["recording_scope"]="entire_story",["content_type"]="complete_story",["story_blocks"]=new JsonArray(0,1),["reason"]="The teacher's celebration and play form one continuing story."},
            new JsonObject{["candidate"]=1,["statements"]=new JsonArray(Fact(0,"contradicted",2)),["recording_scope"]="not_established",["content_type"]="unknown",["story_blocks"]=new JsonArray(),["reason"]="The dialogue explicitly denies making a film."})};
        JsonObject Proposal()=>new(){["assessment"]="supported",["coverage"]="combined",["checks"]=new JsonArray(new JsonObject{["episode_id"]=101,["verdict"]="supported"},new JsonObject{["episode_id"]=102,["verdict"]="supported"}),["note"]="OLD WRONG REASON MUST NOT LEAK"};
        test("Grounded review requires every guide statement and exact original quotes",()=>{
            var result=GroundedVerification.Expand(Audit(),blocks,episodes);
            Check(result.Array("matches").Count==1&&result.Array("matches")[0].Int("episode_id")==101&&result.Array("candidate_checks")[0].Array("guide_grounding").Count==2);
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                r=>r.Array("candidates")[0]!.Array("statements").RemoveAt(1),
                r=>r["candidates"]![0]!["statements"]![1]!["statement"]=0,
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]![0]!["quote"]="Invented dialogue that never happened.",
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]![0]!["quote"]=new string(' ',20),
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]![0]!["block"]=3,
                r=>r["candidates"]![0]!["story_blocks"]=new JsonArray(0,999),
                r=>r["candidates"]![1]!["candidate"]=0
            }){var raw=Audit();change(raw);Reject(()=>GroundedVerification.Expand(raw,blocks,episodes));}
        });
        test("A missing defining guide event cannot be replaced by supported background",()=>{
            var raw=Audit();raw["candidates"]![0]!["statements"]![1]!["verdict"]="insufficient_evidence";raw["candidates"]![0]!["statements"]![1]!["evidence"]=new JsonArray();
            Check(GroundedVerification.Expand(raw,blocks,episodes).Array("matches").Count==0);
        });
        test("Accepted evidence is derived from grounded quotes rather than a duplicated index list",()=>{
            var raw=Audit();raw["candidates"]![0]!["story_blocks"]=new JsonArray(2,3);var snapshot=raw.ToJsonString();
            var result=GroundedVerification.Expand(raw,blocks,episodes);
            Check(result.Array("matches").Single().Array("evidence").Select(e=>e.Int("index")).SequenceEqual(new[]{0,1})&&raw.ToJsonString()==snapshot);
        });
        test("Removing an unsupported candidate cannot hide a remaining unidentified story",()=>{
            var raw=Audit();raw["candidates"]![0]!["recording_scope"]="one_of_multiple_stories";Reject(()=>GroundedVerification.Expand(raw,blocks,episodes));
            foreach(var type in new[]{"preview_or_promo","recap","unknown"}){raw=Audit();raw["candidates"]![0]!["content_type"]=type;Check(GroundedVerification.Expand(raw,blocks,episodes).Array("matches").Count==0);}
        });
        test("Grounded verification hides titles numbers and prior answers and requires two agreeing audits",()=>{
            var handler=new Handler([Audit(),Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http).VerifySavedProposalAsync(cues,episodes,Proposal(),"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches").Single().Int("episode_id")==101&&handler.Requests.Count==2&&result["grounded_verification"]!=null);
            foreach(var request in handler.Requests){var source=JsonNode.Parse(request["messages"]![1].Text("content"))!;var text=source.ToJsonString();Check(!text.Contains("OLD WRONG REASON")&&!text.Contains(episodes[0].Title)&&!text.Contains(episodes[1].Title)&&!text.Contains("episode_id")&&!text.Contains("season")&&source.Array("dialogue_blocks").Count==blocks.Count);}
            var disagree=Audit();disagree["candidates"]![0]!["statements"]![0]!["verdict"]="insufficient_evidence";
            var other=new Handler([Audit(),disagree]);using var otherHttp=new HttpClient(other);
            Reject(()=>new DialogueMatcher(otherHttp).VerifySavedProposalAsync(cues,episodes,Proposal(),"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult());
        });
        test("Invalid combined proposal routes to blind audits without replacing its candidate identities",()=>{
            var bad=JsonNode.Parse("""{"assessment":"supported","coverage":"combined","checks":[{"episode_id":101,"verdict":"supported","content_type":"complete_story","supporting_blocks":[0,1],"contradicting_blocks":[],"reason":"Original teacher events."},{"episode_id":102,"verdict":"supported","content_type":"complete_story","supporting_blocks":[2,3],"contradicting_blocks":[],"reason":"A different film story."}],"note":""}""")!.AsObject();
            var otherEpisode=new Episode(103,2,10,"Tempting adjacent title","Different events.");
            var handler=new Handler([bad,Audit(),Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http).ReevaluateConflictAsync(cues,[..episodes,otherEpisode],"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==3&&result.Array("matches").Single().Int("episode_id")==101&&result["rejected_combination"]!=null);
            Check(handler.Requests.Skip(1).All(r=>!r.ToJsonString().Contains(otherEpisode.Title)&&!r.ToJsonString().Contains("previous response failed validation")));
        });
        test("Forged or repeated proposal IDs cannot enter focused review",()=>{
            var p=Proposal();p["checks"]![1]!["episode_id"]=999;Reject(()=>GroundedVerification.Proposed(p,episodes));p["checks"]![1]!["episode_id"]=101;Reject(()=>GroundedVerification.Proposed(p,episodes));
        });
        test("Guide statements preserve acronyms titles and every original clause",()=>{
            const string description="Log: 12.3\nDr. Rowan boards the U.S.S. Voyager for a rescue. Lt. Hale locates the shuttle. The crew returns safely.";
            var episode=new Episode(301,1,1,"An unrelated rescue",description);var statements=GroundedVerification.Statements(episode);
            Check(statements.Length==3&&statements[0]=="Log: 12.3\nDr. Rowan boards the U.S.S. Voyager for a rescue."&&statements[1]=="Lt. Hale locates the shuttle.");
            Check(string.Join(" ",statements)==description&&episode.Overview==description);
        });
        ContinuousParts(test);
        RecordingCoverageTests.Run(test);
        test("Quote alignment preserves exact words across punctuation and an adjacent subtitle block",()=>{
            var source=DialogueMatcher.Blocks(new Cue[]{new(0,"SO... WHY ARE WE MAKING A FILM? >> WE'RE NOT. I GOTTA TAKE HIM"),new(40,"A FREE PIZZA. This is the continuation."),new(80,"Unrelated later scene with a different story.")});
            var span=GroundedQuote.Resolve(source,0,"I GOTTA TAKE HIM A FREE PIZZA.");Check(span.EndBlock==1&&span.SourceText=="I GOTTA TAKE HIM A FREE PIZZA");
            var punctuation=GroundedQuote.Resolve(source,0,"SO WHY ARE WE MAKING A FILM? WE'RE NOT.");Check(punctuation.SourceText.Contains("SO...")&&punctuation.SourceText.Contains("WE'RE NOT"));
            foreach(var quote in new[]{"WE'RE I GOTTA TAKE HIM", "I GOTTA GIVE HIM A FREE PIZZA.","I TAKE GOTTA HIM A FREE PIZZA.","Unrelated later scene with a different story."})Reject(()=>GroundedQuote.Resolve(source,0,quote));
            Reject(()=>GroundedQuote.Resolve(source,1,"I GOTTA TAKE HIM A FREE PIZZA."));
        });
    }
    static void ContinuousParts(Action<string,Action> test)
    {
        // Synthetic episode descriptions and dialogue: one rescue spanning two complete installments.
        // No real show names, expected benchmark answer or recording-specific exception.
        Cue[] cues=[new(0,"The storm has trapped the miners underground."),new(40,"We found the sealed rescue tunnel. Prepare to enter."),
            new(80,"We entered the tunnel and reached the trapped miners."),new(120,"All the miners are out safely. The rescue is finished.")];
        Episode[] episodes=[new(501,1,4,"The Rescue (1)","A storm traps miners underground. Rescuers locate a sealed tunnel."),
            new(502,1,5,"The Rescue (2)","Rescuers enter the tunnel and reach the miners. They evacuate everyone safely.")];
        var blocks=DialogueMatcher.Blocks(cues);
        JsonObject Audit()=>new(){["candidates"]=new JsonArray(Enumerable.Range(0,2).Select(i=>(JsonNode)new JsonObject {
            ["candidate"]=i,["recording_scope"]="one_of_continuous_parts",["content_type"]="complete_episode",
            ["reason"]="This complete installment develops the same rescue across two distinct guide entries.",
            ["story_blocks"]=new JsonArray(i*2,i*2+1),["statements"]=new JsonArray(Enumerable.Range(0,2).Select(j=>(JsonNode)new JsonObject {
                ["statement"]=j,["verdict"]="supported",["evidence"]=new JsonArray(new JsonObject{["block"]=i*2+j,["quote"]=cues[i*2+j].Text})}).ToArray())}).ToArray())};
        JsonObject Proposal()=>new(){["assessment"]="supported",["coverage"]="combined",["checks"]=new JsonArray(episodes.Select(e=>(JsonNode)new JsonObject{["episode_id"]=e.Id,["verdict"]="supported"}).ToArray())};
        test("A continuous story can cover two complete catalog installments without separate resolutions",()=>{
            var raw=Audit();var snapshot=raw.ToJsonString();var result=GroundedVerification.Expand(raw,blocks,episodes);
            Check(result.Text("coverage")=="combined"&&result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(new[]{501,502}));
            Check(raw.ToJsonString()==snapshot&&result.Array("candidate_checks").All(c=>c.Text("recording_scope")=="one_of_continuous_parts"));
        });
        test("Explicit catalog part structure requires shared title consecutive parts and aligned episode positions",()=>{
            Check(EpisodeCoverage.AreNumberedParts(episodes)&&EpisodeCoverage.AreNumberedParts(episodes.Reverse().ToArray()));
            Check(!EpisodeCoverage.AreNumberedParts([episodes[0]])&&!EpisodeCoverage.AreNumberedParts([episodes[0],episodes[0]]));
            foreach(var other in new[]{new Episode(502,1,5,"Unrelated (2)",""),new(502,1,5,"The Rescue (3)",""),new(502,2,5,"The Rescue (2)",""),new(502,1,8,"The Rescue (2)","")})
                Check(!EpisodeCoverage.AreNumberedParts([episodes[0],other]));
        });
        test("An unresolved related installment cannot be silently dropped in favor of its ending",()=>{
            var raw=Audit();foreach(var c in raw.Array("candidates"))c!["recording_scope"]="entire_episode";
            raw["candidates"]![0]!["statements"]![0]!["evidence"]!.AsArray().Add(new JsonObject{["block"]=1,["quote"]=cues[1].Text});
            raw["candidates"]![0]!["statements"]![1]!["verdict"]="insufficient_evidence";
            raw["candidates"]![0]!["statements"]![1]!["evidence"]=new JsonArray();
            Reject(()=>GroundedVerification.Expand(raw,blocks,episodes));
        });
        test("An incomplete installment is held even when both guide identities are supported",()=>{
            var raw=Audit();raw["candidates"]![1]!["content_type"]="incomplete_episode";raw["candidates"]![1]!["recording_scope"]="incomplete_episode";
            try {GroundedVerification.Expand(raw,blocks,episodes);throw new Exception("Incomplete combined recording accepted.");}
            catch(InvalidDataException ex){Check(ex.Data[AssignmentValidation.ReasonKey]?.ToString()==nameof(AssignmentIssue.IncompleteCombinedStory));}
        });
        test("Promos recaps unknown coverage and missing developments cannot complete a continuous pair",()=>{
            foreach(var type in new[]{"preview_or_promo","recap","unknown"}) {
                var raw=Audit();raw["candidates"]![1]!["content_type"]=type;Reject(()=>GroundedVerification.Expand(raw,blocks,episodes));
            }
            var missing=Audit();missing["candidates"]![1]!["statements"]![1]!["verdict"]="insufficient_evidence";
            missing["candidates"]![1]!["statements"]![1]!["evidence"]=new JsonArray();Reject(()=>GroundedVerification.Expand(missing,blocks,episodes));
        });
        test("Continuous parts still require exact separate evidence and consecutive catalog positions",()=>{
            var overlap=Audit();overlap["candidates"]![1]!["statements"]![0]!["evidence"]![0]!["block"]=1;
            overlap["candidates"]![1]!["statements"]![0]!["evidence"]![0]!["quote"]=cues[1].Text;
            Reject(()=>GroundedVerification.Expand(overlap,blocks,episodes));
            var invented=Audit();invented["candidates"]![1]!["statements"]![1]!["evidence"]![0]!["quote"]="The entirely invented rescue has ended.";
            Reject(()=>GroundedVerification.Expand(invented,blocks,episodes));
            Reject(()=>GroundedVerification.Expand(Audit(),blocks,[episodes[0],new(502,1,8,episodes[1].Title,episodes[1].Overview)]));
        });
        test("A shared handoff block is allowed only with distinct grounded evidence on both sides",()=>{
            Cue[] transition=[..cues.Take(2),new(80,"The search has ended. We can now begin the rescue."),new(120,cues[2].Text),new(160,cues[3].Text)];
            var source=DialogueMatcher.Blocks(transition);var raw=Audit();
            raw["candidates"]![0]!["statements"]![1]!["evidence"]!.AsArray().Add(new JsonObject{["block"]=2,["quote"]=transition[2].Text});
            raw["candidates"]![1]!["statements"]![0]!["evidence"]=new JsonArray(new JsonObject{["block"]=2,["quote"]=transition[2].Text},new JsonObject{["block"]=3,["quote"]=transition[3].Text});
            raw["candidates"]![1]!["statements"]![1]!["evidence"]=new JsonArray(new JsonObject{["block"]=4,["quote"]=transition[4].Text});
            var snapshot=raw.ToJsonString();var result=GroundedVerification.Expand(raw,source,episodes);
            Check(result.Array("matches")[0].Array("evidence").Select(e=>e.Int("index")).SequenceEqual(new[]{0,1}));
            Check(result.Array("matches")[1].Array("evidence").Select(e=>e.Int("index")).SequenceEqual(new[]{3,4})&&raw.ToJsonString()==snapshot);
            Check(result["grounded_audit"]!.ToJsonString()==snapshot);
            var insufficient=raw.DeepClone().AsObject();insufficient["candidates"]![0]!["statements"]![1]!["evidence"]!.AsArray().RemoveAt(0);
            Reject(()=>GroundedVerification.Expand(insufficient,source,episodes));
            var separate=raw.DeepClone().AsObject();foreach(var c in separate.Array("candidates"))c!["recording_scope"]="one_of_separate_episodes";
            Reject(()=>GroundedVerification.Expand(separate,source,episodes));
            Reject(()=>GroundedVerification.Expand(raw,source,[episodes[0],new(502,1,5,"An unrelated rescue",episodes[1].Overview)]));
            var interleaved=raw.DeepClone().AsObject();interleaved["candidates"]![0]!["statements"]![1]!["evidence"]![1]=new JsonObject{["block"]=3,["quote"]=transition[3].Text};
            Reject(()=>GroundedVerification.Expand(interleaved,source,episodes));
        });
        test("Continuous and separate episode scopes must agree within and across independent audits",()=>{
            var mixed=Audit();mixed["candidates"]![1]!["recording_scope"]="one_of_separate_episodes";Reject(()=>GroundedVerification.Expand(mixed,blocks,episodes));
            var separate=Audit();foreach(var c in separate.Array("candidates"))c!["recording_scope"]="one_of_separate_episodes";
            Check(GroundedVerification.Expand(separate,blocks,episodes).Text("coverage")=="combined");
            var handler=new Handler([Audit(),separate]);using var http=new HttpClient(handler);
            Reject(()=>new DialogueMatcher(http).VerifySavedProposalAsync(cues,episodes,Proposal(),"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult());
            Check(handler.Requests.Count==2);
        });
        test("Both independent audits can accept continuous parts using only original dialogue and guides",()=>{
            var handler=new Handler([Audit(),Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http).VerifySavedProposalAsync(cues,episodes,Proposal(),"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==2&&result.Text("coverage")=="combined"&&result.Text("request_revision")==DialogueMatcher.RequestRevision);
            foreach(var request in handler.Requests) {
                var source=JsonNode.Parse(request["messages"]![1].Text("content"))!;
                Check(source.Array("dialogue_blocks").Select(b=>b.Text("text")).SequenceEqual(blocks.Select(b=>b.SourceText)));
                Check(!source.ToJsonString().Contains(episodes[0].Title)&&request.ToJsonString().Contains("continuous_parts"));
                var schema=request["response_format"]!["json_schema"]!["schema"]!;
                Check(schema["properties"]!["recording"]!=null&&schema["properties"]!["candidates"]!["items"]!["properties"]!["recording_scope"]==null);
                Check(source["catalog_relationship"].Text("kind")=="numbered_parts"&&source["catalog_relationship"].Array("members").Select(m=>m.Int("part")).SequenceEqual(new[]{1,2}));
            }
        });
        test("A fully recorded installment of an ongoing story is complete and a cut-off installment remains partial",()=>{
            var single=Audit();single.Array("candidates").RemoveAt(1);single["candidates"]![0]!["recording_scope"]="entire_episode";
            Check(GroundedVerification.Expand(single,blocks.Take(2).ToArray(),[episodes[0]]).Text("coverage")=="complete");
            single["candidates"]![0]!["content_type"]="incomplete_episode";single["candidates"]![0]!["recording_scope"]="incomplete_episode";
            Check(GroundedVerification.Expand(single,blocks.Take(2).ToArray(),[episodes[0]]).Text("coverage")=="partial");
        });
    }
    sealed class Handler(List<JsonObject> replies):HttpMessageHandler {
        public List<JsonNode> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);var reply=replies[Requests.Count-1];if(reply["candidates"]!=null)reply=VerificationTests.RecordingResponse(reply);return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=reply.ToJsonString()}})}.ToJsonString())};}
    }
}
