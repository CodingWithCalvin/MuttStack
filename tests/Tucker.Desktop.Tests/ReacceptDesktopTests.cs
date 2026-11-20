using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tucker;

static class ReacceptDesktopTests
{
    public static async Task<int> Run(string root,string fake,string? renders)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var home=Path.Combine(root,"reaccept-ui");Directory.CreateDirectory(home);
        var preset=Path.Combine(home,"preset.json");
        File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Reaccept UI","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var path=Path.Combine(home,"doggo.db");var db=new DoggoDatabase(path);
        db.Settings(new Preferences(fake,preset,"",Path.Combine(home,"out"),"{disc_label}","","Reaccept UI",1));
        var w=new MainWindow(home,path);
        T C<T>(string name)=>(T)w.FindName(name);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Preview()=>await (Task)typeof(MainWindow).GetMethod("PreparePreview",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,[])!;
        Handoff Offer(string label)
        {
            var source=Path.Combine(home,label+".mkv");File.WriteAllText(source,"raw "+label);var f=new FileInfo(source);
            var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=source,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,DiscLabel=label}]};
            var json=Path.Combine(home,manifest.Id+".json");QueueStore.Write(json,manifest);var batch=Handoff.Read(json);db.Offer(batch);return batch;
        }
        void Select(params Handoff[] batches)
        {
            Click("NavInbox");var grid=C<DataGrid>("InboxGrid");grid.SelectedItems.Clear();
            foreach(var row in grid.Items.Cast<InboxItem>().Where(i=>batches.Any(h=>h.Manifest.Id==i.Id)))grid.SelectedItems.Add(row);
        }
        try {
            var first=Offer("Accepted disc");Select(first);Click("UseInboxBatches");await Preview();Click("AddWaiting");
            var original=w.Manager.State.Jobs.Single().QueueId;var receipt=w.Manager.State.Receipts.Single();
            Select(first);Click("UseInboxBatches");
            Check(C<TextBlock>("Status").Text.Contains("Re-accept selected")&&C<ListBox>("SourcesList").Items.Count==0,"Normal inbox action points accepted batches to explicit re-acceptance");
            Click("ReacceptInboxBatches");await Preview();
            Check(C<DataGrid>("PreviewGrid").Items.Cast<Job>().Single().Status=="Already queued"&&C<TextBlock>("PreviewSummary").Text.Contains("1 skipped"),"Re-accept preview explains existing unfinished work");
            Click("AddWaiting");
            Check(w.Manager.State.Jobs.Count==1&&C<TextBlock>("Status").Text.Contains("resume or delete"),"Saving a duplicate re-accept gives actionable feedback without another run");
            w.Manager.DeleteRun(original);Select(first);
            if(renders!=null)await Render(w,Path.Combine(renders,"inbox-reaccept-minimum.png"));
            Click("ReacceptInboxBatches");await Preview();
            Check(C<ListBox>("SourcesList").Items.Cast<DraftSource>().Single().Display.StartsWith("Re-accept inbox")&&C<DataGrid>("PreviewGrid").Items.Cast<Job>().Single().Status=="Ready to reprocess","Deleted batches visibly stage and preview for reprocessing");
            if(renders!=null)await Render(w,Path.Combine(renders,"reaccept-preview-minimum.png"));
            Click("NewTuckerButton");
            Check(w.Manager.State.Jobs.Count==0&&db.Inbox().Single().Status=="Accepted"&&w.Manager.State.Receipts.Single()==receipt,"Cancelling a re-accept draft preserves the original receipt and adds no work");
            var fresh=Offer("Waiting disc");Select(first,fresh);Click("ReacceptInboxBatches");await Preview();
            Check(C<DataGrid>("PreviewGrid").Items.Count==2&&C<ListBox>("SourcesList").Items.Count==2&&db.PendingInboxCount()==1,"A mixed inbox selection stages accepted and waiting batches without accepting early");
            Click("AddWaiting");var run=w.Manager.State.Jobs.First().QueueId;
            Check(w.Manager.State.Jobs.Count==2&&w.Manager.State.Jobs.All(j=>j.QueueId==run&&j.Status=="Waiting")&&w.Manager.State.Receipts.Count==2&&C<Border>("InboxBadge").Visibility==Visibility.Collapsed,"Mixed re-accept creates one waiting run, keeps receipts unique, and clears only the offered badge");
            Click("StartSelected");await w.Manager.RunTask!;
            var oldOutput=w.Manager.State.Jobs.Single(j=>j.Source==first.Manifest.Files[0].Source).OutputPath;
            Click("AddMoreButton");Select(first);Click("ReacceptInboxBatches");C<TextBox>("Destination").Text=Path.Combine(home,"new-output");await Preview();Click("AddWaiting");
            Check(w.Manager.State.Jobs.Count==3&&w.Manager.State.Jobs.Last().QueueId==run&&w.Manager.State.Jobs.Last().Encoding!.Output.StartsWith(Path.Combine(home,"new-output"))&&File.Exists(oldOutput),"Re-accept appends using current output preferences and preserves completed files");
            w.Close();w=new MainWindow(home,path);
            Check(w.Manager.State.Jobs.Count==3&&w.Manager.State.Receipts.Count==2&&!w.Manager.Running&&db.Inbox().All(i=>i.Status=="Accepted"),"Re-accepted desktop runs survive reopen without starting automatically");
        }finally{if(w.Manager.Running){w.Manager.Stop();await w.Manager.RunTask!;}w.Close();}
        return passed;
    }
    static async Task Render(MainWindow w,string path)
    {
        w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(1080,740));root.Arrange(new Rect(0,0,1080,740));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1080,740,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
