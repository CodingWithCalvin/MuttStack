using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Doggo;
using Ripley;
var root=Path.Combine(Path.GetTempPath(),"DoggoHandoffTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);int passed=0;
void Check(bool yes,string label){if(!yes)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
try {
 passed+=CredentialTests.Run(root);
 var raw=Path.Combine(root,"raw.mkv");File.WriteAllText(raw,"raw content");var stat=new FileInfo(raw);var id=Guid.NewGuid().ToString("N");
 var metadata=Path.Combine(root,"ripley-disc.json");QueueStore.Write(metadata,new{status="Ripped",disc_label="Show S2 V3 D4",season="2",volume_number="3",disc_number="4",rip_date=DateTime.UtcNow,series_identity=new SeriesIdentity("tvdb","42","Show","2026","")});
 var run=JsonSerializer.SerializeToNode(new {Id=id,Status="Completed",Created=DateTime.Now,Season="99",Volume="99",DiscNumber="99",DiscLabel="WRONG",Files=new[]{new Job{Source=raw,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,DiscMetadata=metadata}}})!.AsObject();
 var db=Path.Combine(root,"doggo.db");var outbox=Path.Combine(root,"state");
 var handoffId=TuckerHandoff.Prepare(outbox,run);var path=TuckerHandoff.ManifestPath(outbox,id);var original=File.ReadAllBytes(path);
 Check(!File.Exists(db)&&File.Exists(path),"Preparing handoff freezes a durable outbox without delivering or acknowledging");
 run["Season"]="changed";Check(TuckerHandoff.Prepare(outbox,run)==handoffId&&File.ReadAllBytes(path).SequenceEqual(original),"Retry keeps exact immutable manifest and ID");
 TuckerHandoff.Deliver(outbox,handoffId,db);TuckerHandoff.Deliver(outbox,handoffId,db);
 var inbox=new DoggoInbox(db);Check(inbox.Status(handoffId)=="Offered","Repeated delivery remains Offered and never implies acceptance");
 // Receive through Tucker's real database API, not by importing the producer's local JSON file.
 var incomingItem=new Tucker.DoggoDatabase(db).Inbox().Single();
 var incoming=new Tucker.Handoff(JsonSerializer.Deserialize<Tucker.HandoffManifest>(incomingItem.Payload)!,incomingItem.Digest);
 Check(incomingItem.Status=="Offered"&&incoming.Manifest.Id==handoffId&&incoming.Digest==Tucker.Handoff.Read(path).Digest,"Tucker sees the complete Ripley request directly in the shared database inbox");
 using(var sql=new Sqlite(db))Check(sql.Query("SELECT id FROM handoff_events WHERE kind='Offered'").Count==1&&sql.Query("SELECT id FROM tucker_jobs").Count==0,"Retry creates one inbox event and delivery alone creates no compression jobs");
 var output=Path.Combine(root,"encoded");Directory.CreateDirectory(output);
 var options=new Tucker.Options("fake.exe","preset","",false,output,".mkv");var planned=incoming.Plan(options,@"Season {season:00}\Volume {volume_number}\Disc {disc_number}","");
 Check(planned.Single().Season=="2"&&planned[0].VolumeNumber=="3"&&planned[0].DiscNumber=="4"&&planned[0].Series?.Id=="42","Receiver uses file's saved disc metadata, not currently selected fields");
 Check(incoming.Plan(options,"All", "DO_NOT_ADD_").Single().Name=="raw.mkv","Ripley handoff preserves published raw prefix without adding Tucker prefix twice");
 var receiver=new Tucker.QueueManager(Path.Combine(root,"tucker"),db);receiver.Add(planned,false,incoming);
 Check(!receiver.Running&&receiver.State.Jobs.Single().Status=="Waiting"&&inbox.Status(handoffId)=="Accepted","Tucker commits waiting queue and acknowledgement atomically");
 Check(new DoggoInbox(db).Status(handoffId)=="Accepted"&&new Tucker.QueueManager(Path.Combine(root,"reopen"),db).State.Jobs.Count==1,"Producer can observe acceptance after both apps reopen");
 Check(receiver.Add(planned,false,incoming)==0,"Duplicate acceptance never queues another job");
 TuckerHandoff.Deliver(outbox,handoffId,db);
 Check(new Tucker.DoggoDatabase(db).PendingInboxCount()==0&&inbox.Status(handoffId)=="Accepted"&&receiver.Database.Read().Jobs.Count==1,"Retrying an accepted request never reopens the inbox or duplicates encoding work");
 var locked=(JsonObject)run.DeepClone();locked["Id"]=Guid.NewGuid().ToString();locked["QueuedAtUtc"]=DateTime.UtcNow;bool rejected=false;try{TuckerHandoff.Prepare(outbox,locked);}catch(IOException){rejected=true;}Check(rejected,"Previously queued Ripley run cannot be handed off again");
 var changed=(JsonObject)run.DeepClone();changed["Id"]=Guid.NewGuid().ToString();File.AppendAllText(raw,"changed");rejected=false;try{TuckerHandoff.Prepare(outbox,changed);}catch(IOException){rejected=true;}Check(rejected,"Changed raw file blocks handoff before delivery");File.WriteAllText(raw,"raw content");File.SetLastWriteTimeUtc(raw,stat.LastWriteTimeUtc);
 var history=Path.Combine(root,"history.json");QueueStore.Write(history,new{Selected=id,Sessions=new[]{JsonNode.Parse(run.ToJsonString())}});
 var video=Path.Combine(output,"finished.mkv");File.WriteAllBytes(video,new byte[]{0x1a,0x45,0xdf,0xa3,0,0,0,0,0,0,0,0,0,0,0,0,0});QueueStore.Write(video+".ripley.json",new{version=2,source=raw,disc_metadata=metadata,output=video});
 var report=CompletionReconciler.Inspect(history,output);Check(report.Completed.Count==1&&report.Issues.Count==0,"Reconciliation checks completion evidence and source signature");
 var reconcileDb=Path.Combine(root,"reconciled.db");var historyBefore=File.ReadAllBytes(history);CompletionReconciler.Apply(history,reconcileDb,report,report.HistorySha256);
 var saved=JsonNode.Parse(File.ReadAllBytes(history))!;Check(saved["Sessions"]![0]!["CompressionOutcome"]!.ToString()=="Completed"&&saved["Sessions"]![0]!["Files"]![0]!["CompletedOutput"]!.ToString()==video,"Reconciliation annotates history with the verified completed output");
 var completedReceiver=new Tucker.QueueManager(Path.Combine(root,"receiver2"),reconcileDb);Check(completedReceiver.Add(planned,false)==0&&completedReceiver.State.Jobs.Count==0,"Tucker rejects already-compressed sources using Ripley's completion ledger");
 File.WriteAllBytes(history,historyBefore);CompletionReconciler.Apply(history,reconcileDb,report,report.HistorySha256);Check(new DoggoInbox(reconcileDb).CompletedSource(report.Completed[0].SourceKey),"Reconciliation retries safely after DB commit before history annotation");
 File.Delete(video);var missing=CompletionReconciler.Inspect(history,output);Check(missing.Issues.Count==1&&missing.Completed.Count==0,"Missing output is held for review rather than marked complete");
 rejected=false;try{CompletionReconciler.Apply(history,reconcileDb,missing,missing.HistorySha256);}catch(IOException){rejected=true;}Check(rejected,"Incomplete reconciliation cannot publish success");
 Console.WriteLine($"{passed} handoff checks passed");
}finally{Directory.Delete(root,true);}
