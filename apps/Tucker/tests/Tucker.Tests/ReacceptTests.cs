using Doggo;
using Tucker;
using System.Text.Json;

static class ReacceptTests
{
    public static async Task<int> Run(string temp,string fake)
    {
        int passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        bool Reject(Action action){try{action();return false;}catch(IOException){return true;}}
        var root=Path.Combine(temp,"reaccept");Directory.CreateDirectory(root);
        var preset=Path.Combine(root,"preset.json");
        File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Reaccept test","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var options=Presets.Snapshot(preset,Presets.Load(preset).Single(),fake,Path.Combine(root,"out"),Path.Combine(root,"presets"),"software");
        var state=Path.Combine(root,"state");var m=new QueueManager(state);m.Database.Freeze(options);
        Handoff Batch(params string[] names)
        {
            var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D")};
            foreach(var name in names) {
                var path=Path.Combine(root,name+".mkv");File.WriteAllText(path,"raw "+name);var f=new FileInfo(path);
                manifest.Files.Add(new(){Source=f.FullName,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,DiscLabel="Test disc",Season="2"});
            }
            var json=Path.Combine(root,manifest.Id+".json");QueueStore.Write(json,manifest);
            var batch=Handoff.Read(json);m.Database.Offer(batch);return batch;
        }
        List<Job> Plan(params Handoff[] batches)=>RunPlanner.Plan([],batches,options,true,"All","","{filename}",m.State.Jobs);
        string AcceptanceSnapshot()
        {
            using var sql=new Sqlite(m.Database.Path);
            return JsonSerializer.Serialize(new[]{sql.Query("SELECT * FROM tucker_receipts ORDER BY id"),sql.Query("SELECT * FROM handoff_batches ORDER BY id"),sql.Query("SELECT * FROM handoff_events ORDER BY id")});
        }
        var batch=Batch("first","second");var again=batch with{Reaccept=true};
        Check(Reject(()=>m.AddRun(Plan(again),false,[again],"Invalid"))&&m.Database.PendingInboxCount()==1,"Re-accept requires an existing accepted batch and receipt");
        var original=m.AddRun(Plan(batch),false,[batch],"Original");var receipt=m.State.Receipts.Single();var accepted=AcceptanceSnapshot();
        Check(m.AddRun(Plan(again),false,[again],"Duplicate").Files==0&&m.State.Jobs.Count==2,"Re-accept skips unfinished jobs and does not create an empty duplicate run");
        foreach(var status in new[]{"Waiting","Paused","Failed","Needs review","Encoding","Publishing"}) {
            m.State.Jobs[0].Status=status;
            Check(m.ImportStatus(Plan(again)[0],QueueManager.ReacceptedSourceKeys([again]))=="Already queued"&&m.AddRun(Plan(again),false,[again],"Blocked").Files==0,"Re-accept protects existing "+status+" work");
        }
        m.State.Jobs[0].Status="Waiting";m.SetArchived(original.Id,true);
        Check(m.AddRun(Plan(again),false,[again],"Archived duplicate").Files==0,"Archived unfinished work is protected from duplicate re-acceptance");
        m.DeleteRun(original.Id);m=new QueueManager(state);
        Check(m.AddRun(Plan(batch),false,[],"Folder retry").Files==0,"Normal imports still respect retained signatures after deletion");
        var restored=m.AddRun(Plan(again),false,[again],"Recovered");
        Check(restored.Files==2&&!m.Running&&m.State.Jobs.All(j=>j.Status=="Waiting")&&m.State.Receipts.Single()==receipt,"Explicit re-accept restores deleted work as waiting without replacing its receipt");
        Check(m.AddRun(Plan(again),false,[again],"Double click").Files==0&&AcceptanceSnapshot()==accepted,"Repeated saves leave jobs, shared acceptance timestamps and events unchanged");
        await m.Start(restored.Id);
        var outputs=m.State.Jobs.ToDictionary(j=>j.OutputPath,j=>File.ReadAllText(j.OutputPath));var oldIds=m.State.Jobs.Select(j=>j.Id).ToHashSet();
        var planned=Plan(again);var plannedPaths=planned.Select(j=>j.OutputPath).ToArray();
        Check(planned.All(j=>m.ImportStatus(j,QueueManager.ReacceptedSourceKeys([again]))=="Ready to reprocess")&&!plannedPaths.Any(outputs.ContainsKey),"Completed batch previews intentional reprocessing with collision-safe output names");
        var rerun=m.AddRun(planned,false,[again],"Again");await m.Start(rerun.Id);
        Check(rerun.Files==2&&m.State.Jobs.Where(j=>j.QueueId==rerun.Id).All(j=>j.Status=="Completed"&&!oldIds.Contains(j.Id))&&outputs.All(p=>File.ReadAllText(p.Key)==p.Value)&&plannedPaths.All(File.Exists),"Reprocessing publishes new files and job IDs while preserving completed outputs");
        Check(AcceptanceSnapshot()==accepted&&new QueueManager(state).State.Jobs.Count==4,"Reprocessing survives reopen without changing original acceptance records");

        var fresh=Batch("fresh");var mixedPlan=Plan(again,fresh);
        var mixed=m.AddRun(mixedPlan,false,[again,fresh],"Mixed");
        Check(mixed.Files==3&&m.State.Jobs.Count(j=>j.QueueId==mixed.Id)==3&&m.State.Receipts.Count==2&&m.State.Receipts.Single(r=>r.Id==batch.Manifest.Id)==receipt,"Accepted and offered batches combine in one run with one receipt per original batch");
        m.DeleteRun(mixed.Id);
        var appended=m.AppendRun(restored.Id,Plan(again),false,[again]);
        Check(appended==2&&m.State.Jobs.Count(j=>j.QueueId==restored.Id)==4&&!m.Running,"Re-accepted batches can append to an existing Tucker without auto-starting");
        m.DeleteRun(restored.Id);m.DeleteRun(rerun.Id);

        using(var sql=new Sqlite(m.Database.Path)) {
            sql.Exec("CREATE TABLE ripley_completed_sources(source_key TEXT PRIMARY KEY)");
            foreach(var job in Plan(again))sql.Exec("INSERT INTO ripley_completed_sources VALUES(?)",QueueManager.SourceKey(job));
        }
        var legacy=m.AddRun(Plan(again),false,[again],"Explicit legacy retry");
        Check(legacy.Files==2,"Explicit re-accept can reprocess sources in the historical Ripley completion ledger");
        m.DeleteRun(legacy.Id);
        var bad=batch with{Digest="changed",Reaccept=true};
        Check(Reject(()=>m.AddRun(Plan(bad),false,[bad],"Changed"))&&m.State.Jobs.Count==0,"Re-accept rejects changed batch contents without adding jobs");
        var partial=Plan(again);partial.RemoveAt(0);
        Check(Reject(()=>m.AddRun(partial,false,[again],"Partial")),"Re-accept still requires the whole batch to be previewed");
        var stale=Plan(again);File.AppendAllText(stale[0].Source,"changed after preview");
        Check(Reject(()=>m.AddRun(stale,false,[again],"Stale"))&&m.Database.Read().Jobs.Count==0,"Changed raw sources abort re-acceptance without saving partial work");
        File.WriteAllText(stale[0].Source,"raw first");File.SetLastWriteTimeUtc(stale[0].Source,stale[0].SourceModified);
        var offered=Batch("rollback");var combined=Plan(again,offered);
        using(var sql=new Sqlite(m.Database.Path))sql.Exec("UPDATE handoff_batches SET status='Declined' WHERE id=?",batch.Manifest.Id);
        Check(Reject(()=>m.AddRun(combined,false,[again,offered],"Rollback"))&&m.State.Jobs.Count==0&&m.Database.Read().Jobs.Count==0&&m.Database.Inbox().Single(i=>i.Id==offered.Manifest.Id).Status=="Offered","A stale re-accept rolls back mixed jobs and new acknowledgements atomically");
        using(var sql=new Sqlite(m.Database.Path))sql.Exec("UPDATE handoff_batches SET status='Accepted' WHERE id=?",batch.Manifest.Id);

        var running=m.AddRun(Plan(again),false,[again],"Running");
        try {
            Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-wait");
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            m.Line+=line=>{if(line.Contains("1.00"))started.TrySetResult();};
            var task=m.Start(running.Id);await started.Task.WaitAsync(TimeSpan.FromSeconds(15));var current=m.Current;
            Check(m.AppendRun(running.Id,Plan(again),true,[again])==0&&ReferenceEquals(current,m.Current)&&m.State.Jobs.Count==2,"Re-accept during processing preserves the current encode and skips scheduled duplicates");
            m.Stop();await task;
        }finally{if(m.Running){m.Stop();await m.RunTask!;}Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
        m.DeleteRun(running.Id);
        var folderPlan=Plan(batch);
        Check(folderPlan.All(j=>m.ImportStatus(j,QueueManager.ReacceptedSourceKeys([]))=="Already imported")&&m.AddRun(folderPlan,false,[],"Ordinary import").Files==0,"Folder import history remains distinct from explicit file reprocessing");
        folderPlan[0].Reprocess=true;
        var previousOutput=Path.Combine(folderPlan[0].Encoding!.Output,"first.mkv");var originalOutput=File.ReadAllText(previousOutput);
        var fileRun=m.AddRun([folderPlan[0],folderPlan[0],folderPlan[1]],false,[],"Selected files");
        Check(fileRun.Files==1&&m.State.Jobs.Single().Source==folderPlan[0].Source&&m.State.Jobs.Single().OutputPath!=previousOutput&&File.ReadAllText(previousOutput)==originalOutput,"Only selected folder sources bypass history, with overlap deduplication and output collision protection");
        Check(!m.State.Jobs.Single().Reprocess&&!JsonSerializer.Serialize(folderPlan[0]).Contains("\"Reprocess\"")&&!new QueueManager(state).State.Jobs.Single().Reprocess,"Per-file reprocessing permission is never serialized or restored as persistent import permission");
        Check(m.ImportStatus(folderPlan[0],QueueManager.ReacceptedSourceKeys([]))=="Already queued"&&m.AddRun(folderPlan,false,[],"Queued duplicate").Files==0,"Explicit file reprocessing cannot duplicate an unfinished job");
        m.SetArchived(fileRun.Id,true);
        Check(m.AddRun(folderPlan,false,[],"Archived duplicate").Files==0,"Explicit file reprocessing also protects archived unfinished jobs");
        m.SetArchived(fileRun.Id,false);
        folderPlan[1].Reprocess=true;
        Check(m.AppendRun(fileRun.Id,folderPlan,false,[])==1&&m.State.Jobs.Count==2&&!m.Running,"Selected folder reprocessing can append while skipping sources already in the target run");
        m.DeleteRun(fileRun.Id);folderPlan=Plan(batch);folderPlan[0].Reprocess=true;
        File.AppendAllText(folderPlan[0].Source,"stale file");
        Check(Reject(()=>m.AddRun(folderPlan,false,[],"Stale file retry"))&&m.State.Jobs.Count==0&&m.Database.Read().Jobs.Count==0,"File reprocessing validates source fingerprints before committing any work");
        return passed;
    }
}
