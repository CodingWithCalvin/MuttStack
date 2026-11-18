using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

if(args.Length==4&&args[0]=="--prepare-farpoint"){FarpointFixture.Prepare(args[1],args[2],args[3]);return;}
if(args.Length==3&&args[0]=="--prepare-descriptions"){await DescriptionFixture.Prepare(args[1],args[2]);return;}
if(args.Length==3&&args[0]=="--check-idle"){using var probe=new ScoutIdleGate(args[1],args[2]);var state=await probe.Check(default);Console.WriteLine(state.Detail);return;}
if(args.Length!=2)throw new ArgumentException("Scout.AssignmentBenchmark <isolated fixture.json> <output directory>");
var inputText=File.ReadAllText(args[0]);var fixture=JsonNode.Parse(inputText)!.AsObject();
var mode=fixture.Text("mode","conflict");if(mode is not ("identify" or "conflict"))throw new ArgumentException("Unknown benchmark mode.");
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
if(Directory.EnumerateFileSystemEntries(output).Any())throw new InvalidOperationException("Use an empty isolated output directory; existing inference is never overwritten or repeated.");
var logical=Path.Combine(output,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(output,"benchmark.db")));
void Save(string key,object value)=>Data.Save(Path.Combine(logical,key),value);
var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ThinkingEnabled!=false||settings.ManagedThinking)throw new InvalidOperationException("CPU-only and explicit thinking-off are required.");
settings.AllowDownloads=false;settings.AutoDownloadMissing=false;settings.MaxRequestsPerRun=20;
var catalog=fixture["catalog"]!["episodes"]!.Deserialize<List<Episode>>(Data.Options)!;
var inputs=fixture.Array("cases");if(inputs.Count==0)throw new InvalidDataException("No saved-dialogue cases.");
var wall=Stopwatch.StartNew();var results=new JsonArray();var gate=new object();string phase="preparing",active="";
void Status(string state,string message){lock(gate){phase=message;Save("status.json",new{state,message,active,updated=DateTimeOffset.UtcNow,seconds=wall.Elapsed.TotalSeconds});}Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {message}");}
using var deadline=new CancellationTokenSource();
using var idleGate=new ScoutIdleGate(fixture.Text("idle_status_database"),new Uri(settings.LocalEndpoint).GetLeftPart(UriPartial.Authority));
using var stopMonitor=new CancellationTokenSource();Task monitor=Task.CompletedTask;string? activityBlocker=null;
using var heartbeatStop=new CancellationTokenSource();
async Task Heartbeat(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));try{while(await timer.WaitForNextTickAsync(heartbeatStop.Token))lock(gate)Save("heartbeat.json",new{phase,active,updated=DateTimeOffset.UtcNow,seconds=wall.Elapsed.TotalSeconds});}catch(OperationCanceledException){}}
var heartbeat=Heartbeat();LocalRuntime? runtime=null;DialogueMatcher? matcher=null;string? failure=null;
Save("fixture.json",fixture);
try {
    var replay=ReadReplay(fixture.Text("replay_database"));
    // Only the copied fixture and installed model/runtime are used. No app database or media paths are opened.
    await idleGate.Wait(m=>Status("waiting",m),deadline.Token);
    deadline.CancelAfter(TimeSpan.FromMinutes(fixture.Int("inference_deadline_minutes",130)));
    Status("verifying","Verifying the installed Gemma weights before starting an isolated CPU runtime.");
    var profile=ModelDownloads.Profiles.Single(p=>p.Id==settings.DownloadProfile);
    await using(var file=File.OpenRead(settings.ModelFile))if(file.Length!=profile.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(file,deadline.Token))!=profile.Sha256)throw new IOException("Installed model checksum mismatch.");
    using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));settings.LocalEndpoint=$"http://127.0.0.1:{((IPEndPoint)socket.LocalEndPoint!).Port}/v1/chat/completions";}
    Save("manifest.json",new{inputHash=CollectionResolver.Hash(inputText),coreSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location))),
        revision=DialogueMatcher.RequestRevision,model=settings.Model,modelSha256=profile.Sha256,runtime=RuntimeDownloads.Profile(settings).Id,endpoint=settings.LocalEndpoint,
        settings.CpuThreads,settings.GpuLayers,thinking=settings.ManagedThinking,fullSwaCache=settings.ManagedFullSwaCache,context=settings.EffectiveContextSize,
        outputTokens=settings.EffectiveOutputTokens,timeoutSeconds=settings.EffectiveTimeoutSeconds,mode=inputs.All(c=>c?["saved_proposal"]!=null)?"production-grounded-saved-proposal-review":mode=="identify"?"production-identification-with-context-recovery":"production-full-guide-conflict-review",replayDatabase=fixture.Text("replay_database"),availableSavedResponses=replay.Count,manualCorrections=false,appDatabaseAccess=fixture.Text("idle_status_database")!="",appDatabaseWrites=false,appDatabaseReadScope="workspace status only",mediaAccess=false});
    await idleGate.Wait(m=>Status("waiting",m),deadline.Token);
    async Task MonitorScout() {
        try {using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));while(await timer.WaitForNextTickAsync(stopMonitor.Token)) {
            try {var state=await idleGate.Check(stopMonitor.Token);if(state.Idle)continue;activityBlocker="User Scout became active: "+state.Detail;}
            catch(Exception) when(!stopMonitor.IsCancellationRequested){activityBlocker="Could not confirm the user's Scout remains idle.";}
            deadline.Cancel();return;
        }}catch(OperationCanceledException) when(stopMonitor.IsCancellationRequested){}
    }
    monitor=MonitorScout();
    runtime=await LocalRuntime.StartAsync(settings,new ProgressCallback(m=>Status("loading",m)),deadline.Token);
    using var recorder=new Recorder(Save,m=>Status("inference",m),replay,fixture.Bool("require_first_replay"));using var http=new HttpClient(recorder){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
    matcher=new DialogueMatcher(http,settings.Inference());
    foreach(var input in inputs) {
        active=input.Text("case_id");var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;
        if(CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options))!=input.Text("dialogue_hash"))throw new InvalidDataException("Saved dialogue hash changed.");
        recorder.StartCase(active);var watch=Stopwatch.StartNew();var record=new JsonObject{["case_id"]=active,["source"]=input["source"]!.DeepClone(),["dialogue_hash"]=input["dialogue_hash"]!.DeepClone(),["cue_count"]=cues.Count,["block_count"]=DialogueMatcher.Blocks(cues).Count};
        Status("inference",$"{results.Count+1}/{inputs.Count}: {Path.GetFileName(input.Text("source"))}");
        try {
            var result=input?["saved_proposal"] is JsonObject proposal
                ?await matcher.VerifySavedProposalAsync(cues,catalog,proposal,settings.Model,settings.Endpoint,deadline.Token)
                :mode=="identify"?await matcher.IdentifyAsync(cues,catalog,settings.Model,settings.Endpoint,deadline.Token)
                :await matcher.ReevaluateConflictAsync(cues,catalog,settings.Model,settings.Endpoint,deadline.Token);record["result"]=result;
            record["state"]=result.Text("assessment")=="supported"?"supported":"held";
        }catch(OperationCanceledException) when(deadline.IsCancellationRequested){throw;}
        catch(Exception ex){record["state"]="held";record["error"]=ex.Message;record["error_type"]=ex.GetType().Name;if(ex.Data[AssignmentValidation.ReasonKey] is string issue)record["assignment_issue"]=issue;if(ex.Data["model_attempts"] is string attempts)record["model_attempts"]=JsonNode.Parse(attempts);if(ex.Data["context_recovery"] is string recovery)record["context_recovery_attempts"]=JsonNode.Parse(recovery);}
        record["seconds"]=watch.Elapsed.TotalSeconds;record["requests"]=JsonSerializer.SerializeToNode(recorder.Records,Data.Options);
        Save($"cases/{active}.json",record);results.Add(record);Save("results.json",new{cases=results});
        Status("processing",$"Completed {active}: {record.Text("state")} in {watch.Elapsed.TotalMinutes:0.0} minutes.");
    }
}catch(Exception ex){failure=activityBlocker??ex.Message;Status("failed",failure);}
finally {
    stopMonitor.Cancel();await monitor;heartbeatStop.Cancel();await heartbeat;if(runtime!=null)await runtime.DisposeAsync();
    var report=new JsonObject{["files"]=new JsonArray(),["manual_corrections"]=false};
    foreach(var record in results.OfType<JsonObject>()) {
        var input=inputs.Single(c=>c.Text("case_id")==record.Text("case_id"))!;
        var row=new JsonObject{["path"]=record["source"]!.DeepClone(),["matching_method"]="reasoning",["dialogue"]=new JsonObject{["cues"]=input["cues"]!.DeepClone()},["candidates"]=new JsonArray()};
        if(record["result"] is JsonObject result){row["reasoning"]=result.DeepClone();foreach(var m in result.Array("matches"))row.Array("candidates").Add(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==m.Int("episode_id")),Data.Options)});}
        else row["error"]=record.Text("error");report.Array("files").Add(row);
    }
    var groups=CollectionResolver.Resolve(report);
    var summary=new{complete=failure==null&&results.Count==inputs.Count,failure,cases=results.Count,accepted=groups.Count(g=>g.Episode!=null),held=groups.Count(g=>g.Episode==null),conflicts=groups.Count(g=>g.Status=="unresolved_conflict"),
        seconds=wall.Elapsed.TotalSeconds,usage=matcher?.Usage,reusedResponses=results.Sum(r=>r.Array("requests").Count(q=>q.Bool("reused"))),freshRequests=results.Sum(r=>r.Array("requests").Count(q=>!q.Bool("reused"))),manualCorrections=false,mediaAccess=false,appDatabaseAccess=fixture.Text("idle_status_database")!="",appDatabaseWrites=false,limitations=fixture.Text("limitations","Targeted regression cases using saved subtitles and guide descriptions; this is not an independent accuracy benchmark.")};
    Save("report.json",report);Save("summary.json",summary);
    string E(string s)=>WebUtility.HtmlEncode(s);
    var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Scout · saved-dialogue retest</title><style>body{font:16px/1.6 Segoe UI;max-width:1100px;margin:35px auto;padding:20px;background:#111b28;color:#e4edfb}h1,h2{color:#91c3ff}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#1d2a3c;padding:18px}td,th{padding:14px;text-align:left;border-bottom:1px solid #405269}table{width:100%;border-collapse:collapse}details{margin:20px 0}</style><h1>Automated saved-dialogue retest</h1><p>CPU only · thinking disabled · original saved dialogue and provider guide descriptions · no manual corrections · Scout results and video files untouched.</p><table><tr><th>Recording</th><th>Automated result</th><th>Time this run</th></tr>");
    html.Replace("<h1>Automated saved-dialogue retest</h1>","<h1>"+E(fixture.Text("title","Automated saved-dialogue retest"))+"</h1>");
    foreach(var record in results.OfType<JsonObject>()) {
        var group=groups.Single(g=>g.Rows.Any(r=>r.Text("path")==record.Text("source")));
        var decision=group.Episode!=null?group.Code+" · "+group.Title:"Held — "+record.Text("error",group.Status);
        html.Append("<tr><td>").Append(E(Path.GetFileName(record.Text("source")))).Append("</td><td>").Append(E(decision)).Append("</td><td>").Append((record["seconds"]!.GetValue<double>()/60).ToString("0.0")).Append(" min</td></tr>");
    }
    html.Append("</table>");
    foreach(var episode in catalog.Where(e=>e.DescriptionSource!=null)) {
        var source=episode.DescriptionSource!;
        html.Append("<p>Description for ").Append(E(episode.Code+" · "+episode.Title)).Append(" supplied by <a href=\"").Append(E(source.Url)).Append("\">").Append(E(source.Provider.ToUpperInvariant())).Append("</a>. Selected-provider episode identity retained.");
        if(source.Provider=="tvmaze")html.Append(" TVmaze data: <a href=\"https://creativecommons.org/licenses/by-sa/4.0/\">CC BY-SA 4.0</a>.");
        html.Append("</p>");
    }
    html.Append("<p>Targeted cases do not establish general accuracy. A supported suggestion remains a model judgment. Completed responses are reused only when request content matches exactly; model decisions are never edited. Full request/response bodies and runtime settings are retained in this isolated benchmark's SQLite database.</p><details><summary>Summary</summary><pre>").Append(E(JsonSerializer.Serialize(summary,Data.Options))).Append("</pre></details>");
    foreach(var record in results)html.Append("<details><summary>").Append(E(Path.GetFileName(record.Text("source")))).Append(" · saved response details</summary><pre>").Append(E(record!.ToJsonString(Data.Options))).Append("</pre></details>");
    File.WriteAllText(Path.Combine(output,"results.html"),html.Append("</html>").ToString());
    Status(summary.complete?"complete":"failed",summary.complete?$"Retest finished: {summary.accepted} accepted, {summary.held} held.":"Retest blocked: "+failure);
    if(!summary.complete)Environment.ExitCode=1;
}

