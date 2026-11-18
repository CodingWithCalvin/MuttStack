using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;

static class RecordingCoverageTests
{
    static void Check(bool value){if(!value)throw new Exception("Joint coverage assertion failed.");}
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Contradictory recording coverage accepted.");}
    public static void Run(Action<string,Action> test)
    {
        Cue[] cues=[new(0,"The storm has trapped the miners underground."),new(40,"We found the tunnel. Prepare the rescue."),new(80,"We reached the miners beyond the tunnel."),new(120,"Everyone is out safely. The rescue is finished.")];
        Episode[] episodes=[new(401,2,5,"Rescue (1)","A storm traps miners. Rescuers find a tunnel."),new(402,2,6,"Rescue (2)","Rescuers reach the miners. Everyone is evacuated safely.")];
        var blocks=DialogueMatcher.Blocks(cues);
        JsonObject Decision(string kind,params int[] members)=>new(){
            ["candidates"]=new JsonArray(Enumerable.Range(0,2).Select(i=>(JsonNode)new JsonObject{
                ["candidate"]=i,["content_type"]="complete_episode",["reason"]="The guide's distinct rescue events appear in the original dialogue.",
                ["story_blocks"]=new JsonArray(i*2,i*2+1),["statements"]=new JsonArray(Enumerable.Range(0,2).Select(j=>(JsonNode)new JsonObject{
                    ["statement"]=j,["verdict"]="supported",["evidence"]=new JsonArray(new JsonObject{["block"]=i*2+j,["quote"]=cues[i*2+j].Text})}).ToArray())}).ToArray()),
            ["recording"]=new JsonObject{["kind"]=kind,["candidates"]=new JsonArray(members.Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray()),["reason"]="The first installment establishes the rescue and the second completes it in successive dialogue."}};
        test("One recording decision represents continuous parts and preserves the raw model evidence",()=>{
            var raw=Decision("continuous_parts",0,1);var snapshot=raw.ToJsonString();var result=GroundedRecording.Expand(raw,blocks,episodes);
            Check(result.Text("coverage")=="combined"&&result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(new[]{401,402}));
            Check(result["grounded_audit"]!.ToJsonString()==snapshot&&raw.ToJsonString()==snapshot);
            Check(result.Array("candidate_checks").All(c=>c.Text("recording_scope")=="one_of_continuous_parts"&&c.Text("recording_scope_source")=="recording_decision"));
        });
        test("Two individual whole-recording claims cannot enter the new coverage protocol",()=>{
            var raw=Decision("continuous_parts",0,1);foreach(var c in raw.Array("candidates"))c!["recording_scope"]="entire_episode";
            Reject(()=>GroundedRecording.Expand(raw,blocks,episodes));raw.Remove("recording");Reject(()=>GroundedRecording.Expand(raw,blocks,episodes));
            Reject(()=>GroundedRecording.Expand(Decision("single_episode",0,1),blocks,episodes));
        });
        test("A single assignment cannot silently omit another fully supported episode",()=>{
            Reject(()=>GroundedRecording.Expand(Decision("single_episode",1),blocks,episodes));
        });
        test("Recording coverage can explicitly abstain despite plausible complete guide matches",()=>{
            var result=GroundedRecording.Expand(Decision("unresolved"),blocks,episodes);
            Check(result.Text("assessment")=="uncertain"&&result.Array("matches").Count==0&&result.Text("summary_source")=="recording_decision");
        });
        test("Combined recording decisions cannot select promos recaps fragments or unsupported guides",()=>{
            foreach(var type in new[]{"preview_or_promo","recap","incomplete_episode","unknown"}) {var raw=Decision("continuous_parts",0,1);raw["candidates"]![1]!["content_type"]=type;Reject(()=>GroundedRecording.Expand(raw,blocks,episodes));}
            var missing=Decision("continuous_parts",0,1);missing["candidates"]![1]!["statements"]![1]!["verdict"]="insufficient_evidence";missing["candidates"]![1]!["statements"]![1]!["evidence"]=new JsonArray();
            Reject(()=>GroundedRecording.Expand(missing,blocks,episodes));
        });
        test("A rejected promo does not force an additional episode into a recording decision",()=>{
            var raw=Decision("single_episode",0);raw["candidates"]![1]!["content_type"]="preview_or_promo";
            var result=GroundedRecording.Expand(raw,blocks,episodes);Check(result.Text("coverage")=="complete"&&result.Array("matches").Single().Int("episode_id")==401);
        });
        test("Recording selection requires known unique candidates in grounded playback order",()=>{
            foreach(var pair in new[]{new[]{0,0},new[]{0,2},new[]{1,0}})Reject(()=>GroundedRecording.Expand(Decision("continuous_parts",pair),blocks,episodes));
            Reject(()=>GroundedRecording.Expand(Decision("continuous_parts",0,1),blocks,[episodes[0],new(402,2,9,episodes[1].Title,episodes[1].Overview)]));
        });
        test("A joint recording decision does not bypass quote or evidence overlap checks",()=>{
            var forged=Decision("continuous_parts",0,1);forged["candidates"]![1]!["statements"]![0]!["evidence"]![0]!["quote"]="An invented rescue statement never spoken.";
            Reject(()=>GroundedRecording.Expand(forged,blocks,episodes));
            var overlap=Decision("separate_episodes",0,1);overlap["candidates"]![1]!["statements"]![0]!["evidence"]![0]=new JsonObject{["block"]=1,["quote"]=cues[1].Text};
            Reject(()=>GroundedRecording.Expand(overlap,blocks,episodes));
        });
        test("Separate complete episodes and incomplete single recordings retain distinct coverage",()=>{
            Check(GroundedRecording.Expand(Decision("separate_episodes",0,1),blocks,episodes).Text("coverage")=="combined");
            var partial=Decision("incomplete_episode",0);partial.Array("candidates").RemoveAt(1);partial["candidates"]![0]!["content_type"]="incomplete_episode";
            Check(GroundedRecording.Expand(partial,blocks,[episodes[0]]).Text("coverage")=="partial");
        });
        test("A contradictory joint model response is held unchanged without a repair retry",()=>{
            var raw=Decision("single_episode",0,1);var snapshot=raw.ToJsonString();var handler=new Handler(raw);using var http=new HttpClient(handler);
            var proposal=new JsonObject{["assessment"]="supported",["checks"]=new JsonArray(episodes.Select(e=>(JsonNode)new JsonObject{["episode_id"]=e.Id,["verdict"]="supported"}).ToArray())};
            try {new DialogueMatcher(http).VerifySavedProposalAsync(cues,episodes,proposal,"model","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();throw new Exception("Contradictory joint response accepted.");}
            catch(InvalidDataException ex){Check(AssignmentValidation.IsTerminal(ex));var attempts=JsonNode.Parse((string)ex.Data["model_attempts"]!)!.AsArray();Check(attempts.Count==1&&attempts[0]!["response"]!.ToJsonString()==snapshot);}
            Check(handler.Count==1&&raw.ToJsonString()==snapshot);
        });
    }
    sealed class Handler(JsonObject response):HttpMessageHandler {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Count++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{
                ["finish_reason"]="stop",["message"]=new JsonObject{["content"]=response.ToJsonString()}})}.ToJsonString())});
        }
    }
}
