using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core.Subtitles;

/// <summary>User-confirmed reference identity, bound to the provider and exact reference text.</summary>
public static class ReviewedReferenceMapping
{
    internal static string Digest(TranscriptIndex index)=>CollectionResolver.Hash(string.Join(" ",index.Words));
    public static bool CanReview(JsonObject row) {
        try {
            var proof=row["subtitle_match"]!;var best=proof["best"]!;
            return proof.Text("dialogue_hash")==SubtitleResult.DialogueHash(MultipartEvidence.Cues(row)) &&
                ReferenceMatcher.Strong(best["overlap"]!.Deserialize<Overlap>(Data.Options)!) &&
                proof["margin"]!.GetValue<double>()>=.15 && best.Bool("complete_extent") && best.Bool("ordered_parts") &&
                best.Array("references").Count==1 && best.Array("references")[0].Text("content_hash").Length==64;
        }catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or NullReferenceException or JsonException){return false;}
    }
    public static void Save(JsonObject report,JsonObject row,IReadOnlyList<Episode> episodes,IReadOnlyList<Episode> catalog,string resolutionId) {
        EpisodeRange.Require(episodes);
        if(!CanReview(row)||episodes.Any(e=>!catalog.Contains(e)))throw new InvalidOperationException("Recompare saved dialogue before reviewing this reference; complete, distinctive subtitle evidence is required.");
        var proof=row["subtitle_match"]!;var series=report["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options)!;series.Validate();
        if(proof.Text("provider")!=series.Provider||proof.Text("series_id")!=series.Id)throw new InvalidOperationException("The reference belongs to another catalog provider.");
        var decision=report.Array("human_resolutions").Single(d=>d.Text("id")==resolutionId)!.AsObject();
        if(!HumanResolution.Valid(report,row,decision)||!HumanResolution.Episodes(decision).Select(e=>e.Id).SequenceEqual(episodes.Select(e=>e.Id)))
            throw new InvalidOperationException("Confirm the recording's full episode assignment first.");
        decision["subtitle_reference_review"]=new JsonObject {
            ["series_key"]=series.Key,["reference"]=proof["best"]!.Array("references")[0]!.DeepClone(),
            ["episodes"]=JsonSerializer.SerializeToNode(episodes,Data.Options)
        };
    }
    internal static ProviderLink? Find(JsonArray decisions,SubtitleReference reference,TranscriptIndex index,IReadOnlyList<Episode> catalog,SeriesIdentity series,out bool blocked) {
        blocked=false;
        var reviews=decisions.OfType<JsonObject>().Where(d=>!d.Bool("undone")&&d.Text("series_key")==series.Key)
            .Select(d=>d["subtitle_reference_review"]).Where(r=>r!=null && r.Text("series_key")==series.Key &&
                r!["reference"].Text("id")==reference.Id && r["reference"].Text("name")==reference.Name &&
                r["reference"].Text("content_hash")==Digest(index)).ToArray();
        if(reviews.Length==0)return null;
        // Conflicting reviews are held until the user undoes the obsolete one.
        var choices=reviews.Select(r=>r!.Array("episodes").Select(e=>Data.Episode(e!)).ToArray()).ToArray();
        if(choices.Any(c=>!EpisodeRange.Valid(c)||c.Any(e=>!catalog.Any(current=>current.Id==e.Id&&current.Season==e.Season&&current.Number==e.Number&&current.Title==e.Title))) ||
            choices.Select(c=>string.Join(",",c.Select(e=>e.Id))).Distinct().Count()!=1){blocked=true;return null;}
        return new(choices[0].Select(e=>catalog.Single(c=>c.Id==e.Id)).ToArray(),"user-reviewed-reference-identity",true,[reference.Id]);
    }
}
