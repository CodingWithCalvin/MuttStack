using Doggo;
using Tucker;
using System.Text.Json;
using System.Text.Json.Nodes;
static class QueueTests
{
 public static async Task<int> Run(string temp,string fake) {
  int passed=RunTests.Run(temp,fake)+await AppendTests.Run(temp,fake)+await HistoryTests.Run(temp,fake);void Check(bool yes,string label){if(!yes)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
  var root=Path.Combine(temp,"queues");Directory.CreateDirectory(root);var manager=new QueueManager(root);
  var presetFile=Path.Combine(root,"gui.json");File.WriteAllText(presetFile,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Custom","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit","VideoPreset":"slow","VideoQualitySlider":18}]}""");
  var options=Presets.Snapshot(presetFile,Presets.Load(presetFile).Single(),fake,Path.Combine(root,"out"),Path.Combine(root,"presets"),"nvdec");manager.Database.Freeze(options);
  Job Job(string name,string queue="one") {var source=Path.Combine(root,name+".mkv");File.WriteAllText(source,"raw "+name);var stat=new FileInfo(source);return new(){Source=source,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,Name=name+".mkv",QueueId=queue,QueueName=queue,Encoding=options};}
  var a=Job("a");var b=Job("b","two");var c=Job("c");
  Check(manager.Add([a,b,c],false)==3&&!manager.Running&&manager.State.Jobs.All(j=>j.Status=="Waiting"),"Adding idle work saves it without launching");
  File.WriteAllText(options.Json,"changed");manager.Database.Materialize(options);Check(File.ReadAllText(options.Json).Contains("slow")&&File.ReadAllText(options.Json).Contains("x265_10bit"),"Database preserves exact preset independent of scratch snapshot changes");
  var order=new List<string>();string last="";manager.Changed+=()=>{if(manager.Current is {} j&&j.Id!=last){last=j.Id;order.Add(j.Name);}};
  await manager.Start(null);Check(order.SequenceEqual(new[]{"a.mkv","c.mkv","b.mkv"}),"Start all finishes each queue before the next");
  Check(manager.State.Jobs.All(j=>j.Status=="Completed")&&File.ReadAllText(a.Source)=="raw a", "Queue processing preserves raw sources and completes fake outputs");
  Check(new QueueManager(root).State.Jobs.All(j=>j.Status=="Completed"),"Completed queue survives reopen");
  manager.Clear(null);Check(manager.Add([a],false)==0&&File.Exists(Path.Combine(options.Output,"a.mkv")),"Clearing preserves outputs and duplicate-import ledger");
  var d=Job("d");var e=Job("e");manager.Add([d,e],false);
  bool pauseOnce=false;manager.Changed+=()=>{if(manager.Current?.Name=="d.mkv"&&!pauseOnce){pauseOnce=true;manager.TogglePause();}};
  await manager.Start(null);Check(manager.State.Jobs.Single(j=>j.Name=="d.mkv").Status=="Completed"&&manager.State.Jobs.Single(j=>j.Name=="e.mkv").Status=="Waiting", "Pause waits for current file to publish and leaves next file waiting");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");
  var running=manager.Start(null);manager.TogglePause();Check(manager.PausePending,"Pending pause is visible");manager.TogglePause();Check(!manager.PausePending,"Keep processing cancels pending pause");
  var f=Job("f");manager.Add([f],false);var g=Job("g");manager.Add([g],true);await running;
  Check(manager.State.Jobs.Single(j=>j.Name=="f.mkv").Status=="Waiting"&&manager.State.Jobs.Single(j=>j.Name=="g.mkv").Status=="Completed","Only explicit Add to Running Queue joins an active run");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-wait");
  var signaled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);void Signal(string line){if(line.Contains("1.00"))signaled.TrySetResult();}manager.Line+=Signal;
  running=manager.Start(null);await signaled.Task.WaitAsync(TimeSpan.FromSeconds(15));manager.Stop();await running;manager.Line-=Signal;
  Check(manager.State.Jobs.Single(j=>j.Name=="f.mkv").Status=="Paused"&&!File.Exists(Path.Combine(options.Output,"f.mkv"))&&!Directory.GetDirectories(options.Output,".tucker-*").Any(),"Stop removes partial encode and retains resumable job");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-fail");await manager.Start(null);
  Check(manager.State.Jobs.Single(j=>j.Name=="f.mkv").Status=="Failed"&&!Directory.GetDirectories(options.Output,".tucker-*").Any(),"Failed encoder cleans scratch and records failure");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
  manager.State.Jobs[0].Status="Encoding";manager.State.Jobs[1].Status="Publishing";manager.Save();var reopened=new QueueManager(root);
  Check(!reopened.Running&&reopened.State.Jobs[0].Status=="Paused"&&reopened.State.Jobs[1].Status=="Needs review","Restart never auto-runs and ambiguous publication requires review");
  var h=Job("handoff");var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString(),Files=[new(){Source=h.Source,SourceSize=h.SourceSize,SourceModified=h.SourceModified,DiscId="disc-1",DiscLabel="Show S1 D1",Season="1",VolumeNumber="2",DiscNumber="1",Series=new("tvdb","123","Show","2026",""),RipDate=DateTime.Today}]};
  var manifestPath=Path.Combine(root,"handoff.json");File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest));var handoff=Handoff.Read(manifestPath);manager.Database.Offer(handoff);manager.Database.Offer(handoff);
  Check(manager.Database.Inbox().Count==1&&manager.Database.Inbox()[0].Status=="Offered","Duplicate delivery stays one offered inbox batch");
  var plan=handoff.Plan(options,@"{series}\Season {season:00}","");Check(plan[0].VolumeNumber=="2"&&plan[0].Encoding!.Output.EndsWith(@"Show\Season 01"),"Handoff retains per-file metadata in output planning");
  manager.Add(plan,false,handoff);var accepted=new QueueManager(root);
  Check(accepted.Database.Inbox()[0].Status=="Accepted"&&accepted.State.Receipts.Count==1&&accepted.State.Jobs.Any(j=>j.Source==h.Source),"Queue and acceptance are durable together across reopen");
  Check(accepted.Add(plan,false,handoff)==0,"Accepted handoff retry cannot create duplicate queue entries");
  bool rejected=false;try{accepted.Database.Offer(handoff with{Digest="changed"});}catch(IOException){rejected=true;}Check(rejected,"Same handoff ID with changed contents rejected");
  var bad=handoff with{Manifest=new(){Id=Guid.NewGuid().ToString(),Files=manifest.Files},Digest="new"};accepted.Database.Offer(bad);accepted.Database.Decline(bad.Manifest.Id);var before=accepted.State.Jobs.Count;
  rejected=false;try{accepted.Add([Job("rejected")],false,bad);}catch(IOException){rejected=true;}
  Check(rejected&&new QueueManager(root).State.Jobs.Count==before&&accepted.State.Jobs.Count==before,"Declined handoff rolls back queue insertion and acknowledgement atomically");
  using(var sql=new Sqlite(accepted.Database.Path)){sql.Exec("INSERT INTO doggo_migrations VALUES('scout',99,'test')");}
  Check(new QueueManager(root).State.Jobs.Count==before,"Newer unrelated app schema does not block Tucker");
  using(var sql=new Sqlite(accepted.Database.Path)){sql.Exec("INSERT INTO doggo_migrations VALUES('handoff',99,'test')");}
  rejected=false;try{new QueueManager(root);}catch(IOException){rejected=true;}Check(rejected,"Newer shared schema is rejected without changing data");
  var named=Job("literal{braces}");named.Season="3";Naming.Apply([named],"{{keep}}_{season:00}_{filename}");
  Check(named.Name=="{keep}_03_literal{braces}.mkv","Filename formats preserve escaped braces and do not reinterpret source names");
  rejected=false;try{Naming.Apply([Job("unsafe")],"../{filename}");}catch(IOException){rejected=true;}Check(rejected,"Filename formats cannot escape the output directory");
  return passed;
 }
}
