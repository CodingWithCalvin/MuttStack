using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scout.Core;

public sealed partial class DialogueMatcher
{
    private bool CompactEvidence=>options?.UseEvidenceRetrieval==true;
    public async Task<JsonObject> IdentifyRetrievedAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,string model,string endpoint,CancellationToken ct=default,bool fullSearch=false) {
        ValidateEndpoint(endpoint);
        if(cues.Count==0||catalog.Count==0||catalog.Select(e=>e.Id).Distinct().Count()!=catalog.Count)throw new InvalidDataException("Original dialogue and a distinct episode catalog are required.");
        var blocks=Blocks(cues);var spans=DialogueEvidence.Spans(blocks);var selection=DialogueEvidence.Retrieve(cues,catalog);var searches=new JsonArray();
        async Task<List<Episode>> Search(IReadOnlyList<Episode> episodes,bool entire) {
            var source=EvidenceInput(spans,blocks.Count,entire?null:selection.Blocks);
            source["catalog"]=JsonSerializer.SerializeToNode(episodes.Select(e=>new{id=e.Id,title=e.Title,overview=e.Overview}));
            source["stage_instructions"]=Asset("retrieval-prompt.txt");
            source["task"]=entire?"Broader search: complete original dialogue. Select candidates or abstain.":"Retrieval search: selected original passages and a provisional catalog shortlist. Set search_more if more context or catalog entries are needed.";
            var schema=JsonNode.Parse(Asset("retrieval-schema.json"))!;schema["properties"]!["candidates"]!["items"]!["enum"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id));
            var raw=await transport.CompleteAsync(model,endpoint,Asset("evidence-system.txt"),source,schema,ct,entire?"Searching the full episode guide":"Finding episode candidates");
            if(raw.Count!=2||raw["candidates"] is not JsonArray ids||ids.Count>2||raw["search_more"] is not JsonValue flag||!flag.TryGetValue<bool>(out var more))throw new InvalidDataException("Invalid candidate retrieval response.");
            var selected=new List<Episode>();
            foreach(var node in ids) {
                if(node is not JsonValue value||!value.TryGetValue<int>(out var id)||episodes.SingleOrDefault(e=>e.Id==id) is not Episode episode||selected.Any(e=>e.Id==id))throw new InvalidDataException("Unknown or repeated retrieval candidate.");
                selected.Add(episode);
            }
            searches.Add(new JsonObject{["complete_dialogue"]=entire,["catalog_ids"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id)),["response"]=raw.DeepClone()});
            return more?[]:selected;
        }
        async Task<List<Episode>> Broaden() {
            // Context overflow partitions the CATALOG, never drops original dialogue.
            var found=new List<Episode>();
            async Task Batch(IReadOnlyList<Episode> batch) {
                try {foreach(var e in await Search(batch,true))if(found.All(x=>x.Id!=e.Id))found.Add(e);}
                catch(ModelContextException) when(batch.Count>1){int half=batch.Count/2;await Batch(batch.Take(half).ToArray());await Batch(batch.Skip(half).ToArray());}
            }
            await Batch(catalog);
            return found.Count>2?await Search(found,true):found;
        }
        JsonObject Finish(JsonObject result) {
            result["retrieval"]=new JsonObject{["method"]=DialogueEvidence.Revision,["total_blocks"]=blocks.Count,["retrieved_blocks"]=JsonSerializer.SerializeToNode(selection.Blocks),
                ["shortlist_ids"]=JsonSerializer.SerializeToNode(selection.Episodes.Select(e=>e.Id)),["searches"]=searches.DeepClone(),["full_dialogue_verified"]=result["grounded_verification"].Bool("full_dialogue")};
            result["method"]=Version;result["request_revision"]=RequestRevision;result["model"]=model;result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));return result;
        }
        JsonObject Empty()=>new(){["assessment"]="uncertain",["coverage"]="unknown",["summary"]="No catalog episode was established by the full-dialogue search. Held for review; this does not prove the recording is a feature.",
            ["matches"]=new JsonArray(),["candidate_checks"]=new JsonArray(),["alternatives"]=new JsonArray()};
        var proposed=fullSearch?await Broaden():await Search(selection.Episodes,false);bool broadened=fullSearch;
        if(proposed.Count==0&&!broadened){proposed=await Broaden();broadened=true;}
        if(proposed.Count==0)return Finish(Empty());
        var result=await AuditSpansAsync(cues,proposed,model,endpoint,ct,fullSearch?2:proposed.Count==2?2:1,broadened?null:selection);
        if(result.Text("assessment")!="supported"&&!broadened) {
            var broader=await Broaden();
            if(broader.Count>0&&!broader.Select(e=>e.Id).SequenceEqual(proposed.Select(e=>e.Id))) {
                var previous=result.DeepClone();result=await AuditSpansAsync(cues,broader,model,endpoint,ct,broader.Count==2?2:1);result["prior_retrieval_audit"]=previous;
            }
        }
        return Finish(result);
    }

    // Keep system and original dialogue first and byte-equivalent across stages.
    // Catalogs, candidate identity and answers are never part of the shared prefix.
    private static JsonObject EvidenceInput(IReadOnlyList<DialogueSpan> spans,int total,IReadOnlyList<int>? selected=null) {
        bool full=selected==null||selected.Count==total;
        return new(){["dialogue"]=DialogueEvidence.Text(spans,full?null:selected),["total_blocks"]=total,
            ["included_blocks"]=full?total:selected!.Count,["scope"]=full?"complete_dialogue":"selected_original_passages"};
    }

    private async Task<JsonObject> AuditSpansAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,CancellationToken ct,int passes=2,EvidenceSelection? selected=null) {
        var blocks=Blocks(cues);var spans=DialogueEvidence.Spans(blocks);
        var guides=new JsonObject();
        for(int i=0;i<episodes.Count;i++) {
            var statements=new JsonObject();var text=GroundedVerification.Statements(episodes[i]);for(int j=0;j<text.Length;j++)statements["g"+j]=text[j];
            guides["c"+i]=new JsonObject{["statements"]=statements};
        }
        var audits=new List<JsonObject>();var rawAudits=new JsonArray();
        var relationship=EpisodeCoverage.AreNumberedParts(episodes)?new JsonObject{["kind"]="numbered_parts",["members"]=JsonSerializer.SerializeToNode(episodes.Select((e,i)=>new{candidate="c"+i,part=DescriptionMapping.Part(e.Title)!.Value.Part}))}:new JsonObject{["kind"]="not_established"};
        bool excerpts=options?.AllowExcerptVerification==true&&passes==1&&episodes.Count==1&&DescriptionMapping.Part(episodes[0].Title)==null&&selected!=null&&selected.Blocks.Count<blocks.Count&&selected.Blocks.Contains(0)&&selected.Blocks.Contains(blocks.Count-1);
        string fallback="";
        async Task<JsonObject> Call(bool full,int pass) {
            var included=full?null:selected!.Blocks;var visible=spans.Where(s=>included==null||included.Contains(s.Block)).ToArray();
            var schema=CompactGrounding.Schema(JsonNode.Parse(Asset("span-grounded-schema.json"))!.AsObject(),visible,episodes);
            if(!full) {schema["properties"]!["full_dialogue_required"]=new JsonObject{["type"]="boolean"};schema.Array("required").Add("full_dialogue_required");}
            var source=EvidenceInput(spans,blocks.Count,included);source["candidates"]=guides.DeepClone();source["catalog_relationship"]=relationship.DeepClone();
            source["stage_instructions"]=Asset("span-grounded-prompt.txt")+(!full?"\nThis request contains selected original passages with explicit gaps, NOT the complete dialogue. Return full_dialogue_required=true if missing context is needed for any guide statement, chronology, another story, promo/recap distinction or recording completeness. Missing passages cannot establish that events are absent. Only a supported single complete episode can take the evidence route; all other outcomes receive full dialogue. Never claim to have inspected omitted passages.":"");
            source["task"]=full?(pass==0?"Audit every guide statement and classify the COMPLETE recording. Cite span IDs only.":"Independently audit every guide statement and classify the COMPLETE recording. Cite span IDs only."):"Audit the supplied evidence. Request the complete dialogue if it cannot safely establish one episode and its coverage.";
            var raw=await transport.CompleteAsync(model,endpoint,Asset("evidence-system.txt"),source,schema,ct,$"Verifying guide evidence · pass {pass+1}/{passes}");
            rawAudits.Add(new JsonObject{["scope"]=source["scope"]!.DeepClone(),["response"]=raw.DeepClone()});
            var decision=raw.DeepClone().AsObject();
            if(!full) {
                if(raw.Count!=3||decision["full_dialogue_required"] is not JsonValue flag||!flag.TryGetValue<bool>(out bool required))throw new InvalidDataException("Evidence audit omitted its full-dialogue requirement.");
                if(required)throw new InvalidDataException("Model requested the complete original dialogue.");
                decision.Remove("full_dialogue_required");
            }
            CompactGrounding.ValidateVisibleCitations(decision,visible);
            var result=CompactGrounding.ExpandKeyed(decision,spans,blocks,episodes);
            if(!full&&(result.Text("assessment")!="supported"||result.Text("coverage")!="complete"||result["recording_decision"].Text("kind")!="single_episode"))throw new InvalidDataException("Evidence audit did not establish a complete single episode.");
            if(!full) {
                var support=result.Array("candidate_checks").SelectMany(c=>c.Array("guide_grounding")).SelectMany(f=>f.Array("evidence")).Select(e=>e.Int("block")).Distinct().Order().ToArray();
                // Sparse or concentrated support can describe a recap/promo or just
                // one scene. This is a conservative routing guard, not a confidence score.
                if(support.Length<2||support[^1]-support[0]<Math.Ceiling(blocks.Count*.2)||support[0]>=blocks.Count*.8)
                    throw new InvalidDataException("Supporting excerpts are too concentrated to use the evidence-only route.");
            }
            return result;
        }
        try {
            if(excerpts) {
                try {audits.Add(await Call(false,0));}
                catch(InvalidDataException ex){fallback=ex.Message;excerpts=false;}
                catch(ModelContextException ex){fallback=ex.Message;excerpts=false;}
            }
            if(!excerpts)for(int pass=0;pass<passes;pass++)audits.Add(await Call(true,pass));
            string Decision(JsonObject r)=>r.Text("assessment")+":"+r.Text("coverage")+":"+r["recording_decision"].Text("kind")+":"+string.Join(",",r.Array("matches").Select(m=>m.Int("episode_id")));
            if(audits.Select(Decision).Distinct().Count()!=1)throw new InvalidDataException("Independent compact coverage audits disagree. No assignment accepted.");
            var result=audits[^1];result["grounded_verification"]=new JsonObject{["method"]="compact-original-span-audits-v5",["passes"]=audits.Count,["full_dialogue"]=!excerpts,
                ["included_blocks"]=excerpts?selected!.Blocks.Count:blocks.Count,["total_blocks"]=blocks.Count,["fallback_reason"]=fallback,["candidate_ids"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id)),["raw_audits"]=rawAudits.DeepClone()};
            result["method"]=Version;result["model"]=model;result["request_revision"]=RequestRevision;result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));return result;
        }catch(Exception ex){ex.Data["model_attempts"]=rawAudits.ToJsonString();ex.Data["model_candidate_ids"]=JsonSerializer.Serialize(episodes.Select(e=>e.Id));throw;}
    }
}
