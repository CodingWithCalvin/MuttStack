using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tucker;
static class HistoryDesktopTests
{
    public static async Task<int> Run(string root,string fake,string? renders)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var home=Path.Combine(root,"history-ui");Directory.CreateDirectory(home);var dbPath=Path.Combine(home,"doggo.db");
        var preset=Path.Combine(home,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"History UI","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var db=new DoggoDatabase(dbPath);db.Settings(new Preferences(fake,preset,"",Path.Combine(home,"out"),"All","","History UI",1));
        bool approveDelete=false;string? deletionName=null;
        bool Confirm(string name){deletionName=name;return approveDelete;}
        var w=new MainWindow(home,dbPath,Confirm);
        T C<T>(string name)=>(T)w.FindName(name);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
        ObservableCollection<string> Folders()=>(ObservableCollection<string>)typeof(MainWindow).GetField("folders",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!;
        async Task Preview()=>await (Task)Call("PreparePreview")!;
        string Source(string name){var dir=Path.Combine(home,"raw",name);Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"title.mkv"),"raw "+name);return dir;}
        Handoff Offer(string label,string series,string season){var dir=Source(label);var f=new FileInfo(Path.Combine(dir,"title.mkv"));var m=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=f.FullName,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,Series=new("tvdb",series,series,"2020",""),Season=season,DiscLabel=label,DiscId=label,RipDate=DateTime.Now}]};var path=Path.Combine(home,m.Id+".json");QueueStore.Write(path,m);var h=Handoff.Read(path);db.Offer(h);return h;}
        void Use(Handoff batch){Click("NavInbox");C<DataGrid>("InboxGrid").SelectedItem=C<DataGrid>("InboxGrid").Items.Cast<InboxItem>().Single(i=>i.Id==batch.Manifest.Id);Click("UseInboxBatches");}
        async Task<ContextMenu> Menu(string id){
            await Layout(w);var list=C<ListBox>("QueueList");var tile=list.Items.Cast<QueueTile>().Single(q=>q.Id==id);list.ScrollIntoView(tile);list.UpdateLayout();
            var item=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(tile);
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Right){RoutedEvent=UIElement.PreviewMouseRightButtonDownEvent});
            Check(ReferenceEquals(item,list.ItemContainerGenerator.ContainerFromItem(tile))&&((QueueTile)list.SelectedItem).Id==id,"Right-click selects its own run without replacing the item under the pointer");
            Call("HistoryMenuOpening",item,null);var menu=item.ContextMenu;await Dispatcher.Yield(DispatcherPriority.DataBind);
            // Apply the real menu tree to exercise inherited DataContext before clicking.
            menu.ApplyTemplate();menu.Measure(new Size(200,200));menu.Arrange(new Rect(0,0,200,menu.DesiredSize.Height));menu.UpdateLayout();
            return menu;
        }
        void Action(ContextMenu menu,int index)=>((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        try{
            var folder=Source("Weekend_Discs");Folders().Add(folder);
            Check(C<TextBlock>("Heading").Text=="Weekend Discs","Folder selection immediately supplies an automatic run heading");
            var batch=Offer("Alpha_S1_D1","Alpha","1");Use(batch);
            Check(C<TextBlock>("Heading").Text=="Alpha + Weekend Discs","Adding inbox batches updates the suggested name before preview");
            await Preview();Check(C<TextBlock>("Heading").Text=="Alpha + Weekend Discs · 2 files"&&C<Button>("AddWaiting").IsEnabled,"Create Tucker shows the final suggested name and deduplicated file count");
            Check(w.FindName("RunName")==null,"New Tucker has no run-name setting");
            Folders().Add(Source("More_Discs"));await Preview();var suggested=C<TextBlock>("Heading").Text;
            Check(suggested=="Alpha + More Discs + 1 more · 3 files","Changing sources automatically updates the run heading before creation");
            if(renders!=null){Directory.CreateDirectory(renders);await Render(w,1080,740,Path.Combine(renders,"automatic-name-minimum.png"));}
            Click("AddWaiting");var first=w.Manager.State.Jobs[0].QueueId;
            Check(w.Manager.State.Jobs.All(j=>j.QueueName==suggested)&&C<TextBlock>("Heading").Text==suggested,"Creating the Tucker saves the exact suggested name shown before creation");
            Click("NewTuckerButton");Use(Offer("Beta_S2_D1","Beta","2"));
            Check(C<TextBlock>("Heading").Text=="Beta · Season 2 · 1 file","Inbox-only selection names the run from series and season before preview");
            await Preview();Click("AddWaiting");var second=w.Manager.State.Jobs.Last().QueueId;
            var menu=await Menu(first);Check(((MenuItem)menu.Items[0]).Header.ToString()=="Archive"&&((MenuItem)menu.Items[1]).Header.ToString()=="Delete"&&((MenuItem)menu.Items[0]).DataContext is QueueTile q&&q.Id==first,"Context menu offers Archive and Delete for the clicked run");
            if(renders!=null)await RenderElement(menu,Path.Combine(renders,"history-context-menu.png"));
            Action(menu,0);
            Check(w.Manager.IsArchived(first)&&C<ListBox>("QueueList").Items.Count==1&&C<Button>("ShowArchivedHistory").Content.ToString()=="Archived (1)"&&C<TextBlock>("OverallSummary").Text.StartsWith("0 / 1"),"Archiving hides the run from active history and overall processing totals");
            Click("StartAll");await w.Manager.RunTask!;
            Check(w.Manager.State.Jobs.Where(j=>j.QueueId==first).All(j=>j.Status=="Waiting")&&w.Manager.State.Jobs.Single(j=>j.QueueId==second).Status=="Completed"&&!C<Button>("StartAll").IsEnabled,"Desktop Start all leaves archived work waiting");
            Click("ShowArchivedHistory");
            Check(C<TextBlock>("HistoryHeading").Text=="TUCKER ARCHIVE"&&C<ListBox>("QueueList").Items.Count==1&&!C<Button>("StartSelected").IsEnabled&&!C<Button>("AddMoreButton").IsEnabled&&C<Button>("RestoreRunButton").IsEnabled,"Archive view allows review and restoration without starting or extending archived runs");
            if(renders!=null)await Render(w,1080,740,Path.Combine(renders,"archived-history-minimum.png"));
            menu=await Menu(first);Check(((MenuItem)menu.Items[0]).Header.ToString()=="Restore","The archived run's right-click action changes to Restore");Action(menu,0);
            Check(!w.Manager.IsArchived(first)&&C<ListBox>("QueueList").Items.Count==2&&!w.Manager.Running&&C<Button>("StartAll").IsEnabled,"Restoring returns the run to active history without auto-starting it");
            menu=await Menu(second);Action(menu,1);
            Check(deletionName==w.Manager.State.Jobs.Last().QueueName&&w.Manager.State.Jobs.Select(j=>j.QueueId).Distinct().Count()==2,"Delete confirmation names the clicked run and cancellation preserves it");
            var output=w.Manager.State.Jobs.Last().OutputPath;approveDelete=true;Action(menu,1);
            Check(w.Manager.State.Jobs.All(j=>j.QueueId==first)&&File.Exists(output)&&db.Inbox().All(i=>i.Status=="Accepted"),"Confirmed deletion removes the selected run while preserving completed media and inbox receipts");
            Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");Click("StartSelected");
            menu=await Menu(first);Check(!((MenuItem)menu.Items[0]).IsEnabled&&!((MenuItem)menu.Items[1]).IsEnabled,"Archive and Delete are disabled while the selected Tucker is processing");
            w.Manager.TogglePause();await w.Manager.RunTask!;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
            menu=await Menu(first);Action(menu,0);w.Close();w=new(home,dbPath,Confirm);
            Check(C<ListBox>("QueueList").Items.Count==0&&C<Button>("ShowArchivedHistory").Content.ToString()=="Archived (1)"&&!C<Button>("StartAll").IsEnabled,"Reopening keeps archived runs hidden and excluded from Start all");
            Click("ShowArchivedHistory");Click("RestoreRunButton");Check(C<ListBox>("QueueList").Items.Count==1&&!w.Manager.IsArchived(first),"Restore Tucker button returns a persisted archived run to history");
        }finally{if(w.Manager.Running){w.Manager.Stop();await w.Manager.RunTask!;}w.Close();Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
        return passed;
    }
    static async Task Layout(MainWindow w,int width=1080,int height=740){w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();}
    static async Task Render(MainWindow w,int width,int height,string path){await Layout(w,width,height);await RenderElement((FrameworkElement)w.Content,path);}
    static Task RenderElement(FrameworkElement element,string path){var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);return Task.CompletedTask;}
}
