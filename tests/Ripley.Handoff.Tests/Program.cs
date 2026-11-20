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
 // Assert the public database contract without loading the recipient's implementation.
 using(var sql=new Sqlite(db)) {
  var item=sql.Query("SELECT id,digest,payload,status FROM handoff_batches").Single();
  var incoming=JsonNode.Parse(item[2])!;var file=incoming["Files"]![0]!;
  Check(item[0]==handoffId&&item[1]==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original))&&item[3]=="Offered","Producer publishes a stable ID, byte digest and Offered status through SQLite");
  Check(incoming["Version"]!.GetValue<int>()==1&&incoming["SourceApp"]!.ToString()=="ripley"&&incoming["TargetApp"]!.ToString()=="tucker","Manifest declares the versioned Ripley to Tucker contract");
  Check(file["Season"]!.ToString()=="2"&&file["VolumeNumber"]!.ToString()=="3"&&file["DiscNumber"]!.ToString()=="4"&&file["Series"]!["Id"]!.ToString()=="42","Manifest preserves authoritative per-file metadata");
  Check(file["Source"]!.ToString()==raw&&file["SourceSize"]!.GetValue<long>()==stat.Length&&file["SourceModified"]!.GetValue<DateTime>()==stat.LastWriteTimeUtc,"Manifest preserves the published source name and fingerprint");
  Check(sql.Query("SELECT id FROM handoff_events WHERE kind='Offered'").Count==1&&sql.Query("SELECT name FROM sqlite_master WHERE name='tucker_jobs'").Count==0,"Producer retries create one offer and never create a recipient queue");
  // A recipient process commits this response; only the database boundary is simulated here.
  sql.Exec("BEGIN IMMEDIATE");sql.Exec("UPDATE handoff_batches SET status='Accepted',accepted_utc=? WHERE id=?",DateTime.UtcNow.ToString("O"),handoffId);
  sql.Exec("INSERT INTO handoff_events(batch_id,kind,created_utc) VALUES(?,'Accepted',?)",handoffId,DateTime.UtcNow.ToString("O"));sql.Exec("COMMIT");
 }
 Check(new DoggoInbox(db).Status(handoffId)=="Accepted","Reopened producer observes acceptance committed by a separate database client");
 TuckerHandoff.Deliver(outbox,handoffId,db);
 using(var sql=new Sqlite(db))Check(inbox.Status(handoffId)=="Accepted"&&sql.Query("SELECT id FROM handoff_events").Count==2,"Retrying an accepted request never reopens or duplicates the database handoff");
 var output=Path.Combine(root,"encoded");Directory.CreateDirectory(output);
 var locked=(JsonObject)run.DeepClone();locked["Id"]=Guid.NewGuid().ToString();locked["QueuedAtUtc"]=DateTime.UtcNow;bool rejected=false;try{TuckerHandoff.Prepare(outbox,locked);}catch(IOException){rejected=true;}Check(rejected,"Previously queued Ripley run cannot be handed off again");
 var changed=(JsonObject)run.DeepClone();changed["Id"]=Guid.NewGuid().ToString();File.AppendAllText(raw,"changed");rejected=false;try{TuckerHandoff.Prepare(outbox,changed);}catch(IOException){rejected=true;}Check(rejected,"Changed raw file blocks handoff before delivery");File.WriteAllText(raw,"raw content");File.SetLastWriteTimeUtc(raw,stat.LastWriteTimeUtc);
 var history=Path.Combine(root,"history.json");QueueStore.Write(history,new{Selected=id,Sessions=new[]{JsonNode.Parse(run.ToJsonString())}});
 var video=Path.Combine(output,"finished.mkv");File.WriteAllBytes(video,new byte[]{0x1a,0x45,0xdf,0xa3,0,0,0,0,0,0,0,0,0,0,0,0,0});QueueStore.Write(video+".ripley.json",new{version=2,source=raw,disc_metadata=metadata,output=video});
 var report=CompletionReconciler.Inspect(history,output);Check(report.Completed.Count==1&&report.Issues.Count==0,"Reconciliation checks completion evidence and source signature");
 var reconcileDb=Path.Combine(root,"reconciled.db");var historyBefore=File.ReadAllBytes(history);CompletionReconciler.Apply(history,reconcileDb,report,report.HistorySha256);
 var saved=JsonNode.Parse(File.ReadAllBytes(history))!;Check(saved["Sessions"]![0]!["CompressionOutcome"]!.ToString()=="Completed"&&saved["Sessions"]![0]!["Files"]![0]!["CompletedOutput"]!.ToString()==video,"Reconciliation annotates history with the verified completed output");
 Check(new DoggoInbox(reconcileDb).CompletedSource(report.Completed[0].SourceKey),"Verified completion is visible through the database source-signature contract");
 File.WriteAllBytes(history,historyBefore);CompletionReconciler.Apply(history,reconcileDb,report,report.HistorySha256);Check(new DoggoInbox(reconcileDb).CompletedSource(report.Completed[0].SourceKey),"Reconciliation retries safely after DB commit before history annotation");
 File.Delete(video);var missing=CompletionReconciler.Inspect(history,output);Check(missing.Issues.Count==1&&missing.Completed.Count==0,"Missing output is held for review rather than marked complete");
 rejected=false;try{CompletionReconciler.Apply(history,reconcileDb,missing,missing.HistorySha256);}catch(IOException){rejected=true;}Check(rejected,"Incomplete reconciliation cannot publish success");
 Console.WriteLine($"{passed} handoff checks passed");
}finally{Directory.Delete(root,true);}
