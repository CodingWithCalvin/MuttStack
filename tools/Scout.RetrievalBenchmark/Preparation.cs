using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class Preparation {
    public static void Run(string difficultPath,string farpointPath,string output) {
        if(File.Exists(output))throw new InvalidOperationException("Fixture already exists; no overwrite.");
        var difficult=JsonNode.Parse(File.ReadAllText(difficultPath))!;var farpoint=JsonNode.Parse(File.ReadAllText(farpointPath))!;var cases=new JsonArray();
        foreach(var input in farpoint.Array("cases")) {
            var catalog=farpoint["catalog"]!["episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
            var episodes=GroundedVerification.Proposed(input!["saved_proposal"]!.AsObject(),catalog);
            Add("farpoint",input.Text("source"),"Star Trek: The Next Generation",input["cues"]!,farpoint["catalog"]!["episodes"]!,JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id))!,"supported","Prior automated Farpoint proposal; not independent truth.");
        }
        foreach(var input in difficult.Array("inputs")) {
            var label=difficult.Array("labels").Single(l=>l.Text("case_id")==input.Text("case_id"))!;
            Add(input.Text("case_id"),label.Text("path"),"Victorious",input!["cues"]!,difficult["catalog"]!["episodes"]!,label["expected_ids"]!,label.Text("expected_kind"),"Inherited difficult-case reference; not independent truth.");
        }
        var settings=farpoint["settings"]!.DeepClone();settings["gpu_layers"]=0;settings["thinking_enabled"]=false;settings["request_timeout_seconds"]=7200;settings["max_requests_per_run"]=200;settings["allow_downloads"]=false;settings["auto_download_missing"]=false;
        var fixture=new JsonObject{["settings"]=settings,["idle_status_database"]=farpoint["idle_status_database"]!.DeepClone(),["cases"]=cases,
            ["provenance"]=new JsonArray(new JsonObject{["path"]=Path.GetFullPath(difficultPath),["sha256"]=CollectionResolver.Hash(File.ReadAllText(difficultPath))},new JsonObject{["path"]=Path.GetFullPath(farpointPath),["sha256"]=CollectionResolver.Hash(File.ReadAllText(farpointPath))}),
            ["manual_corrections"]=false};
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,fixture.ToJsonString(Data.Options));Console.WriteLine($"Prepared {cases.Count} unchanged saved-dialogue cases; {cases.Select(c=>c.Text("dialogue_hash")).Distinct().Count()} unique dialogues.");
        void Add(string id,string source,string series,JsonNode dialogue,JsonNode catalog,JsonNode reference,string kind,string provenance) {
            var cues=dialogue.Deserialize<List<Cue>>(Data.Options)!;
            cases.Add(new JsonObject{["case_id"]=id,["source"]=source,["series"]=series,["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options)),["cues"]=dialogue.DeepClone(),["catalog"]=catalog.DeepClone(),["reference_ids"]=reference.DeepClone(),["reference_kind"]=kind,["reference_provenance"]=provenance});
        }
    }
}
