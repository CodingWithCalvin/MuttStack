using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using Tucker;
static class AppendDesktopTests
{
    public static async Task<int> Run(string root,string fake,string? renders)
    {
        int passed=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
        var home=Path.Combine(root,"append-ui");Directory.CreateDirectory(home);var dbPath=Path.Combine(home,"doggo.db");
        var preset=Path.Combine(home,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Append UI","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var db=new DoggoDatabase(dbPath);db.Settings(new Preferences(fake,preset,"",Path.Combine(home,"out"),"{disc_label}","","Append UI",1));
        var w=new MainWindow(home,dbPath);
        T C<T>(string name)=>(T)w.FindName(name);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
        ObservableCollection<string> Folders()=>(ObservableCollection<string>)typeof(MainWindow).GetField("folders",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!;
        async Task Preview()=>await (Task)Call("PreparePreview")!;
        string Source(string name){var dir=Path.Combine(home,"raw",name);Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"title.mkv"),"raw "+name);return dir;}
        Handoff Offer(string name){var dir=Source(name);var source=Path.Combine(dir,"title.mkv");var f=new FileInfo(source);var m=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=source,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,DiscLabel=name,DiscId=name,RipDate=DateTime.Now}]};var path=Path.Combine(home,m.Id+".json");QueueStore.Write(path,m);var h=Handoff.Read(path);db.Offer(h);return h;}
        void Use(params Handoff[] batches){Click("NavInbox");C<DataGrid>("InboxGrid").SelectedItems.Clear();foreach(var row in C<DataGrid>("InboxGrid").Items.Cast<InboxItem>().Where(i=>batches.Any(h=>h.Manifest.Id==i.Id)))C<DataGrid>("InboxGrid").SelectedItems.Add(row);Click("UseInboxBatches");}
        void Select(string id)=>C<ListBox>("QueueList").SelectedItem=C<ListBox>("QueueList").Items.Cast<QueueTile>().Single(q=>q.Id==id);
        try {
            Folders().Add(Source("original"));await Preview();var runName=C<TextBlock>("Heading").Text;Click("AddWaiting");var runId=w.Manager.State.Jobs.Single().QueueId;
            Click("StartSelected");await w.Manager.RunTask!;var original=w.Manager.State.Jobs.Single();var frozen=JsonSerializer.Serialize(original);
            Click("NewTuckerButton");Folders().Add(Source("other-run"));await Preview();Click("AddWaiting");var otherId=w.Manager.State.Jobs.Last().QueueId;
            Select(runId);Click("AddMoreButton");
            Check(C<TextBlock>("Heading").Text=="Add to "+runName&&w.FindName("RunName")==null&&C<Button>("AddWaiting").Content.ToString()=="Add as waiting","Completed runs expose an append workspace with a fixed target and clear save action");
            Folders().Add(Source("initial-addition"));var first=Offer("initial-inbox");Select(otherId);Use(first);await Preview();Click("AddWaiting");
            Check(w.Manager.State.Jobs.Count(j=>j.QueueId==runId)==3&&w.Manager.State.Jobs.Count(j=>j.QueueId==otherId)==1&&C<ListBox>("QueueList").Items.Count==2&&!w.Manager.Running,"Navigating elsewhere while collecting additions cannot change the bound target or create another run");
            Check(ReferenceEquals(original,w.Manager.State.Jobs.First())&&JsonSerializer.Serialize(original)==frozen&&db.Inbox().Single().Status=="Accepted","Idle append preserves completed work and commits its batch acknowledgement");
            var waitingFolder=Source("wait-folder");var waitingBatch=Offer("wait-inbox");var joinedFolder=Source("join-folder");var joinedBatch=Offer("join-inbox");var joinedBatch2=Offer("join-inbox-two");
            Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");Click("StartSelected");await Until(()=>w.Manager.Running&&w.Manager.Current!=null);var current=w.Manager.Current;
            Click("PauseButton");Click("AddMoreButton");Folders().Add(waitingFolder);Use(waitingBatch);await Preview();
            Check(w.Manager.PausePending&&C<Button>("AddWaiting").IsEnabled&&!C<Button>("AddRunning").IsEnabled&&C<Button>("AddMoreButton").IsEnabled,"Running and pausing runs still allow additions, with active joining disabled during a pending pause");
            Click("AddWaiting");
            Check(ReferenceEquals(current,w.Manager.Current)&&w.Manager.PausePending&&db.Inbox().Single(i=>i.Id==waitingBatch.Manifest.Id).Status=="Accepted","Saving waiting additions leaves the current encode and pending pause intact");
            Click("PauseButton");Click("AddMoreButton");Folders().Add(joinedFolder);Use(joinedBatch,joinedBatch2);C<TextBox>("Destination").Text=Path.Combine(home,"new-output");await Preview();
            Check(C<Button>("AddRunning").IsEnabled&&C<Button>("UseInboxBatches").Content.ToString()=="Add selected to this Tucker"&&C<TextBlock>("Heading").Text=="Add to "+runName,"Multiple folders and inbox batches can be added to the active run with an explicit join action");
            if(renders!=null){Directory.CreateDirectory(renders);await Render(w,1080,740,Path.Combine(renders,"append-running-minimum.png"));}
            Click("AddRunning");var joined=w.Manager.State.Jobs.Where(j=>j.Source.StartsWith(joinedFolder)||j.Source==joinedBatch.Manifest.Files[0].Source||j.Source==joinedBatch2.Manifest.Files[0].Source).ToList();
            Check(joined.Count==3&&joined.All(j=>j.QueueId==runId&&j.Encoding!.Output.StartsWith(Path.Combine(home,"new-output")))&&C<ListBox>("QueueList").Items.Count==2&&db.Inbox().All(i=>i.Status=="Accepted"),"Active append commits all new files and batches to the same run using the previewed new settings");
            await w.Manager.RunTask!;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
            Check(joined.All(j=>j.Status=="Completed")&&w.Manager.State.Jobs.Where(j=>j.Source.StartsWith(waitingFolder)||j.Source==waitingBatch.Manifest.Files[0].Source||j.QueueId==otherId).All(j=>j.Status=="Waiting"),"Joined additions complete while waiting additions and other runs remain waiting");
            Check(JsonSerializer.Serialize(original)==frozen&&File.Exists(original.OutputPath),"Changing settings for appended files never changes original file settings or output");
            var cancelled=Offer("cancelled-append");Click("AddMoreButton");Use(cancelled);Click("CancelAdditions");
            Check(C<TextBlock>("Heading").Text==runName&&db.Inbox().Single(i=>i.Id==cancelled.Manifest.Id).Status=="Offered","Cancelling additions returns to the original run and leaves its inbox batches waiting");
            if(renders!=null)await Render(w,1080,740,Path.Combine(renders,"append-history-minimum.png"));
            w.Close();w=new(home,dbPath);Select(runId);
            Check(w.Manager.State.Jobs.Count(j=>j.QueueId==runId)==8&&C<ListBox>("QueueList").Items.Count==2&&w.Manager.State.Receipts.Count==4&&!w.Manager.Running,"Reopening retains appended memberships, receipts and statuses without auto-start");
        }finally{if(w.Manager.Running){w.Manager.Stop();await w.Manager.RunTask!;}w.Close();Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
        return passed;
    }
    static async Task Until(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(10);while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException("Append workflow timed out.");await Task.Delay(20);}}
    static async Task Render(MainWindow w,int width,int height,string path){w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}
