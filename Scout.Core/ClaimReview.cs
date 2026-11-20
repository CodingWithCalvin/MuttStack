using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

/// <summary>Audits disputed claims against one guide card at a time, using only original dialogue.</summary>
public static class ClaimReview
{
    public const string Version="individual-claim-v1";
    private const string Instruction="Audit ONLY the supplied episode against the original dialogue. Other recordings, filenames and previous suggestions are not evidence. Identify the defining task, relationship, action and consequence in the guide, and check that those same events occur in the cited dialogue. Shared characters, a class, a performance or a date are not enough. A later scene or subplot continuing the same story is not evidence of a second episode. Do not substitute different events for the supplied guide. Return one check for the supplied episode, using insufficient_evidence or contradicted when its defining events are absent or different. If supported, report complete or partial coverage of that episode. Do not infer a missing half from another recording.";

    public static async Task RunAsync(JsonObject report,IReadOnlyList<Episode> catalog,DialogueMatcher matcher,string model,string endpoint,
        Action checkpoint,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null,IReadOnlySet<string>? allowedPaths=null,bool checkSources=true)
    {
        if(allowedPaths!=null)allowedPaths=IssueReview.Expand(report,allowedPaths);
        var rows=CollectionResolver.Resolve(report).Where(g=>g.Compilation==null&&g.HumanDecision==null&&g.Status is not ("source_unavailable" or "overridden_conflict")&&
            (g.Status=="unresolved_conflict"||g.Rows.Any(r=>r.Bool("claim_review_pending")||r.Text("claim_review_error")!="")))
            .SelectMany(g=>g.Rows).Where(r=>allowedPaths==null||allowedPaths.Contains(Path.GetFullPath(r.Text("path")))).ToList();
        if(rows.Count==0)return;
        foreach(var row in rows)row["claim_review_pending"]=true;
        checkpoint();
        foreach(var row in rows) {
            pause?.Checkpoint();ct.ThrowIfCancellationRequested();
            try {
                VerifySource(row);
                var cues=MultipartEvidence.Cues(row);var blocks=DialogueMatcher.Blocks(cues);
                // Resume the original set of claims even if an earlier checkpoint replaced the candidates.
                var prior=row["claim_resolution"] as JsonObject;
                var candidates=prior?["previous_candidates"] as JsonArray??row.Array("candidates");
                var ids=candidates.Select(c=>c!["episode"].Int("id")).Distinct().ToList();
                if(ids.Count is <1 or >2)throw new InvalidDataException("No valid disputed episode claims to audit.");
                var episodes=ids.Select(id=>catalog.Single(e=>e.Id==id)).ToList();
                var signature=CollectionResolver.Hash(JsonSerializer.Serialize(new{Version,DialogueMatcher.RequestRevision,cues,episodes,model,endpoint,scope=matcher.CacheScope}));
                if(prior?.Text("signature")!=signature) {
                    prior=new JsonObject{["signature"]=signature,["previous_reasoning"]=row["reasoning"]?.DeepClone(),
                        ["previous_candidates"]=candidates.DeepClone(),["checks"]=new JsonArray()};
                    row["claim_resolution"]=prior;checkpoint();
                }
                foreach(var episode in episodes) {
                    if(prior!.Array("checks").Any(c=>c.Int("episode_id")==episode.Id))continue;
                    progress.Report($"Checking disputed claim {episode.Code} · {episode.Title}: {Path.GetFileName(row.Text("path"))}");
                    var result=await matcher.DirectAsync(cues,[episode],model,endpoint,true,ct,Instruction);
                    VerifySource(row);
                    if(result["verification_decision"].Array("checks").Count!=1)throw new InvalidDataException("The model did not check the disputed episode.");
                    prior.Array("checks").Add(new JsonObject{["episode_id"]=episode.Id,["result"]=result});
                    checkpoint();pause?.Checkpoint();
                }
                var reviews=prior!.Array("checks").Select(c=>c!["result"]!.AsObject()).ToList();
                var checks=new JsonArray(reviews.SelectMany(r=>r["verification_decision"].Array("checks")).Select(c=>c!.DeepClone()).ToArray());
                int supported=checks.Count(c=>c.Text("verdict")=="supported");
                if(supported>0&&checks.Any(c=>c.Text("verdict")=="insufficient_evidence"))
                    throw new InvalidDataException("A disputed episode remains inconclusive. No reduced assignment accepted.");
                if(supported==2&&reviews.Any(r=>r.Text("coverage")!="complete"))
                    throw new InvalidDataException("Separate checks do not establish two complete episodes. Combined assignment remains held.");
                string coverage=supported==0?"unknown":supported==2?"combined":reviews.Single(r=>r.Array("matches").Count==1).Text("coverage");
                // Preserve original playback order, then let normal multipart validation reject overlap/non-adjacency.
                if(supported==2)checks=new JsonArray(checks.OrderBy(c=>c.Array("supporting_blocks").Select(i=>i!.GetValue<int>()).DefaultIfEmpty(int.MaxValue).Min()).Select(c=>c!.DeepClone()).ToArray());
                var raw=new JsonObject{["assessment"]=supported>0?"supported":"uncertain",["coverage"]=coverage,["checks"]=checks,
                    ["note"]=supported==0?"No disputed episode was supported by the separate guide checks.":"Disputed episodes were checked separately against the original dialogue."};
                var decision=CompactVerification.Expand(raw,blocks,episodes);DialogueMatcher.Validate(decision,blocks,episodes,true);
                decision["method"]=DialogueMatcher.Version;decision["model"]=model;decision["dialogue_hash"]=MultipartEvidence.DialogueHash(row);
                VerifySource(row);
                row["matching_method"]="reasoning";row["reasoning"]=decision;
                row["candidates"]=new JsonArray(decision.Array("matches").Select(m=>(JsonNode)new JsonObject{
                    ["episode"]=JsonSerializer.SerializeToNode(episodes.Single(e=>e.Id==m.Int("episode_id")),Data.Options),["method"]="reasoning",
                    ["reason"]=m!["reason"]?.DeepClone(),["evidence"]=new JsonArray(m.Array("evidence").Select(b=>(JsonNode)new JsonObject{
                        ["start"]=b!["start"]?.DeepClone(),["text"]=b["source_text"]?.DeepClone()}).ToArray())}).ToArray());
                prior["summary"]=decision["summary"]!.DeepClone();prior["completed_at"]=DateTimeOffset.UtcNow.ToString("O");
                row.Remove("claim_review_error");
            }catch(OperationCanceledException){throw;}catch(UsageLimitException){throw;}
            catch(Exception ex){row["claim_review_error"]=ex.Message;if(ex.Data["model_attempts"] is string attempts)row["claim_review_attempts"]=JsonNode.Parse(attempts);if(ex.Data[AssignmentValidation.ReasonKey] is string issue)row["assignment_issue"]=issue;progress.Report("Disputed claim remains held: "+ex.Message);}
            checkpoint();pause?.Checkpoint();
        }
        foreach(var row in rows)row.Remove("claim_review_pending");checkpoint();

        void VerifySource(JsonObject row) {
            var proof=row.DeepClone().AsObject();proof.Remove("claim_review_pending");proof.Remove("claim_review_error");
            if(checkSources&&!RunRecovery.CanReuse(proof,catalog))throw new InvalidDataException("Source or saved evidence changed. No claim resolution accepted.");
        }
    }
}
