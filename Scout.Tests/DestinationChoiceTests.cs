using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class DestinationChoiceTests {
 static void Check(bool ok,string message="Destination choice regression"){if(!ok)throw new Exception(message);}
 sealed class Progress:IProgress<MoveUpdate>{public void Report(MoveUpdate u){}}
 public static void Run(Action<string,Action> test,string temp) {
  Episode[] catalog=[new(610,1,1,"Opening")];
  (string Root,string Report,MovePlan Plan) Setup(string name) {
   var root=Path.Combine(temp,"destination-choices",name,"media");Directory.CreateDirectory(root);
   var source=Path.Combine(root,"old-name.mkv");File.WriteAllText(source,"source recording");
   var row=new JsonObject{["path"]=source,["matching_method"]="reasoning",["duration"]=1320,["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog[0],Data.Options)}),
    ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),["size"]=new FileInfo(source).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(source),["sampled_digest"]=PreviewPlanner.SampledDigest(source)}};
   var reportPath=Path.Combine(temp,"destination-choices",name,"report.json");Data.Save(reportPath,new JsonObject{["files"]=new JsonArray(row)});
   var plan=Plan(reportPath,root);var target=plan.Items.Single().Destination!;Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,"existing recording");
   return(root,reportPath,Plan(reportPath,root));
  }
  MovePlan Plan(string path,string root)=>PreviewPlanner.Create(Data.Read(path),root,"Show",catalog,naming:new(){PreserveCorrectNames=false},destinationRoot:root);
  void Choose(string path,MovePlan plan,string choice) {var report=Data.Read(path);DestinationChoices.Save(report,plan,plan.Items.Single().Source,choice);Data.Save(path,report);}
  test("Same-root conflict offers keep-both; applying it preserves the existing file",()=>{
   var s=Setup("both");var original=s.Plan.Items.Single();Check(original.DestinationConflict&&original.Action=="hold");
   Choose(s.Report,s.Plan,"both");var next=Plan(s.Report,s.Root);var chosen=next.Items.Single();
   Check(chosen.Action=="rename/move"&&!chosen.DestinationConflict&&chosen.Destination!.EndsWith(" (2).mkv"));
   MoveExecutor.ApplyAsync(s.Report,next,[chosen.Source],catalog,new Progress(),default).GetAwaiter().GetResult();
   Check(File.ReadAllText(original.Destination!)=="existing recording"&&File.ReadAllText(chosen.Destination!)=="source recording"&&!File.Exists(chosen.Source));
   Check(Plan(s.Report,s.Root).Items.All(i=>i.Action=="completed"),"Keep-both choices must remain stable after moving.");
  });
  test("Skip and reset are persisted choices without media changes",()=>{
   var s=Setup("skip");Choose(s.Report,s.Plan,"skip");var skipped=Plan(s.Report,s.Root);
   Check(skipped.Items.Single().Action=="hold"&&skipped.Items.Single().DestinationChoice=="skip"&&File.Exists(skipped.Items.Single().Source));
   Choose(s.Report,skipped,"reset");Check(Plan(s.Report,s.Root).Items.Single().DestinationConflict);
  });
  test("Keep-both never silently changes an approved filename after a new collision",()=>{
   var s=Setup("race");Choose(s.Report,s.Plan,"both");var next=Plan(s.Report,s.Root);var chosen=next.Items.Single();
   File.WriteAllText(chosen.Destination!,"new occupant");var refreshed=Plan(s.Report,s.Root);
   Check(refreshed.Items.Single().Destination==chosen.Destination&&refreshed.Items.Single().DestinationConflict);
   bool rejected=false;try{MoveExecutor.ApplyAsync(s.Report,next,[chosen.Source],catalog,new Progress(),default).GetAwaiter().GetResult();}catch(InvalidOperationException){rejected=true;}
   Check(rejected&&File.ReadAllText(chosen.Destination!)=="new occupant"&&File.Exists(chosen.Source));
  });
  test("Renames within a nested destination root execute without a folder-overlap rejection",()=>{
   var s=Setup("nested");var plan=PreviewPlanner.Create(Data.Read(s.Report),s.Root,"Show",catalog,destinationRoot:Path.Combine(s.Root,"Formatted"));
   var item=plan.Items.Single();MoveExecutor.ApplyAsync(s.Report,plan,[item.Source],catalog,new Progress(),default).GetAwaiter().GetResult();
   Check(File.Exists(item.Destination)&&!File.Exists(item.Source));
  });
  test("A destination choice expires when naming settings or the source fingerprint change",()=>{
   var s=Setup("expire");Choose(s.Report,s.Plan,"both");var report=Data.Read(s.Report);
   Check(PreviewPlanner.Create(report,s.Root,"Renamed",catalog).Items.Single().DestinationChoice=="");
   report.Array("files")[0]!["primary_fingerprint"]!["sha256"]=new string('b',64);Data.Save(s.Report,report);
   Check(Plan(s.Report,s.Root).Items.Single().DestinationChoice=="");
  });
  test("Runtime and median size flag possible combined recordings without assigning episodes",()=>{
   var rows=new JsonArray();var root=Path.Combine(temp,"length-hints");
   for(int i=0;i<7;i++)rows.Add(new JsonObject{["path"]=Path.Combine(root,$"{i}.mkv"),["duration"]=i==6?2640:1320,["size"]=i==6?2000000:1000000,
    ["candidates"]=i==6?new JsonArray():new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(new Episode(i+100,1,i+1,"Story "+i),Data.Options)})});
   var report=new JsonObject{["files"]=rows};var hints=RecordingLengthHints.ForReport(report);var last=rows[6]!.AsObject();
   Check(hints[last.Text("path")].Contains("about 2")&&last.Array("candidates").Count==0);
   last["duration"]=1320;Check(!RecordingLengthHints.ForReport(report).ContainsKey(last.Text("path")),"Size alone must not contradict a normal runtime.");
   last.Remove("duration");Check(RecordingLengthHints.ForReport(report)[last.Text("path")].Contains("weak clue"));
   rows.RemoveAt(0);rows.RemoveAt(0);Check(RecordingLengthHints.ForReport(report).Count==0,"Small samples must not create estimates.");
  });
 }
}
