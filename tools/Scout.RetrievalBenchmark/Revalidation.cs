using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

// Replays exact recorded requests through production post-processing. It never
// edits a model answer, calls a server, reads media, or opens the application DB.
static class Revalidation {
    public static async Task Run(string sourceDirectory,string outputDirectory) {
        var source=Path.GetFullPath(sourceDirectory);var output=Path.GetFullPath(outputDirectory);
        if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new InvalidOperationException("Use an empty revalidation directory.");
        var documents=new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(source,"benchmark.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())) {
            db.Open();using var command=db.CreateCommand();command.CommandText="SELECT path,content FROM documents";using var rows=command.ExecuteReader();
            while(rows.Read())documents[Path.GetRelativePath(Path.Combine(source,"data"),rows.GetString(0))]=JsonNode.Parse((byte[])rows[1])!.AsObject();
        }
        var originalSummary=documents["summary.json"];if(!originalSummary.Bool("complete"))throw new InvalidOperationException("Wait for the measured benchmark to finish before revalidation.");
        Directory.CreateDirectory(output);var logical=Path.Combine(output,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(output,"benchmark.db")));
        void Save(string name,object value)=>Data.Save(Path.Combine(logical,name),value);
        foreach(var doc in documents)if(doc.Key is not ("summary.json" or "status.json" or "results.json" or "heartbeat.json"))Save(doc.Key,doc.Value);
        Save("summary.json",new{complete=false,failure="Revalidation in progress."});
        Save("status.json",new{state="processing",message="Revalidating unchanged recorded responses.",updated=DateTimeOffset.UtcNow});
        var fixture=documents["fixture.json"];var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
        if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ManagedThinking)throw new InvalidOperationException("Measured CPU-only, thinking-off configuration required.");
        settings.MaxRequestsPerRun=200;var results=new JsonArray();var memo=new Dictionary<string,JsonObject>();var wall=Stopwatch.StartNew();int replayed=0,unchanged=0,newContextAssignments=0;
        try {
        foreach(var input in fixture.Array("cases")) {
            string id=input.Text("case_id");var original=documents[Path.Combine("cases",id+".json")];var row=original.DeepClone().AsObject();
            foreach(var field in new[]{"result","error","error_type","model_attempts","model_candidate_ids","unaccepted_model_proposals","reused_identical_dialogue"})row.Remove(field);
            var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
            string key=input.Text("dialogue_hash")+":"+CollectionResolver.Hash(input["catalog"]!.ToJsonString());
            var records=documents.Where(p=>p.Key.StartsWith(Path.Combine("requests",id)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&p.Key.EndsWith("-request.json",StringComparison.Ordinal))
                .OrderBy(p=>int.Parse(Path.GetFileName(p.Key).Split('-')[0])).Select(p=>(Request:p.Value,Response:documents[p.Key.Replace("-request.json","-response.json",StringComparison.Ordinal)])).ToArray();
            using var handler=new RecordedHandler(records);using var http=new HttpClient(handler);
            try {
                if(memo.TryGetValue(key,out var previous)){row["result"]=previous.DeepClone();row["reused_identical_dialogue"]=true;}
                else {var result=await new DialogueMatcher(http,settings.Inference()).IdentifyAsync(cues,catalog,settings.Model,"http://127.0.0.1:1/v1/chat/completions");row["result"]=result;memo[key]=result.DeepClone().AsObject();}
                row["state"]=row["result"].Text("assessment")=="supported"?"accepted":"held";
            }catch(ReplayMismatchException){throw;}
            catch(Exception ex){row["state"]="held";row["error"]=ex.Message;row["error_type"]=ex.GetType().Name;if(ex.Data["model_attempts"] is string attempts)row["model_attempts"]=JsonNode.Parse(attempts);if(ex.Data["model_candidate_ids"] is string candidateIds)row["model_candidate_ids"]=JsonNode.Parse(candidateIds);}
            if(handler.Used!=records.Length)throw new ReplayMismatchException("The request sequence changed for "+id+"; a fresh measured run is required.");
            replayed+=handler.Used;
            if(original.Text("state")=="accepted") {
                var a=original["result"]!.DeepClone();var b=row["result"]?.DeepClone();RemoveRevision(a);if(b!=null)RemoveRevision(b);
                if(!JsonNode.DeepEquals(a,b))throw new ReplayMismatchException("Previously accepted output changed for "+id+"; revalidation cannot silently replace it.");
                unchanged++;
            }else if(row.Text("state")=="accepted") {
                if(row["result"].Array("citation_context").Count==0)throw new ReplayMismatchException("An assignment changed without a source-context extraction fix.");
                newContextAssignments++;
            }
            var expected=input.Array("reference_ids").Select(n=>n!.GetValue<int>()).Order().ToArray();var actual=row["result"].Array("matches").Select(m=>m.Int("episode_id")).Order().ToArray();
            row["reference_agreement"]=row.Text("error")==""&&actual.SequenceEqual(expected)&&(expected.Length>0||row["result"].Text("assessment")==input.Text("reference_kind"));
            row["accepted_reference_disagreement"]=actual.Length>0&&!actual.SequenceEqual(expected);row["held_non_episode_reference"]=actual.Length==0&&input.Text("reference_kind")=="non_episode";
            if(row["model_candidate_ids"] is JsonArray proposalIds)row["unaccepted_model_proposals"]=JsonSerializer.SerializeToNode(proposalIds.Select(n=>catalog.Single(e=>e.Id==n!.GetValue<int>())).Select(e=>new{id=e.Id,code=e.Code,title=e.Title,overview=e.Overview}),Data.Options);
            row["postprocessing_revalidated"]=true;Save(Path.Combine("cases",id+".json"),row);results.Add(row);
            Console.WriteLine(id+": "+row.Text("state")+(row.Text("error")==""?"":" — "+row.Text("error")));
        }
        Save("results.json",new{cases=results});var summary=originalSummary.DeepClone().AsObject();
        summary["accepted"]=results.Count(r=>r.Text("state")=="accepted");summary["held"]=results.Count(r=>r.Text("state")=="held");summary["errors"]=results.Count(r=>r.Text("error")!="");
        summary["reference_agreement"]=results.Count(r=>r.Bool("reference_agreement"));summary["accepted_reference_disagreement"]=results.Count(r=>r.Bool("accepted_reference_disagreement"));summary["held_non_episode_reference"]=results.Count(r=>r.Bool("held_non_episode_reference"));
        summary["revalidated_saved_responses"]=true;summary["exact_requests_replayed"]=replayed;summary["new_inference_requests"]=0;summary["previous_acceptances_unchanged"]=unchanged;summary["source_context_assignments"]=newContextAssignments;
        summary["revalidation_seconds"]=wall.Elapsed.TotalSeconds;summary["timing_scope"]="Original measured CPU inference, plus the separately reported offline revalidation time. Every replayed request must match exactly; raw model responses are unchanged.";
        Save("summary.json",summary);var manifest=documents["manifest.json"].DeepClone().AsObject();Save("measured-manifest.json",manifest.DeepClone());
        manifest["revision"]=DialogueMatcher.RequestRevision;manifest["core_sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location)));
        manifest["mode"]="exact-request-replay-source-context-validation";manifest["source_benchmark"]=source;manifest["source_database_sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,"benchmark.db"))));Save("manifest.json",manifest);
        Save("status.json",new{state="complete",message="All saved responses revalidated automatically; raw model outputs unchanged.",updated=DateTimeOffset.UtcNow});Finalization.Run(output);
        }catch(Exception ex) {
            Save("summary.json",new{complete=false,failure=ex.Message,cases=results.Count,manualCorrections=false,appDatabaseWrites=false,mediaAccess=false});
            Save("status.json",new{state="failed",message=ex.Message,updated=DateTimeOffset.UtcNow});throw;
        }
    }
    static void RemoveRevision(JsonNode node) {
        if(node is JsonObject obj){obj.Remove("request_revision");foreach(var pair in obj.ToArray())if(pair.Value!=null)RemoveRevision(pair.Value);}
        else if(node is JsonArray array)foreach(var value in array)if(value!=null)RemoveRevision(value);
    }
    sealed class ReplayMismatchException(string message):InvalidOperationException(message);
    sealed class RecordedHandler((JsonObject Request,JsonObject Response)[] records):HttpMessageHandler {
        public int Used {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if(Used>=records.Length||!JsonNode.DeepEquals(body,records[Used].Request))throw new ReplayMismatchException("The matcher requested new or different model input; no response was fabricated.");
            var response=records[Used++].Response;
            return new((HttpStatusCode)response.Int("http_status")){Content=new StringContent(response.Text("body"))};
        }
    }
}
