using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed record ScoutContentOptions {
    public bool IncludeBonusContent { get; init; }
    public bool IncludeCompilations { get; init; }
    public static ScoutContentOptions? Read(JsonObject report) => report["content_options"]?.Deserialize<ScoutContentOptions>(Data.Options);
    public static bool KnownBonus(JsonObject row) => row.Text("error")=="" && !row.Bool("source_changed") &&
        (row["reasoning"].Text("assessment") == "non_episode" && row.Array("candidates").Count==0 ||
        row.Array("candidates").Count > 0 && row.Array("candidates").All(c => c?["episode"].Int("season", -1) == 0));
}
