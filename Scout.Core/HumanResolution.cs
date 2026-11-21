using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

public static class HumanResolution
{
    public static JsonObject? For(JsonObject report,JsonObject row) {
        var fingerprint=CollectionResolver.FingerprintKey(row["primary_fingerprint"]);
        return fingerprint==null?null:report.Array("human_resolutions").OfType<JsonObject>()
            .LastOrDefault(r=>!r.Bool("undone")&&r.Text("fingerprint")==fingerprint);
    }
    public static bool Valid(JsonObject report,JsonObject row,JsonObject decision)=>
        decision.Text("dialogue_hash")==MultipartEvidence.DialogueHash(row)&&
        decision.Text("series_key")==SeriesKey(report)&&EpisodeRange.Valid(Episodes(decision));
    public static List<Episode> Episodes(JsonObject decision) {
        try {return decision["episodes"] is JsonArray many?many.Select(e=>Data.Episode(e!)).ToList():decision["episode"] is JsonObject one?[Data.Episode(one)]:[];}
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or ArgumentException){return [];}
    }
    private static string SeriesKey(JsonObject report)=>report["series_identity"].Text("provider")+":"+report["series_identity"].Text("id");
    public static string Apply(JsonObject report,JsonObject row,Episode episode,IReadOnlyList<Episode> catalog,string note)=>Apply(report,row,[episode],catalog,note);
    public static string Apply(JsonObject report,JsonObject row,IReadOnlyList<Episode> episodes,IReadOnlyList<Episode> catalog,string note) {
        EpisodeRange.Require(episodes);
        if(episodes.Any(episode=>!catalog.Contains(episode)))throw new InvalidOperationException("Choose an episode from this Scout's saved guide.");
        if(MoveExecutor.HasPending(report))throw new InvalidOperationException("Reconcile interrupted moves before overriding an assignment.");
        if(!RunRecovery.SourceUnchanged(row))throw new InvalidOperationException("Verify or locate this recording before assigning it. Its source fingerprint could not be confirmed.");
        if(note.Length>500)throw new ArgumentException("Keep the override note within 500 characters.");
        var id=Guid.NewGuid().ToString("N");
        if(report["human_resolutions"] is not JsonArray)report["human_resolutions"]=new JsonArray();
        report.Array("human_resolutions").Add(new JsonObject {
            ["id"]=id,["recorded_at"]=DateTimeOffset.UtcNow.ToString("O"),["source"]=row.Text("path"),
            ["fingerprint"]=CollectionResolver.FingerprintKey(row["primary_fingerprint"]),["dialogue_hash"]=MultipartEvidence.DialogueHash(row),
            ["series_key"]=SeriesKey(report),["episode"]=JsonSerializer.SerializeToNode(episodes[0],Data.Options),["episodes"]=JsonSerializer.SerializeToNode(episodes,Data.Options),["note"]=note.Trim(),["undone"]=false
        });
        report["manual_corrections"]=true;
        return id;
    }
    public static void Undo(JsonObject report,string id) {
        if(MoveExecutor.HasPending(report))throw new InvalidOperationException("Reconcile interrupted moves first.");
        var entry=report.Array("human_resolutions").OfType<JsonObject>().SingleOrDefault(r=>r.Text("id")==id&&!r.Bool("undone"))
            ??throw new InvalidOperationException("This override is no longer active.");
        entry["undone"]=true;entry["undone_at"]=DateTimeOffset.UtcNow.ToString("O");
        foreach(var row in report.Array("files").OfType<JsonObject>().Where(r=>Subtitles.SubtitleResult.IsSubtitle(r)&&r["subtitle_match"].Bool("accepted")&&r["subtitle_match"].Array("review_resolution_ids").Any(n=>n?.GetValue<string>()==id))) {
            row["superseded_subtitle_match"]=row["subtitle_match"]?.DeepClone();
            row.Remove("subtitle_match");row.Remove("reasoning");row["candidates"]=new JsonArray();row["matching_method"]="reasoning";
            row["status"]="awaiting_ai";row["error"]="";
        }
        report["manual_corrections"]=report.Array("human_resolutions").Any(r=>!r.Bool("undone"));
    }
    public static int Order(JsonObject report,JsonObject decision)=>report.Array("human_resolutions").IndexOf(decision);
}
