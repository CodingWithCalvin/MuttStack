using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Scout.Core;

static class RetrievalTests
{
    public static void Run(Action<string,Action> test) {
        var episodes=new Episode[]{new(101,1,1,"The Rescue (1)","The crew rescues the captain. The crew repairs the shuttle."),new(102,1,2,"The Rescue (2)","The team returns to the station. The team celebrates their arrival.")};
        Cue[] cues=[new(0,"The crew rescues the captain from the cave."),new(40,"The crew repairs the shuttle and prepares to leave."),new(80,"The team returns to the station with everyone aboard."),new(120,"The team celebrates their arrival with a big party.")];
        JsonObject Proposal(int id=101)=>new(){["candidates"]=new JsonArray(id),["search_more"]=false};
        JsonObject Audit(bool combined=false)=>new(){["candidates"]=new JsonArray(Enumerable.Range(0,combined?2:1).Select(i=>(JsonNode)new JsonObject{
            ["candidate"]=i,["role"]="main_story",["statements"]=new JsonArray(Enumerable.Range(0,2).Select(s=>(JsonNode)new JsonObject{
                ["statement"]=s,["verdict"]="supported",["evidence"]=new JsonArray("s"+(i*2+s))}).ToArray())}).ToArray()),
            ["recording"]=new JsonObject{["kind"]=combined?"continuous_parts":"single_episode",["candidates"]=combined?new JsonArray(0,1):new JsonArray(0)}};
        test("Evidence spans retain every original word and deterministic source coordinates",()=>{
            var longCues=new[]{new Cue(0,string.Join(" ",Enumerable.Range(0,90).Select(i=>"originalword"+i))),new Cue(40,"No. That never happened here.")};
            var blocks=DialogueMatcher.Blocks(longCues);var spans=DialogueEvidence.Spans(blocks);
            Check(string.Join(" ",spans.Select(s=>s.Text))==string.Join(" ",blocks.Select(b=>b.SourceText)));
            Check(spans.All(s=>s.Text.Length<=170)&&spans.Select(s=>s.Id).SequenceEqual(Enumerable.Range(0,spans.Count)));
            foreach(var span in spans)if(Regex.Matches(span.Text,@"\w+").Count>=3&&span.Text.Length>=12)Check(GroundedQuote.Resolve(blocks,span.Block,span.Text).Block==span.Block);
            var text=DialogueEvidence.Text(spans);Check(text.Contains("[s0]")&&!Regex.IsMatch(text,@"block \d+:"));
            var longWord=new string('x',200)+" remains intact in the original dialogue.";var longSpans=DialogueEvidence.Spans(DialogueMatcher.Blocks(new[]{new Cue(0,longWord)}));
            Check(string.Join(" ",longSpans.Select(s=>s.Text))==longWord);
        });
        test("Short source pointers retrieve only adjacent original context within the same block",()=>{
            var text=string.Join(" ",Enumerable.Repeat("original",18))+" AND JADE.";
            var blocks=DialogueMatcher.Blocks(new[]{new Cue(0,text)});var spans=DialogueEvidence.Spans(blocks);var tail=spans.Last();
            var quote=DialogueEvidence.Citation(spans,tail.Id,blocks);Reject(()=>GroundedQuote.Resolve(blocks,tail.Block,tail.Text));Check(quote!=tail.Text&&quote.EndsWith(tail.Text)&&quote.Length<=180&&GroundedQuote.Resolve(blocks,tail.Block,quote).Block==tail.Block);
            var shortBlocks=DialogueMatcher.Blocks(new[]{new Cue(0,"A meaningful previous original block."),new Cue(40,"YES.")});var shortSpans=DialogueEvidence.Spans(shortBlocks);
            Reject(()=>DialogueEvidence.Citation(shortSpans,shortSpans.Count-1,shortBlocks));
            var simple=DialogueEvidence.Spans(DialogueMatcher.Blocks(cues));Check(DialogueEvidence.Citation(simple,0,DialogueMatcher.Blocks(cues))==simple[0].Text);
        });
        test("Context retrieval preserves model verdicts selected IDs and recording chronology",()=>{
            var dialogue=new[]{new Cue(0,string.Join(" ",Enumerable.Repeat("context",18))+" The crew rescues the captain."),new Cue(40,"The crew repairs the shuttle and prepares to leave.")};
            var blocks=DialogueMatcher.Blocks(dialogue);var spans=DialogueEvidence.Spans(blocks);Check(spans.Count==3&&spans[1].Text.Length<12);
            var raw=Audit();raw["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s1");raw["candidates"]![0]!["statements"]![1]!["evidence"]=new JsonArray("s2");raw=Keyed(raw);var before=raw.ToJsonString();
            var result=CompactGrounding.ExpandKeyed(raw,spans,blocks,episodes.Take(1).ToArray());
            Check(result.Array("matches").Single().Int("episode_id")==101&&raw.ToJsonString()==before&&JsonNode.DeepEquals(result["compact_grounded_decision"],raw));
            var context=result.Array("citation_context").Single()!;Check(context.Text("span_id")=="s1"&&context.Int("block")==0&&context.Text("original_context").EndsWith(context.Text("selected_text")));
        });
        test("Retrieval is bounded includes temporal anchors and considers late episode candidates",()=>{
            var dialogue=Enumerable.Range(0,120).Select(i=>new Cue(i*40,i<30?"The crew rescues the captain and repairs the shuttle.":i>=90?"The team returns to the station and celebrates their arrival.":"Unrelated neutral conversation about daily life.")).ToArray();
            var selection=DialogueEvidence.Retrieve(dialogue,episodes);Check(selection.Episodes.Select(e=>e.Id).ToHashSet().SetEquals([101,102]));
            Check(selection.Blocks.Count<=40&&selection.Blocks.Contains(0)&&selection.Blocks.Contains(119)&&selection.Blocks.SequenceEqual(selection.Blocks.Order()));
        });
        test("Compact grounding expands original evidence without model-written quotations",()=>{
            var blocks=DialogueMatcher.Blocks(cues);var spans=DialogueEvidence.Spans(blocks);var raw=Audit(true);var before=raw.ToJsonString();
            var result=CompactGrounding.Expand(raw,spans,blocks,episodes);
            Check(result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(new[]{101,102}));
            Check(result.Array("candidate_checks")[0]!.Array("guide_grounding")[0]!.Array("evidence")[0].Text("source_quote").Contains("rescues the captain"));
            Check(raw.ToJsonString()==before&&JsonNode.DeepEquals(result["compact_grounded_decision"],raw));
        });
        test("Compact grounding rejects forged repeated missing and promotional evidence",()=>{
            var blocks=DialogueMatcher.Blocks(cues);var spans=DialogueEvidence.Spans(blocks);
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s999"),
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray(0),
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s00"),
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s0","s0"),
                r=>r["candidates"]![0]!["statements"]!.AsArray().RemoveAt(1),
                r=>r["candidates"]![0]!["statements"]![1]!["statement"]=0,
                r=>r["candidates"]![0]!["role"]="preview_or_promo",
                r=>r["recording"]!["candidates"]=new JsonArray(0,1),
                r=>r["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("invented quote")
            }) {var raw=Audit();change(raw);Reject(()=>CompactGrounding.Expand(raw,spans,blocks,episodes.Take(1).ToArray()));}
        });
        test("Compact completeness has one explicit authority and never repairs old contradictory output",()=>{
            var blocks=DialogueMatcher.Blocks(cues);var spans=DialogueEvidence.Spans(blocks);
            var partial=Audit();partial["recording"]!["kind"]="incomplete_episode";
            var result=CompactGrounding.Expand(partial,spans,blocks,episodes.Take(1).ToArray());
            Check(result.Text("coverage")=="partial");
            var old=Audit(true);foreach(var c in old.Array("candidates")){c!.AsObject().Remove("role");c["content_type"]="incomplete_episode";}
            Reject(()=>CompactGrounding.Expand(old,spans,blocks,episodes));
            var omitted=Audit(true);omitted["recording"]!["kind"]="single_episode";omitted["recording"]!["candidates"]=new JsonArray(0);
            Reject(()=>CompactGrounding.Expand(omitted,spans,blocks,episodes));
            var unresolved=Audit(true);unresolved["recording"]!["kind"]="unresolved";unresolved["recording"]!["candidates"]=new JsonArray();
            Check(CompactGrounding.Expand(unresolved,spans,blocks,episodes).Array("matches").Count==0);
        });
        test("Fixed response slots require every candidate and guide key without repairing repeated rows",()=>{
            var blocks=DialogueMatcher.Blocks(cues);var spans=DialogueEvidence.Spans(blocks);var raw=Keyed(Audit(true));var before=raw.ToJsonString();
            var result=CompactGrounding.ExpandKeyed(raw,spans,blocks,episodes);
            Check(result.Array("matches").Count==2&&raw.ToJsonString()==before&&JsonNode.DeepEquals(result["compact_grounded_decision"],raw));
            Reject(()=>CompactGrounding.ExpandKeyed(Audit(true),spans,blocks,episodes));
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                r=>r["candidates"]!["c0"]!["statements"]!.AsObject().Remove("g1"),
                r=>r["candidates"]!["c0"]!["statements"]!["g2"]=r["candidates"]!["c0"]!["statements"]!["g1"]!.DeepClone(),
                r=>r["candidates"]!["c2"]=r["candidates"]!["c0"]!.DeepClone(),
                r=>r["recording"]!["candidates"]=new JsonArray("c00","c1"),
                r=>r["recording"]!["candidates"]=new JsonArray("c0","c0")
            }) {var invalid=raw.DeepClone().AsObject();change(invalid);Reject(()=>CompactGrounding.ExpandKeyed(invalid,spans,blocks,episodes));}
            using var resource=typeof(DialogueMatcher).Assembly.GetManifestResourceStream("Scout.Core.Assets.span-grounded-schema.json")!;
            using var reader=new StreamReader(resource);var schema=CompactGrounding.Schema(JsonNode.Parse(reader.ReadToEnd())!.AsObject(),spans,episodes);
            var keys=schema["properties"]!["candidates"]!;
            Check(keys.Text("type")=="object"&&keys.Array("required").Select(n=>n!.GetValue<string>()).SequenceEqual(new[]{"c0","c1"}));
            var facts=keys["properties"]!["c0"]!["properties"]!["statements"]!;
            Check(facts.Array("required").Select(n=>n!.GetValue<string>()).SequenceEqual(new[]{"g0","g1"})&&!facts.Bool("additionalProperties"));
            var coverage=schema["properties"]!["recording"]!.Array("anyOf");
            Check(coverage.Count==3);
            foreach(var variant in coverage) {
                var properties=variant!["properties"]!;var kinds=properties["kind"]!.Array("enum").Select(n=>n!.GetValue<string>()).ToArray();
                int count=kinds.Contains("single_episode")?1:kinds.Contains("unresolved")?0:2;
                Check(properties["candidates"].Int("minItems")==count&&properties["candidates"].Int("maxItems")==count);
            }
            var single=CompactGrounding.Schema(JsonNode.Parse(readerResult())!.AsObject(),spans,episodes.Take(1).ToArray());
            Check(single["properties"]!["recording"]!.Array("anyOf").Count==2);
            string readerResult(){using var r=typeof(DialogueMatcher).Assembly.GetManifestResourceStream("Scout.Core.Assets.span-grounded-schema.json")!;using var t=new StreamReader(r);return t.ReadToEnd();}
        });
        test("Numbered part proposals always receive complete original dialogue before assignment",()=>{
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Unrelated contextual material preserved at original position number "+i))).ToArray();
            using var handler=new Handler([Proposal(),Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(dialogue,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches").Single().Int("episode_id")==101&&handler.Requests.Count==2&&result["retrieval"].Bool("full_dialogue_verified"));
            var search=Source(handler.Requests[0]);var audit=Source(handler.Requests[1]);Check(search.Int("included_blocks")<search.Int("total_blocks"));
            Check(audit.Text("dialogue").Contains("original position number 99")&&!audit.ToJsonString().Contains("The Rescue"));
            Check(handler.Requests.All(r=>!r["chat_template_kwargs"].Bool("enable_thinking")));
        });
        test("Default retrieval verifies full dialogue without a redundant excerpt audit",()=>{
            var ordinary=new[]{episodes[0] with {Title="Rescue"}};
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Additional original conversation at position number "+i))).ToArray();
            using var handler=new Handler([Proposal(),Audit()]);using var http=new HttpClient(handler);
            var options=new AppSettings().Inference();Check(!options.AllowExcerptVerification);
            var matcher=new DialogueMatcher(http,options);
            var result=matcher.IdentifyAsync(dialogue,ordinary,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==2&&result.Text("assessment")=="supported"&&result["grounded_verification"].Bool("full_dialogue"));
            Check(Source(handler.Requests[0]).Int("included_blocks")<Source(handler.Requests[0]).Int("total_blocks"));
            Check(Source(handler.Requests[1]).Text("scope")=="complete_dialogue"&&Source(handler.Requests[1]).Text("dialogue").Contains("position number 99"));
            Check(handler.Requests[1]["response_format"]!["json_schema"]!["schema"]!["properties"]?["full_dialogue_required"]==null);
            Check(matcher.CacheScope!=new DialogueMatcher(http,new AppSettings().Inference(allowExcerptVerification:true)).CacheScope);
        });
        test("Routine evidence audit shares the dialogue prefix and accurately reports omitted passages",()=>{
            var ordinary=new[]{episodes[0] with {Title="Rescue"}};
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Unrelated contextual material preserved at original position number "+i))).ToArray();
            dialogue[51]=dialogue[51] with {Text=cues[1].Text};
            var reply=Audit();reply["full_dialogue_required"]=false;
            var later=DialogueEvidence.Spans(DialogueMatcher.Blocks(dialogue)).First(s=>s.Block==51);
            reply["candidates"]![0]!["statements"]![1]!["evidence"]=new JsonArray("s"+later.Id);
            using var handler=new Handler([Proposal(),reply]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(dialogue,ordinary,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Text("assessment")=="supported"&&!result["retrieval"].Bool("full_dialogue_verified")&&handler.Requests.Count==2);
            var search=Source(handler.Requests[0]);var audit=Source(handler.Requests[1]);
            Check(search.Text("dialogue")==audit.Text("dialogue")&&audit.Text("dialogue").Contains("omitted original dialogue")&&audit.Int("included_blocks")<audit.Int("total_blocks"));
            Check(handler.Requests[0]["messages"]![0].Text("content")==handler.Requests[1]["messages"]![0].Text("content"));
            Check(!audit.ToJsonString().Contains("Rescue")&&!audit.AsObject().ContainsKey("catalog")&&handler.Requests.All(r=>r.Array("messages").Count==2));
        });
        test("Concentrated support falls back even when the model requests no additional context",()=>{
            var ordinary=new[]{episodes[0] with {Title="Rescue"}};
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Additional original conversation at position number "+i))).ToArray();
            var excerpt=Audit();excerpt["full_dialogue_required"]=false;
            using var handler=new Handler([Proposal(),excerpt,Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(dialogue,ordinary,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==3&&result["grounded_verification"].Bool("full_dialogue")&&result["grounded_verification"].Text("fallback_reason").Contains("concentrated"));
        });
        test("Excerpt uncertainty and unseen citations escalate without changing saved model responses",()=>{
            var ordinary=new[]{episodes[0] with {Title="Rescue"}};
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Unrelated contextual material preserved at original position number "+i))).ToArray();
            var blocks=DialogueMatcher.Blocks(dialogue);var spans=DialogueEvidence.Spans(blocks);var selected=DialogueEvidence.Retrieve(dialogue,ordinary);
            var unseen=spans.First(s=>!selected.Blocks.Contains(s.Block));
            foreach(bool requestFull in new[]{true,false}) {
                var first=Audit();first["full_dialogue_required"]=requestFull;
                if(!requestFull)first["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s"+unseen.Id);
                var before=first.ToJsonString();using var handler=new Handler([Proposal(),first,Audit()]);using var http=new HttpClient(handler);
                var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(dialogue,ordinary,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
                Check(result.Text("assessment")=="supported"&&result["retrieval"].Bool("full_dialogue_verified")&&handler.Requests.Count==3&&first.ToJsonString()==before);
                Check(result["grounded_verification"]!.Array("raw_audits").Count==2&&result["grounded_verification"].Text("fallback_reason")!="");
                Check(Source(handler.Requests[2]).Text("dialogue")==DialogueEvidence.Text(spans));
            }
        });
        test("Shared full-dialogue prefix excludes catalog identity and prior answers",()=>{
            var more=Proposal();more["search_more"]=true;using var handler=new Handler([more,Proposal(),Audit()]);using var http=new HttpClient(handler);
            new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            var search=handler.Requests[1];var audit=handler.Requests[2];
            string Prefix(JsonObject r){var text=r["messages"]![1].Text("content");int end=text.IndexOf(",\"catalog\":",StringComparison.Ordinal);if(end<0)end=text.IndexOf(",\"candidates\":",StringComparison.Ordinal);return text[..end];}
            Check(Prefix(search)==Prefix(audit)&&search["messages"]![0].Text("content")==audit["messages"]![0].Text("content"));
            Check(!Source(audit).AsObject().ContainsKey("catalog")&&!Source(audit).ToJsonString().Contains("The Rescue"));
        });
        test("Evidence-only promo partial and contradictory outcomes cannot bypass full verification",()=>{
            var ordinary=new[]{episodes[0] with {Title="Rescue"}};
            var dialogue=cues.Take(2).Concat(Enumerable.Range(0,100).Select(i=>new Cue(100+i*40,"Additional original conversation at position number "+i))).ToArray();
            foreach(Action<JsonObject> change in new Action<JsonObject>[] {
                r=>r["candidates"]![0]!["role"]="preview_or_promo",
                r=>r["recording"]!["kind"]="incomplete_episode",
                r=>r["candidates"]![0]!["statements"]![1]!["verdict"]="contradicted"
            }) {
                var excerpt=Audit();excerpt["full_dialogue_required"]=false;change(excerpt);
                using var handler=new Handler([Proposal(),excerpt,Audit()]);using var http=new HttpClient(handler);
                var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(dialogue,ordinary,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
                Check(handler.Requests.Count==3&&result["grounded_verification"].Bool("full_dialogue")&&result["grounded_verification"].Text("fallback_reason")!="");
                Check(JsonNode.DeepEquals(result["grounded_verification"]!.Array("raw_audits")[0]!["response"],Keyed(excerpt)));
            }
        });
        test("Missing descriptions outside the proposal do not prevent evidence retrieval",()=>{
            var mixed=episodes.Append(new Episode(999,3,1,"Unrelated","")).ToArray();
            var selection=DialogueEvidence.Retrieve(cues,mixed);Check(selection.Blocks.Count>0);
            using var handler=new Handler([Proposal(),Audit()]);using var http=new HttpClient(handler);
            Check(new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,mixed,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult().Text("assessment")=="supported");
        });
        test("Inconclusive retrieval broadens to the entire catalog and full dialogue automatically",()=>{
            var empty=new JsonObject{["candidates"]=new JsonArray(),["search_more"]=true};using var handler=new Handler([empty,Proposal(),Audit()]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==3&&Source(handler.Requests[1]).Int("included_blocks")==4&&result["retrieval"]!.Array("searches").Count==2);
        });
        test("Broader search does not turn missing evidence into a non-episode claim",()=>{
            using var handler=new Handler([new(){["candidates"]=new JsonArray(),["search_more"]=true},new(){["candidates"]=new JsonArray(),["search_more"]=false}]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Text("assessment")=="uncertain"&&result.Array("matches").Count==0&&!result["retrieval"].Bool("full_dialogue_verified"));
        });
        test("Combined retrieval retains two independent full-dialogue audits and exact prompt prefix",()=>{
            var proposal=new JsonObject{["candidates"]=new JsonArray(101,102),["search_more"]=false};using var handler=new Handler([proposal,Audit(true),Audit(true)]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches").Count==2&&handler.Requests.Count==3);var a=Source(handler.Requests[1]);var b=Source(handler.Requests[2]);a.AsObject().Remove("task");b.AsObject().Remove("task");Check(JsonNode.DeepEquals(a,b));
        });
        test("Disagreeing compact coverage audits remain held without prediction repair",()=>{
            var disagree=Audit(true);disagree["recording"]!["kind"]="unresolved";disagree["recording"]!["candidates"]=new JsonArray();using var handler=new Handler([Audit(true),disagree]);using var http=new HttpClient(handler);
            var proposal=new JsonObject{["assessment"]="supported",["matches"]=new JsonArray(new JsonObject{["episode_id"]=101},new JsonObject{["episode_id"]=102})};
            Reject(()=>new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).VerifySavedProposalAsync(cues,episodes,proposal,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult());Check(handler.Requests.Count==2);
        });
        test("Application settings enable compact retrieval and isolate its cache from legacy requests",()=>{
            Check(new AppSettings().Inference().UseEvidenceRetrieval);using var http=new HttpClient();Check(new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).CacheScope!=new DialogueMatcher(http).CacheScope);
        });
        test("Unsupported retrieved candidate triggers a fresh full-catalog search without rewriting its audit",()=>{
            var held=Audit();held["recording"]!["kind"]="unresolved";held["recording"]!["candidates"]=new JsonArray();held["candidates"]![0]!["role"]="unknown";
            foreach(var fact in held["candidates"]![0]!.Array("statements")){fact!["verdict"]="insufficient_evidence";fact["evidence"]=new JsonArray();}
            var corrected=Audit();corrected["candidates"]![0]!["statements"]![0]!["evidence"]=new JsonArray("s2");corrected["candidates"]![0]!["statements"]![1]!["evidence"]=new JsonArray("s3");
            using var handler=new Handler([Proposal(),held,Proposal(102),corrected]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(handler.Requests.Count==4&&result.Array("matches").Single().Int("episode_id")==102&&result["prior_retrieval_audit"].Text("assessment")=="uncertain");
            Check(JsonNode.DeepEquals(result["prior_retrieval_audit"]!["compact_grounded_decision"],Keyed(held)));
        });
        test("Context overflow searches every catalog partition without truncating original dialogue",()=>{
            var empty=new JsonObject{["candidates"]=new JsonArray(),["search_more"]=true};using var handler=new Handler([empty,new(),Proposal(),Proposal(102),Audit(true),Audit(true)],[2]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches").Count==2&&handler.Requests.Count==6);var all=Source(handler.Requests[1]).Text("dialogue");
            Check(Source(handler.Requests[2]).Text("dialogue")==all&&Source(handler.Requests[3]).Text("dialogue")==all);
            Check(Source(handler.Requests[2]).Array("catalog").Single().Int("id")==101&&Source(handler.Requests[3]).Array("catalog").Single().Int("id")==102);
        });
        test("A request for more context after full search cannot become an assignment",()=>{
            var more=Proposal();more["search_more"]=true;using var handler=new Handler([more,more]);using var http=new HttpClient(handler);
            var result=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true}).IdentifyAsync(cues,episodes,"m","http://127.0.0.1:1234/v1/chat/completions").GetAwaiter().GetResult();
            Check(result.Array("matches").Count==0&&handler.Requests.Count==2);
        });
    }
    static JsonObject Keyed(JsonObject raw) {
        if(raw["recording"] is not JsonObject recording)return raw.DeepClone().AsObject();
        var candidates=new JsonObject();foreach(var row in raw.Array("candidates")) {
            var facts=new JsonObject();foreach(var fact in row!.Array("statements"))facts["g"+fact.Int("statement")]=new JsonObject{["verdict"]=fact!["verdict"]!.DeepClone(),["evidence"]=fact["evidence"]!.DeepClone()};
            candidates["c"+row.Int("candidate")]=new JsonObject{["statements"]=facts,["role"]=row!["role"]!.DeepClone()};
        }
        var result=new JsonObject{["candidates"]=candidates,["recording"]=new JsonObject{["kind"]=recording["kind"]!.DeepClone(),
            ["candidates"]=new JsonArray(recording.Array("candidates").Select(n=>(JsonNode?)JsonValue.Create("c"+n!.GetValue<int>())).ToArray())}};
        if(raw.ContainsKey("full_dialogue_required"))result["full_dialogue_required"]=raw["full_dialogue_required"]!.DeepClone();
        return result;
    }
    static JsonNode Source(JsonObject request)=>JsonNode.Parse(request["messages"]![1].Text("content"))!;
    static void Check(bool value){if(!value)throw new Exception("Retrieval assertion failed.");}
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Expected invalid compact decision to be held.");}
    sealed class Handler(List<JsonObject> replies,HashSet<int>? contextFailures=null):HttpMessageHandler {
        public List<JsonObject> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            if(contextFailures?.Contains(Requests.Count)==true)return new(HttpStatusCode.BadRequest){Content=new StringContent("""{"error":{"type":"exceed_context_size_error"}}""")};
            return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=Keyed(replies[Requests.Count-1]).ToJsonString()}})}.ToJsonString())};
        }
    }
}
