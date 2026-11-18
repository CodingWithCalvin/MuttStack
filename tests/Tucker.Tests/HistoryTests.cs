using Tucker;
using Doggo;
using System.Text.Json;
static class HistoryTests
{
    public static async Task<int> Run(string temp,string fake)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        bool Reject(Action action){try{action();return false;}catch(IOException){return true;}}
        var root=Path.Combine(temp,"history");Directory.CreateDirectory(root);
        var preset=Path.Combine(root,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"History","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var options=Presets.Snapshot(preset,Presets.Load(preset).Single(),fake,Path.Combine(root,"out"),Path.Combine(root,"presets"),"software");
        Job Source(string label,string? series=null,string season=""){
            var folder=Path.Combine(root,"raw",label);Directory.CreateDirectory(folder);var path=Path.Combine(folder,"title.mkv");File.WriteAllText(path,"raw "+label);var f=new FileInfo(path);
            return new(){Source=path,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,Name=label+".mkv",DiscLabel=label,Series=series==null?null:new("tvdb",series,series,"2020",""),Season=season,Encoding=options};
        }
        var a=Source("Alpha_S1_D1","Alpha","1");var b=Source("Alpha_S2_D1","Alpha","2");
        Check(RunNames.Suggest([],[],[])==""&&RunNames.Suggest([],[Path.GetDirectoryName(a.Source)!],[])=="Alpha S1 D1","Names appear from folder selection without requiring a preview");
        Check(RunNames.Suggest([a],[],[])=="Alpha · Season 1 · 1 file"&&RunNames.Suggest([b,a],[],[])=="Alpha · Seasons 1, 2 · 2 files","Preview names use series, sorted seasons and actual unique file count");
        var c=Source("Beta","Beta","1");var d=Source("Gamma","Gamma","1");
        Check(RunNames.Suggest([a,c,d],[],[])=="Alpha + Beta + 1 more · 3 files"&&RunNames.Unique("Alpha",["ALPHA","Alpha (2)"])=="Alpha (3)","Mixed series names remain concise and repeated suggested names get unique suffixes");
        var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=a.Source,SourceSize=a.SourceSize,SourceModified=a.SourceModified,Series=a.Series,Season=a.Season,DiscLabel=a.DiscLabel}]};
        var batchPath=Path.Combine(root,"batch.json");QueueStore.Write(batchPath,manifest);var batch=Handoff.Read(batchPath);
        Check(RunNames.Suggest([],[],[batch,batch])=="Alpha · Season 1 · 1 file","Inbox selections name the run immediately and overlapping batches count each file once");
        var m=new QueueManager(Path.Combine(root,"state"));m.Database.Freeze(options);m.Database.Settings(new{Label="keep preferences"});m.Database.Offer(batch);
        var first=m.AddRun([a],false,[batch],"");var second=m.AddRun([b],false,[],"Second");
        Check(m.State.Jobs[0].QueueName=="Alpha · Season 1 · 1 file","Saving without a typed name derives the run name from selected media");
        // Simulate the deployed v1 schema, preserving all existing data and shared tables.
        using(var sql=new Sqlite(m.Database.Path)){sql.Exec("DROP TABLE tucker_archived_runs");sql.Exec("DELETE FROM doggo_migrations WHERE component='tucker' AND version=2");}
        m=new QueueManager(Path.Combine(root,"state"));
        Check(m.State.Jobs.Count==2&&m.State.Receipts.Single().Id==batch.Manifest.Id&&m.Database.Settings<JsonElement>().GetProperty("Label").GetString()=="keep preferences"&&m.Database.Inbox().Single().Status=="Accepted","Tucker v1 migration preserves runs, preferences and handoff acknowledgements");
        using(var sql=new Sqlite(m.Database.Path)){Check(sql.Query("SELECT MAX(version) FROM doggo_migrations WHERE component='tucker'")[0][0]=="2"&&sql.Query("SELECT MAX(version) FROM doggo_migrations WHERE component='handoff'")[0][0]=="1","Archive storage advances only Tucker's own schema");}
        m.SetArchived(first.Id,true);var reopened=new QueueManager(Path.Combine(root,"state"));
        Check(reopened.IsArchived(first.Id)&&reopened.State.Jobs.Count==2&&!reopened.Running,"Archive status and full run contents survive reopening without starting work");
        Check(Reject(()=>m.Start(first.Id))&&Reject(()=>m.AppendRun(first.Id,[c],false,[])),"An archived run cannot start or receive additions before restoration");
        await m.Start(null);
        Check(m.State.Jobs.Single(j=>j.QueueId==first.Id).Status=="Waiting"&&m.State.Jobs.Single(j=>j.QueueId==second.Id).Status=="Completed","Start all skips archived waiting work");
        m.SetArchived(first.Id,false);Check(!m.IsArchived(first.Id)&&!m.Running&&m.State.Jobs[0].Status=="Waiting","Restoring a run keeps it waiting for an explicit Start");
        await m.Start(first.Id);var completed=m.State.Jobs[0];m.SetArchived(first.Id,true);m.DeleteRun(first.Id);
        reopened=new QueueManager(Path.Combine(root,"state"));
        Check(reopened.State.Jobs.Count==1&&!reopened.IsArchived(first.Id)&&File.Exists(completed.Source)&&File.Exists(completed.OutputPath)&&File.Exists(completed.OutputPath+".ripley.json"),"Deleting an archived completed run removes only its saved run and preserves source, output and metadata");
        Check(reopened.State.Receipts.Single().Id==batch.Manifest.Id&&reopened.Database.Inbox().Single().Status=="Accepted"&&reopened.AddRun([a],false,[],"Again").Files==0,"Deletion retains acknowledgements and the import ledger, preventing duplicate work");
        // Force persistence failure to verify archive/delete state is not adopted before commit.
        using(var sql=new Sqlite(m.Database.Path))sql.Exec("CREATE TRIGGER fail_history BEFORE DELETE ON tucker_jobs BEGIN SELECT RAISE(ABORT,'history test'); END");
        Check(Reject(()=>m.SetArchived(second.Id,true))&&!m.IsArchived(second.Id)&&Reject(()=>m.DeleteRun(second.Id))&&m.State.Jobs.Count==1&&m.Database.Read().Jobs.Count==1,"Failed history changes roll back both database and in-memory state");
        using(var sql=new Sqlite(m.Database.Path))sql.Exec("DROP TRIGGER fail_history");
        var active=new QueueManager(Path.Combine(root,"active"));active.Database.Freeze(options);
        try{
            var run=active.AddRun([c,d],false,[],"Active");Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");
            var task=active.Start(run.Id);var current=active.Current!;
            var idle=active.AddRun([Source("idle")],false,[],"Idle");var other=active.AddRun([Source("delete-idle")],false,[],"Delete idle");
            Check(!active.CanManageRun(run.Id)&&Reject(()=>active.SetArchived(run.Id,true))&&Reject(()=>active.DeleteRun(run.Id)),"Active and scheduled files protect their run from archive or deletion");
            active.SetArchived(idle.Id,true);active.DeleteRun(other.Id);
            Check(ReferenceEquals(current,active.Current)&&ReferenceEquals(current,active.State.Jobs[0])&&active.Running,"Archiving or deleting unrelated waiting runs preserves the active encode object");
            await task;Check(active.State.Jobs.Where(j=>j.QueueId==run.Id).All(j=>j.Status=="Completed")&&active.State.Jobs.Single(j=>j.QueueId==idle.Id).Status=="Waiting","Processing finishes normally after managing unrelated history");
        }finally{if(active.Running){active.Stop();await active.RunTask!;}Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
        return passed;
    }
}