static Dictionary<string,SavedResponse> ReadReplay(string path) {
    var replay=new Dictionary<string,SavedResponse>();if(path=="")return replay;
    using var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(path),Mode=SqliteOpenMode.ReadOnly}.ToString());database.Open();
    using var command=database.CreateCommand();command.CommandText="SELECT path,content FROM documents";using var reader=command.ExecuteReader();var documents=new Dictionary<string,JsonNode>();
    while(reader.Read())documents[reader.GetString(0)]=JsonNode.Parse(reader.GetValue(1) is byte[] bytes?Encoding.UTF8.GetString(bytes):reader.GetString(1))!;
    foreach(var (name,request) in documents.Where(d=>d.Key.EndsWith("-request.json",StringComparison.Ordinal))) {
        var responseName=name[..^"-request.json".Length]+"-response.json";
        if(!documents.TryGetValue(responseName,out var response)||response.Int("http_status")!=200)continue;
        var raw=response.Text("body");if(JsonNode.Parse(raw)!.Array("choices").SingleOrDefault().Text("finish_reason")!="stop")continue;
        var hash=CollectionResolver.Hash(request.ToJsonString());
        if(replay.TryGetValue(hash,out var previous)&&previous.Body!=raw)throw new InvalidDataException("Conflicting saved responses for the same request; no response chosen.");
        replay[hash]=new(raw,name);
    }
    return replay;
}
sealed class ProgressCallback(Action<string> action):IProgress<string>{public void Report(string value)=>action(value);}
sealed record SavedResponse(string Body,string Request);
sealed class Recorder(Action<string,object> save,Action<string> progress,IReadOnlyDictionary<string,SavedResponse> replay,bool requireFirstReplay):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    string active="";public List<JsonObject> Records=[];
    public void StartCase(string id){active=id;Records.Clear();}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        var key=$"requests/{active}/{Records.Count+1:00}";var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        save(key+"-request.json",body);var task=JsonNode.Parse(body["messages"]![1].Text("content"))!.Text("task");
        var entry=new JsonObject{["index"]=Records.Count+1,["state"]="running",["started"]=DateTimeOffset.UtcNow,["validation_retry"]=task.Contains("The previous response failed validation:"),["thinking"]=body["chat_template_kwargs"].Bool("enable_thinking")};
        Records.Add(entry);save(key+"-status.json",entry);var watch=Stopwatch.StartNew();progress($"{active}: model request {Records.Count} started.");
        HttpResponseMessage? response=null;
        try {
            if(replay.TryGetValue(CollectionResolver.Hash(body.ToJsonString()),out var saved)) {
                response=new(HttpStatusCode.OK){Content=new StringContent(saved.Body)};entry["reused"]=true;entry["reused_request"]=saved.Request;
                save(key+"-status.json",entry);progress($"{active}: reusing the unchanged saved response for request {Records.Count}.");
            }else{
                if(requireFirstReplay&&Records.Count==1)throw new InvalidDataException("The first audit does not exactly match its saved request. No replacement inference started.");
                entry["reused"]=false;save(key+"-status.json",entry);response=await base.SendAsync(request,ct);
            }
            var raw=await response.Content.ReadAsStringAsync(ct);save(key+"-response.json",new{httpStatus=(int)response.StatusCode,body=raw});
            var parsed=JsonNode.Parse(raw)!;entry["state"]="complete";entry["seconds"]=watch.Elapsed.TotalSeconds;entry["http_status"]=(int)response.StatusCode;
            entry["usage"]=parsed["usage"]?.DeepClone();entry["timings"]=parsed["timings"]?.DeepClone();
            save(key+"-status.json",entry);progress($"{active}: request {Records.Count} returned in {watch.Elapsed.TotalMinutes:0.0} minutes.");return response;
        }catch(Exception ex){response?.Dispose();entry["state"]="failed";entry["seconds"]=watch.Elapsed.TotalSeconds;entry["error"]=ex.Message;save(key+"-status.json",entry);throw;}
    }
}
