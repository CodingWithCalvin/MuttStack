using Tucker;
static class RunTests
{
    public static int Run(string root,string fake)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var home=Path.Combine(root,"combined-runs");Directory.CreateDirectory(home);
        var preset=Path.Combine(home,"preset.json");File.WriteAllText(preset,"{}");
        var options=new Options(fake,"test",preset,false,Path.Combine(home,"out"),".mkv");
        HandoffFile FileSource(string label,string series,string season,string name="title.mkv"){
            var folder=Path.Combine(home,"raw",label);Directory.CreateDirectory(folder);var source=Path.Combine(folder,name);File.WriteAllText(source,"raw data");var stat=new FileInfo(source);
            return new(){Source=source,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,Series=new("tvdb",series,series,"2020",""),Season=season,DiscLabel=label,DiscId=label,RipDate=DateTime.Now};
        }
        Handoff Batch(params HandoffFile[] files){var m=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=files.ToList()};var path=Path.Combine(home,m.Id+".json");QueueStore.Write(path,m);return Handoff.Read(path);}
        var a=FileSource("A_S1_D1","Alpha","1");var b=FileSource("A_S1_D1","Alpha","1","title2.mkv");var c=FileSource("B_S2_D1","Beta","2");
        var first=Batch(a,b);var second=Batch(b,c);
        var external=Path.Combine(home,"raw","external");var ctx=new FolderContext(new("tvdb","Gamma","Gamma","2021",""),"3","G_S3_D1",DateTime.Now,"1","1");
        var raw=RawRip.Create(external,new{series_identity=ctx.Series,season=ctx.Season,disc_label=ctx.Disc,rip_date=ctx.Date});File.WriteAllText(Path.Combine(raw.Files,"external.mkv"),"external raw");raw.Publish("",ctx,null,CancellationToken.None);
        var jobs=RunPlanner.Plan([Path.GetDirectoryName(a.Source)!,external], [first,second],options,true,@"{series}\Season {season:00}","","{filename}");
        Check(jobs.Count==4&&jobs.Select(QueueManager.SourceKey).Distinct().Count()==4,"Combined planning deduplicates folder/inbox and inbox/inbox overlaps");
        Check(jobs.Select(j=>j.Series!.Name).Distinct().Count()==3&&jobs.Single(j=>j.Source==c.Source).Encoding!.Output.EndsWith(@"Beta\Season 02"),"Combined run retains each file's series, season and destination");
        var conflict=System.Text.Json.JsonSerializer.Deserialize<HandoffFile>(System.Text.Json.JsonSerializer.Serialize(a))!;conflict.Season="9";
        bool conflictRejected=false;try{RunPlanner.Plan([], [first,Batch(conflict)],options,true,"All","","{filename}");}catch(IOException){conflictRejected=true;}
        Check(conflictRejected,"Conflicting inbox metadata is rejected instead of silently choosing one batch's values");
        var manager=new QueueManager(Path.Combine(home,"state"));manager.Database.Offer(first);manager.Database.Offer(second);
        var saved=manager.AddRun(jobs,false,[first,second],"Weekend discs");
        Check(saved.Files==4&&manager.State.Jobs.All(j=>j.QueueId==saved.Id&&j.QueueName=="Weekend discs")&&!manager.Running,"Mixed sources save as exactly one waiting Tucker run");
        Check(manager.Database.Inbox().All(i=>i.Status=="Accepted")&&manager.State.Receipts.Count==2&&manager.State.Receipts.All(r=>r.Files==2),"Every inbox batch receives its own accurate receipt in the combined save");
        var restored=new QueueManager(Path.Combine(home,"state"));Check(restored.State.Jobs.Count==4&&restored.State.Jobs.Select(j=>j.QueueId).Distinct().Count()==1&&restored.State.Receipts.Count==2,"Combined run and all receipts survive reopening");
        bool rejected=false;try{restored.AddRun(jobs,false,[first,second],"Duplicate");}catch(IOException){rejected=true;}Check(rejected&&restored.State.Jobs.Count==4,"Repeating combined acceptance cannot duplicate a saved run");
        var failure=new QueueManager(Path.Combine(home,"rollback"));failure.Database.Offer(first);failure.Database.Offer(second);failure.Database.Decline(second.Manifest.Id);
        rejected=false;try{failure.AddRun(jobs,false,[first,second],"Should roll back");}catch(IOException){rejected=true;}
        var after=failure.Database.Read();Check(rejected&&failure.State.Jobs.Count==0&&after.Jobs.Count==0&&after.Receipts.Count==0&&after.ImportedSources.Count==0&&failure.Database.Inbox().Single(i=>i.Id==first.Manifest.Id).Status=="Offered","A rejected batch rolls back every job, import signature and acknowledgement in the combined save");
        var partial=new QueueManager(Path.Combine(home,"partial"));partial.Database.Offer(first);partial.Database.Offer(second);
        rejected=false;try{partial.AddRun(jobs.Where(j=>j.Source!=c.Source),false,[first,second],"Incomplete");}catch(IOException){rejected=true;}Check(rejected&&partial.Database.PendingInboxCount()==2&&partial.State.Jobs.Count==0,"A combined run cannot acknowledge an omitted inbox file");
        File.AppendAllText(c.Source,"changed");rejected=false;try{partial.AddRun(jobs,false,[first,second],"Stale preview");}catch(IOException){rejected=true;}Check(rejected&&partial.Database.PendingInboxCount()==2&&partial.Database.Read().Jobs.Count==0,"A source changed after preview leaves every selected batch unaccepted");
        var many=Enumerable.Range(0,40).Select(i=>Batch(FileSource("Disc"+i,"Series"+i,"1"))).ToArray();
        var plan=RunPlanner.Plan([],many,options,true,"Combined","","{filename}");var large=new QueueManager(Path.Combine(home,"many"));foreach(var h in many)large.Database.Offer(h);
        var combined=large.AddRun(plan,false,many,"Forty batches");Check(combined.Files==40&&large.State.Jobs.Select(j=>j.QueueId).Distinct().Count()==1&&large.State.Receipts.Count==40&&large.State.Jobs.Select(j=>j.OutputPath).Distinct().Count()==40,"Many inbox batches form one run with unique output names and all receipts");
        return passed;
    }
}
