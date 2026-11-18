using Tucker;
using System.Text.Json;
static class AppendTests
{
    public static async Task<int> Run(string temp,string fake)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var root=Path.Combine(temp,"append-runs");Directory.CreateDirectory(root);
        var preset=Path.Combine(root,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Append test","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var options=Presets.Snapshot(preset,Presets.Load(preset).Single(),fake,Path.Combine(root,"out"),Path.Combine(root,"presets"),"software");
        Job Source(string name){var folder=Path.Combine(root,"raw",name);Directory.CreateDirectory(folder);var source=Path.Combine(folder,"title.mkv");File.WriteAllText(source,"raw "+name);var f=new FileInfo(source);return new(){Source=source,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,Name="title.mkv",DiscLabel=name,PresetDisplay="Append test",Encoding=options with{Output=Path.Combine(options.Output,"All")}};}
        Handoff Offer(QueueManager m,Job job){var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=job.Source,SourceSize=job.SourceSize,SourceModified=job.SourceModified,DiscLabel=job.DiscLabel,DiscId=job.DiscLabel,RipDate=DateTime.Now}]};var file=Path.Combine(root,manifest.Id+".json");QueueStore.Write(file,manifest);var h=Handoff.Read(file);m.Database.Offer(h);return h;}
        var m=new QueueManager(Path.Combine(root,"completed"));m.Database.Freeze(options);
        var seed=Source("seed");var run=m.AddRun([seed],false,[],"Saved run");await m.Start(run.Id);
        var original=m.State.Jobs.Single();var frozen=JsonSerializer.Serialize(original);
        var folder=Source("folder-addition");var fromInbox=Source("inbox-addition");var batch=Offer(m,fromInbox);
        var plan=RunPlanner.Plan([Path.GetDirectoryName(folder.Source)!],[batch],options,true,"All","","{filename}",m.State.Jobs);
        var expected=plan.Select(j=>j.OutputPath).ToArray();var count=m.AppendRun(run.Id,plan,false,[batch]);
        Check(count==2&&m.State.Jobs.Count==3&&m.State.Jobs.All(j=>j.QueueId==run.Id&&j.QueueName=="Saved run")&&!m.Running,"Folders and inbox batches append to a completed Tucker without creating or starting a new run");
        Check(ReferenceEquals(original,m.State.Jobs[0])&&JsonSerializer.Serialize(original)==frozen&&File.Exists(original.OutputPath),"Appending preserves completed file records, frozen settings and media");
        Check(m.State.Jobs.Skip(1).Select(j=>j.OutputPath).SequenceEqual(expected)&&expected.Distinct().Count()==2,"Append preview reserves existing output names and matches saved paths");
        Check(m.AppendRun(run.Id,[seed],false,[])==0&&m.State.Jobs.Count==3,"Appending an already-imported source cannot duplicate the run's files");
        var reopened=new QueueManager(Path.Combine(root,"completed"));Check(reopened.State.Jobs.All(j=>j.QueueId==run.Id)&&reopened.State.Receipts.Single().Id==batch.Manifest.Id,"Append membership and inbox acceptance survive reopening");
        var missing=Offer(m,Source("missing-target"));var before=m.State.Jobs.Count;bool rejected=false;
        try{m.AppendRun("removed-run",missing.Plan(options,"All",""),false,[missing]);}catch(IOException){rejected=true;}
        Check(rejected&&m.State.Jobs.Count==before&&m.Database.Inbox().Single(i=>i.Id==missing.Manifest.Id).Status=="Offered","A missing target cannot create a replacement run or accept its staged batch");
        var good=Offer(m,Source("rollback-good"));var bad=Offer(m,Source("rollback-declined"));m.Database.Decline(bad.Manifest.Id);
        rejected=false;try{m.AppendRun(run.Id,RunPlanner.Plan([],[good,bad],options,true,"All","","{filename}"),false,[good,bad]);}catch(IOException){rejected=true;}
        Check(rejected&&m.State.Jobs.Count==before&&m.Database.Read().Jobs.Count==before&&m.Database.Inbox().Single(i=>i.Id==good.Manifest.Id).Status=="Offered"&&ReferenceEquals(original,m.State.Jobs[0]),"A failed append rolls back all additions and acknowledgements while keeping the existing run intact");
        var active=new QueueManager(Path.Combine(root,"active"));active.Database.Freeze(options);
        try {
            var activeRun=active.AddRun([Source("active")],false,[],"Active run");
            Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");
            var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);void OnLine(string line){if(line.Contains("1.00"))signal.TrySetResult();}active.Line+=OnLine;
            var task=active.Start(activeRun.Id);await signal.Task.WaitAsync(TimeSpan.FromSeconds(15));var encoding=active.Current!;
            var waitFolder=Source("wait-folder");var waitInbox=Source("wait-inbox");var waitBatch=Offer(active,waitInbox);
            var joinFolder=Source("join-folder");var joinInbox=Source("join-inbox");var joinBatch=Offer(active,joinInbox);
            active.TogglePause();rejected=false;try{active.AppendRun(activeRun.Id,[joinFolder,joinInbox],true,[joinBatch]);}catch(IOException){rejected=true;}
            Check(rejected&&!active.CanJoinRunning&&active.Database.Inbox().Single(i=>i.Id==joinBatch.Manifest.Id).Status=="Offered","Pending pause prevents active additions without accepting their inbox batch");
            active.AppendRun(activeRun.Id,[waitFolder,waitInbox],false,[waitBatch]);active.TogglePause();
            active.AppendRun(activeRun.Id,[joinFolder,joinInbox],true,[joinBatch]);
            Check(ReferenceEquals(encoding,active.Current)&&ReferenceEquals(encoding,active.State.Jobs[0])&&encoding.Status=="Encoding"&&active.State.Jobs.All(j=>j.QueueId==activeRun.Id),"Live append keeps the active encode object and all additions in the same Tucker");
            await task;active.Line-=OnLine;
            Check(active.State.Jobs.Where(j=>j.Source==joinFolder.Source||j.Source==joinInbox.Source||j.Source==encoding.Source).All(j=>j.Status=="Completed")&&active.State.Jobs.Where(j=>j.Source==waitFolder.Source||j.Source==waitInbox.Source).All(j=>j.Status=="Waiting"),"Only explicitly joined additions are processed; waiting additions stay waiting in the same run");
            var ended=Source("after-processing");rejected=false;try{active.AppendRun(activeRun.Id,[ended],true,[]);}catch(IOException){rejected=true;}
            Check(rejected&&!active.Running&&active.State.Jobs.All(j=>j.Source!=ended.Source),"Finishing during source selection never silently restarts processing");
            Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-wait");signal=new(TaskCreationOptions.RunContinuationsAsynchronously);active.Line+=OnLine;
            task=active.Start(activeRun.Id);await signal.Task.WaitAsync(TimeSpan.FromSeconds(15));active.Stop();rejected=false;
            try{active.AppendRun(activeRun.Id,[ended],true,[]);}catch(IOException){rejected=true;}
            active.AppendRun(activeRun.Id,[ended],false,[]);await task;active.Line-=OnLine;
            Check(rejected&&!active.CanJoinRunning&&active.State.Jobs.Single(j=>j.Source==ended.Source).Status=="Waiting"&&!active.Running,"Stopping permits durable waiting additions while rejecting additions to active processing");
        }finally{if(active.Running){active.Stop();await active.RunTask!;}Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
        return passed;
    }
}
