using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

/// <summary>Checks extractive grounding and derives assignments without substituting candidate identities.</summary>
public static class GroundedVerification
{
    public static string[] Statements(Episode episode)
    {
        RequireDescription(episode);
        var text=episode.Overview.Trim();var parts=new List<string>();int start=0;
        foreach(Match boundary in Regex.Matches(text,@"(?<=[.!?])\s+(?=[A-Z\""“])")) {
            // Keep initials, acronyms and common title abbreviations attached to the
            // sentence they qualify. When ambiguous, a longer intact statement is safer
            // than an unverifiable fragment. Never remove or paraphrase guide text.
            var prefix=text[..boundary.Index];
            if(Regex.IsMatch(prefix,@"(?:\p{L}\.){2,}$|\b(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|St|Lt|Capt|Cmdr|Gen|Col|Sgt|Rev|vs|etc)\.$",RegexOptions.IgnoreCase))continue;
            parts.Add(text[start..boundary.Index]);start=boundary.Index+boundary.Length;
        }
        parts.Add(text[start..]);var statements=parts.Where(s=>s.Length>0).ToArray();
        if(statements.Length is <1 or >24)throw new InvalidDataException("A usable episode description is required for grounded review.");
        return statements;
    }

    public static void RequireDescription(Episode episode) {
        if(string.IsNullOrWhiteSpace(episode.Overview))throw AssignmentValidation.Reject(AssignmentIssue.MissingDescription,
            $"Missing episode description: {episode.Code} · {episode.Title} (catalog ID {episode.Id}). "+(episode.DescriptionIssue??"No guide evidence is available to verify this assignment."));
    }

    public static List<Episode> Proposed(JsonObject raw,IReadOnlyList<Episode> catalog)
    {
        if(raw.Text("assessment")!="supported")throw new InvalidDataException("No supported proposal to audit.");
        var ids=raw["checks"] is JsonArray checks
            ?checks.Where(c=>c.Text("verdict")=="supported").Select(c=>c.Int("episode_id")).ToList()
            :raw.Array("matches").Select(m=>catalog.SingleOrDefault(e=>e.Title==m.Text("episode_title"))?.Id??m.Int("episode_id")).ToList();
        if(ids.Count is <1 or >EpisodeRange.Maximum||ids.Distinct().Count()!=ids.Count||ids.Any(id=>catalog.Count(e=>e.Id==id)!=1))
            throw new InvalidDataException("Grounded review requires one to four distinct proposed catalog entries.");
        return ids.Select(id=>catalog.Single(e=>e.Id==id)).ToList();
    }

    public static JsonObject Expand(JsonObject raw,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes)
    {
        if(raw.Count!=1||raw["candidates"] is not JsonArray audits||audits.Count!=episodes.Count)
            throw new InvalidDataException("Every proposed candidate must receive one grounded audit.");
        var seen=new HashSet<int>();var checks=new JsonArray();var accepted=new List<(int Index,string Scope,int First,int Last)>();bool unresolvedPart=false;
        foreach(var node in audits) {
            if(node is not JsonObject audit||audit.Count!=6||audit["candidate"] is not JsonValue value||!value.TryGetValue<int>(out var index)||index<0||index>=episodes.Count||!seen.Add(index))
                throw new InvalidDataException("Unknown or repeated grounded candidate.");
            var statements=Statements(episodes[index]);var scope=EpisodeCoverage.Scope(audit.Text("recording_scope"));var type=EpisodeCoverage.Content(audit.Text("content_type"));
            if(scope is not ("entire_episode" or "one_of_separate_episodes" or "one_of_continuous_parts" or "incomplete_episode" or "not_established")||type is not ("complete_episode" or "incomplete_episode" or "preview_or_promo" or "recap" or "unknown"))
                throw new InvalidDataException("Invalid grounded episode coverage.");
            ModelOutputQuality.ValidateText(audit["reason"],"grounded reason",240);
            if(audit["statements"] is not JsonArray facts||facts.Count!=statements.Length)throw new InvalidDataException("Every complete guide statement must be checked.");
            var factIds=new HashSet<int>();var supporting=new HashSet<int>();var contradicting=new HashSet<int>();bool allSupported=true;
            foreach(var fact in facts) {
                if(fact is not JsonObject f||f.Count!=3||f["statement"] is not JsonValue n||!n.TryGetValue<int>(out var id)||id<0||id>=statements.Length||!factIds.Add(id))
                    throw new InvalidDataException("Missing, repeated or unknown guide statement.");
                var verdict=f.Text("verdict");
                if(verdict is not ("supported" or "contradicted" or "insufficient_evidence")||f["evidence"] is not JsonArray quotes||quotes.Count>2||verdict!="insufficient_evidence"&&quotes.Count==0)
                    throw new InvalidDataException("A grounded verdict needs original evidence.");
                allSupported&=verdict=="supported";
                foreach(var q in quotes) {
                    if(q is not JsonObject quote||quote.Count!=2||quote["block"] is not JsonValue b||!b.TryGetValue<int>(out var block)||block<0||block>=blocks.Count)
                        throw new InvalidDataException("Invalid grounded dialogue block.");
                    var resolved=GroundedQuote.Resolve(blocks,block,quote.Text("quote"));
                    if(verdict=="supported"){supporting.Add(block);supporting.Add(resolved.EndBlock);}
                    if(verdict=="contradicted"){contradicting.Add(block);contradicting.Add(resolved.EndBlock);}
                }
            }
            var reportedStory=new List<int>();
            if(audit["story_blocks"] is not JsonArray storyNodes||storyNodes.Count>4)throw new InvalidDataException("Invalid grounded story evidence.");
            foreach(var b in storyNodes) {
                if(b is not JsonValue v||!v.TryGetValue<int>(out var i)||i<0||i>=blocks.Count||reportedStory.Contains(i))throw new InvalidDataException("Invalid grounded story block.");
                reportedStory.Add(i);
            }
            // The separately generated list is diagnostic only. Bind accepted evidence to
            // the verified statement quotes instead of trusting duplicated model indices.
            var story=facts.OrderBy(f=>f.Int("statement")).Where(f=>f.Text("verdict")=="supported")
                .SelectMany(f=>f.Array("evidence")).Select(q=>q.Int("block")).Distinct().Take(4).ToList();
            bool positive=allSupported&&(type=="complete_episode"&&(scope=="entire_episode"||EpisodeCoverage.IsCombinedScope(scope))||type=="incomplete_episode"&&scope=="incomplete_episode");
            if(!positive&&type=="complete_episode"&&supporting.Count>=2&&contradicting.Count==0)unresolvedPart=true;
            if(positive&&story.Count<2)throw new InvalidDataException("A grounded assignment needs supporting quotes from two distinct original blocks.");
            if(positive)accepted.Add((index,scope,supporting.Min(),supporting.Max()));
            checks.Add(new JsonObject{["episode_id"]=episodes[index].Id,["verdict"]=positive?"supported":contradicting.Count>0?"contradicted":"insufficient_evidence",
                ["supporting_blocks"]=new JsonArray((positive?story:supporting.Order().Take(4)).Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray()),
                ["contradicting_blocks"]=new JsonArray(contradicting.Order().Take(4).Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray()),
                ["content_type"]=type,["reason"]=audit["reason"]!.DeepClone()});
        }
        if(accepted.Count==1&&unresolvedPart&&EpisodeCoverage.AreNumberedParts(episodes))throw AssignmentValidation.Reject(AssignmentIssue.UncertainStoryRole,"A related catalog part retains supporting but incomplete guide evidence. Whole-recording coverage is unresolved; no single-part assignment accepted.");
        if(accepted.Count==1&&EpisodeCoverage.IsCombinedScope(accepted[0].Scope))throw AssignmentValidation.Reject(AssignmentIssue.UncertainStoryRole,"Another catalog installment remains unidentified. No single-episode assignment accepted.");
        if(accepted.Count>1&&accepted.Any(a=>a.Scope=="incomplete_episode"))throw AssignmentValidation.Reject(AssignmentIssue.IncompleteCombinedStory,"At least one catalog installment is recorded incompletely. A continuous story is allowed, but every episode needs complete coverage.");
        if(accepted.Count>1&&(accepted.Any(a=>!EpisodeCoverage.IsCombinedScope(a.Scope))||accepted.Select(a=>a.Scope).Distinct().Count()!=1))throw AssignmentValidation.Reject(AssignmentIssue.UncertainStoryRole,"Two guide matches do not consistently establish two complete catalog installments, either separate episodes or parts of one continuous story.");
        if(accepted.Count>1) {
            var ordered=accepted.OrderBy(a=>a.First).ToList();
            if(ordered.Count>2 && ordered.Zip(ordered.Skip(1)).Any(p=>p.First.Last>=p.Second.First))
                throw AssignmentValidation.Reject(AssignmentIssue.OverlappingStories,"Each combined installment needs distinct ordered evidence.");
            if(ordered.Count==2 && ordered[0].Last>=ordered[1].First) {
                // Explicit consecutive parts can share their exact handoff block. Require
                // two independent quoted blocks on EACH side; interleaving is still held.
                // Keep the shared quotes in the full audit, but use exclusive quotes for
                // the assignment's ordered evidence. No candidate identity is substituted.
                int boundary=ordered[0].Last;
                if(boundary!=ordered[1].First||ordered.Any(a=>a.Scope!="one_of_continuous_parts")||!EpisodeCoverage.AreNumberedParts(episodes))
                    throw AssignmentValidation.Reject(AssignmentIssue.OverlappingStories,"Grounded quote ranges overlap; distinct successive catalog installments are not established.");
                for(int side=0;side<2;side++) {
                    var item=ordered[side];var audit=audits.Single(a=>a.Int("candidate")==item.Index)!;
                    var exclusive=audit.Array("statements").Where(f=>f.Text("verdict")=="supported").SelectMany(f=>f.Array("evidence"))
                        .Where(q=>side==0?GroundedQuote.Resolve(blocks,q.Int("block"),q.Text("quote")).EndBlock<boundary:q.Int("block")>boundary)
                        .Select(q=>q.Int("block")).Distinct().Take(4).ToArray();
                    if(exclusive.Length<2)throw AssignmentValidation.Reject(AssignmentIssue.OverlappingStories,"A shared transition does not establish two episodes without two distinct grounded evidence blocks on each side.");
                    checks.Single(c=>c.Int("episode_id")==episodes[item.Index].Id)!["supporting_blocks"]=new JsonArray(exclusive.Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray());
                }
            }
            checks=new JsonArray(checks.OrderBy(c=>accepted.FirstOrDefault(a=>episodes[a.Index].Id==c.Int("episode_id")).First).Select(c=>c!.DeepClone()).ToArray());
        }
        var compact=new JsonObject{["assessment"]=accepted.Count>0?"supported":"uncertain",["coverage"]=accepted.Count>1?"combined":accepted.Count==0?"unknown":accepted[0].Scope=="incomplete_episode"?"partial":"complete",
            ["checks"]=checks,["note"]="Candidates were audited against their complete guide statements and exact original dialogue excerpts."};
        var result=CompactVerification.Expand(compact,blocks,episodes);DialogueMatcher.Validate(result,blocks,episodes,true,requireContentType:true);
        foreach(var check in result.Array("candidate_checks")) {
            int index=episodes.ToList().FindIndex(e=>e.Id==check.Int("episode_id"));var audit=audits.Single(a=>a.Int("candidate")==index)!;var guide=Statements(episodes[index]);
            check!["recording_scope"]=audit["recording_scope"]!.DeepClone();
            check["guide_grounding"]=new JsonArray(audit.Array("statements").Select(s=>{
                var fact=s!.DeepClone().AsObject();fact["guide_text"]=guide[s.Int("statement")];
                foreach(var q in fact.Array("evidence")) {var resolved=GroundedQuote.Resolve(blocks,q.Int("block"),q.Text("quote"));q!["source_quote"]=resolved.SourceText;q["end_block"]=resolved.EndBlock;}
                return (JsonNode)fact;
            }).ToArray());
        }
        result["grounded_audit"]=raw.DeepClone();return result;
    }
}
