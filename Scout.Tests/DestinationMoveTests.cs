using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class DestinationMoveTests {
 sealed class Progress<T>(Action<T> callback):IProgress<T>{public void Report(T value)=>callback(value);}
 public static void Run(Action<string,Action> test,string temp) {
  void Check(bool value,string message="Destination move assertion failed"){if(!value)throw new Exception(message);}
  Episode[] catalog=[new(801,1,1,"Opening")];
  (string Root,string Report,JsonObject Row,MovePlan Plan) Setup(string name,string destination) {
   var root=Path.Combine(temp,name);Directory.CreateDirectory(root);var path=Path.Combine(root,"episode.mkv");File.WriteAllBytes(path,Enumerable.Range(0,3000000).Select(i=>(byte)(i%251)).ToArray());
   var row=new JsonObject{["path"]=path,["matching_method"]="reasoning",["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog[0],Data.Options)}),["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
   var report=Path.Combine(temp,name+"-data","report.json");Data.Save(report,new JsonObject{["files"]=new JsonArray(row)});
   return(root,report,row,PreviewPlanner.Create(Data.Read(report),root,"Show",catalog,destinationRoot:destination,contentOptions:new()));
  }
  var quiet=new Progress<MoveUpdate>(_=>{});var log=new Progress<string>(_=>{});
  test("Separate-root moves remove episodes from source and keep saved evidence and full destination paths",()=>{
   var s=Setup("separate-root",Path.Combine(temp,"library","Show"));var item=s.Plan.Items.Single();
   Check(item.Destination==Path.Combine(temp,"library","Show","Season 01","Show - S01E01 - Opening.mkv"));
   MoveExecutor.ApplyAsync(s.Report,s.Plan,[item.Source],catalog,quiet,default).GetAwaiter().GetResult();
   var saved=Data.Read(s.Report);Check(!File.Exists(item.Source)&&File.Exists(item.Destination)&&VideoSelection.Enumerate(s.Root).Count()==0);
   Check(RunRecovery.SourceUnchanged(saved.Array("files")[0]!.AsObject())&&RecordingRelocation.MovedOutsideSource(saved,item.Destination!,s.Root));
   Check(!RecordingRelocation.MovedOutsideSource(saved,Path.Combine(temp,"foreign.mkv"),s.Root));
   var catalogPath=Path.Combine(Path.GetDirectoryName(s.Report)!,"catalog.json");Data.Save(catalogPath,new{episodes=catalog});
   using var http=new HttpClient(new FakeHandler());var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>throw new Exception("Moved episodes must not be reprocessed"));
   pipeline.RunAsync(new(s.Root,catalogPath,Path.GetDirectoryName(s.Report)!,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:[item.Source],Restart:true,ContentOptions:new()),log,default).GetAwaiter().GetResult();
   Check(Data.Read(s.Report).Array("files")[0].Text("path")==item.Destination);
  });
  test("Destination changes invalidate previews and nested destinations are rejected",()=>{
   var s=Setup("destination-validation",Path.Combine(temp,"destination-validation-out"));var report=Data.Read(s.Report);
   Check(PreviewCache.Signature(report,s.Root,"Show",catalog,new(),null,s.Plan.DestinationRoot,new())!=PreviewCache.Signature(report,s.Root,"Show",catalog,new(),null,Path.Combine(temp,"different"),new()));
   foreach(var target in new[]{Path.Combine(s.Root,"output"),temp,"relative-folder"}) {
    bool rejected=false;try{PreviewPlanner.Create(report,s.Root,"Show",catalog,destinationRoot:target);}catch(ArgumentException){rejected=true;}Check(rejected);
   }
  });
  test("Doggo reopen retains separate-root move history and a second rename remains outside source scans",()=>{
   var s=Setup("destination-doggo",Path.Combine(temp,"destination-doggo-out"));var folder=Path.GetDirectoryName(s.Report)!;var snapshot=Data.Read(s.Report);
   var databasePath=Path.Combine(temp,"destination-doggo.db");ScoutStorage.Register(folder,new ScoutDatabase(databasePath,doggo:true));
   try {
    Data.Save(s.Report,snapshot);var first=s.Plan.Items.Single();MoveExecutor.ApplyAsync(s.Report,s.Plan,[first.Source],catalog,quiet,default).GetAwaiter().GetResult();
    ScoutStorage.Unregister(folder);ScoutStorage.Register(folder,ScoutDatabase.OpenExistingDoggo(databasePath));
    var saved=Data.Read(s.Report);Check(saved.Text("destination_root")==s.Plan.DestinationRoot&&ScoutContentOptions.Read(saved)==new ScoutContentOptions());
    var next=PreviewPlanner.Create(saved,s.Root,"Renamed Show",catalog,naming:new(){PreserveCorrectNames=false},destinationRoot:s.Plan.DestinationRoot,contentOptions:new());var pending=next.Items.Single(i=>i.Action=="rename/move");
    MoveExecutor.ApplyAsync(s.Report,next,[pending.Source],catalog,quiet,default).GetAwaiter().GetResult();
    Check(RecordingRelocation.MovedOutsideSource(Data.Read(s.Report),pending.Destination!,s.Root)&&!File.Exists(first.Destination)&&File.Exists(pending.Destination));
   }finally{ScoutStorage.Unregister(folder);}
  });
  if(Environment.GetEnvironmentVariable("SCOUT_CROSS_VOLUME_TEST_ROOT") is not string crossRoot)return;
  crossRoot=Path.GetFullPath(crossRoot);Directory.CreateDirectory(crossRoot);
  Check(!string.Equals(Path.GetPathRoot(crossRoot),Path.GetPathRoot(temp),StringComparison.OrdinalIgnoreCase),"Cross-volume test root must use another drive");
  foreach(var scenario in new[]{"success","cancel","published-interruption","completion-interruption","corrupt-recovery","occupied"}) test("Cross-drive verified move: "+scenario,()=>{
   var destination=Path.Combine(crossRoot,Guid.NewGuid().ToString("N"));var s=Setup("cross-"+scenario,destination);var item=s.Plan.Items.Single();using var cancel=new CancellationTokenSource();bool injected=false;
   try {
    if(scenario=="occupied"){Directory.CreateDirectory(Path.GetDirectoryName(item.Destination!)!);File.WriteAllText(item.Destination!,"existing");}
    void Save(JsonObject report) {
     var entry=report.Array("moves").Single()!;
     if(!injected&&scenario=="published-interruption"&&entry.Bool("copy_published")){injected=true;throw new IOException("Simulated interruption after publish");}
     if(scenario is "completion-interruption" or "corrupt-recovery"&&entry.Text("state")=="completed")throw new IOException("Simulated completion save failure");
     Data.Save(s.Report,report);
     if(scenario=="cancel"&&entry.Text("sha256")!=""&&!entry.Bool("copy_published"))cancel.Cancel();
    }
    try{MoveExecutor.ApplyAsync(s.Report,s.Plan,[item.Source],catalog,quiet,cancel.Token,Save).GetAwaiter().GetResult();}
    catch(Exception ex) when(ex is IOException or OperationCanceledException or InvalidOperationException) {if(scenario=="success")throw;}
    if(scenario=="success") {
     var saved=Data.Read(s.Report);Check(!File.Exists(item.Source)&&File.Exists(item.Destination)&&saved.Array("moves")[0].Text("transfer_method")=="verified-copy-v1");
     Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Destination!)))==s.Row["primary_fingerprint"].Text("sha256")&&RunRecovery.SourceUnchanged(saved.Array("files")[0]!.AsObject()));
    } else if(scenario=="cancel")Check(File.Exists(item.Source)&&!File.Exists(item.Destination)&&!Directory.EnumerateFiles(destination,"*.partial",SearchOption.AllDirectories).Any()&&!MoveExecutor.HasPending(Data.Read(s.Report)));
    else if(scenario=="published-interruption") {
     Check(File.Exists(item.Source)&&File.Exists(item.Destination));var recovered=MoveExecutor.RecoverAsync(s.Report,s.Root,log,default).GetAwaiter().GetResult();Check(MoveExecutor.HasPending(recovered)&&File.Exists(item.Source));
    } else if(scenario is "completion-interruption" or "corrupt-recovery") {
     Check(!File.Exists(item.Source)&&File.Exists(item.Destination)&&MoveExecutor.HasPending(Data.Read(s.Report)));
     if(scenario=="corrupt-recovery")File.AppendAllText(item.Destination!,"changed");
     var recovered=MoveExecutor.RecoverAsync(s.Report,s.Root,log,default).GetAwaiter().GetResult();Check(MoveExecutor.HasPending(recovered)==(scenario=="corrupt-recovery"));
    } else Check(File.ReadAllText(item.Destination!)=="existing"&&File.Exists(item.Source));
   } finally {
    // Only this test's newly generated child of the explicit test root is removed.
    if(PreviewPlanner.Within(destination,crossRoot)&&!PreviewPlanner.Same(destination,crossRoot)&&Directory.Exists(destination))Directory.Delete(destination,true);
   }
  });
 }
}
