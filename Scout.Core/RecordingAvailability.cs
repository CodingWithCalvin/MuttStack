using System.Text.Json.Nodes;
namespace Scout.Core;
public static class RecordingAvailability
{
    public static bool Missing(JsonObject row)=>row["source_availability"].Text("state")=="missing"&&
        row["source_availability"].Text("path")==row.Text("path")&&
        row["source_availability"].Text("fingerprint")==CollectionResolver.FingerprintKey(row["primary_fingerprint"]);
    public static void MarkMissing(JsonObject row)=>row["source_availability"]=new JsonObject {
        ["state"]="missing",["path"]=row.Text("path"),["fingerprint"]=CollectionResolver.FingerprintKey(row["primary_fingerprint"]),
        ["checked_at"]=DateTimeOffset.UtcNow.ToString("O")
    };
}
