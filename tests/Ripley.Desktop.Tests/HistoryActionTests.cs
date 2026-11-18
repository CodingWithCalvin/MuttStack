using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ripley;

partial class Program
{
    static async Task CheckHistoryActions(string temp,string? renders)
    {
        var state=Path.Combine(temp,"history-actions");Directory.CreateDirectory(state);
        var files=MakeDisc(Path.Combine(temp,"history-action-raw"),"ICARLY_S1_D1","iCarly","82493","1",2);
        var first=new RipSession{DiscLabel="ICARLY_S1_D1",Season="1",Status="Completed",Files=[files[0]],RawFolder=Path.GetDirectoryName(files[0].Source)!};
        var second=new RipSession{DiscLabel="ICARLY_S1_D2",Season="1",Status="Completed",Files=[files[1]],RawFolder=first.RawFolder};
        var document=JsonSerializer.SerializeToNode(new{Selected=second.Id,Sessions=new[]{first,second}})!;
        foreach(var item in document["Sessions"]!.AsArray())item!.AsObject().Remove("Archived");
        var historyPath=Path.Combine(state,"history.json");QueueStore.Write(historyPath,document);
        bool approve=false;string? confirmedName=null;int confirmations=0;
        bool Confirm(string name){confirmedName=name;confirmations++;return approve;}
        var w=new MainWindow(state,confirmHistoryDeletion:Confirm);
        T C<T>(string name) where T:FrameworkElement=>Control<T>(w,name);
        ObservableCollection<RipSession> History()=>(ObservableCollection<RipSession>)Field(w,"ripHistory")!;
        RipSession Run(string id)=>History().Single(s=>s.Id==id);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Action(ContextMenu menu,int index)=>((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        async Task<ContextMenu> Menu(string id)
        {
            w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(1080,740));root.Arrange(new Rect(0,0,1080,740));root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list=C<ListBox>("HistoryList");var run=Run(id);list.ScrollIntoView(run);list.UpdateLayout();
            var item=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(run);
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Right){RoutedEvent=UIElement.PreviewMouseRightButtonDownEvent});
            Check(ReferenceEquals(item,list.ItemContainerGenerator.ContainerFromItem(run)),"Right-click preserves the history item under the pointer");
            Call(w,"HistoryMenuOpening",item,null!);var menu=item.ContextMenu;await Dispatcher.Yield(DispatcherPriority.DataBind);
            menu.ApplyTemplate();menu.Measure(new Size(220,180));menu.Arrange(new Rect(0,0,220,menu.DesiredSize.Height));menu.UpdateLayout();
            return menu;
        }
        try {
            Check(History().Count==2&&History().All(s=>!s.Archived)&&C<ListBox>("HistoryList").Items.Count==2,"Older history without archive flags remains visible");
            var menu=await Menu(first.Id);
            Check(C<ListBox>("HistoryList").SelectedItem==Run(first.Id)&&((MenuItem)menu.Items[0]).Header.ToString()=="Archive"&&((MenuItem)menu.Items[1]).DataContext==Run(first.Id),"Archive and Delete target the right-clicked run rather than the previous selection");
            if(renders!=null){Directory.CreateDirectory(renders);var bitmap=new RenderTargetBitmap((int)menu.ActualWidth,(int)menu.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(menu);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(renders,"history-menu.png"));png.Save(output);}
            Call(w,"SendToTucker",w,new RoutedEventArgs());var handoff=Run(first.Id).HandoffId;
            var outbox=TuckerHandoff.ManifestPath(state,first.Id);var original=File.ReadAllBytes(outbox);
            Action(menu,0);
            Check(Run(first.Id).Archived&&Run(first.Id).IsLocked&&C<ListBox>("HistoryList").Items.Count==1&&C<Button>("ShowArchivedHistory").Content.ToString()=="Archived (1)","Archive hides a sent run while preserving its handoff lock");
            Check(((RipSession)Field(w,"currentRipSession")!).Id==second.Id&&JsonNode.Parse(File.ReadAllText(historyPath))!["Sessions"]![0]!["Archived"]!.GetValue<bool>(),"Archive persists before switching the workspace to the remaining run");
            Click("ShowArchivedHistory");
            Check(C<TextBlock>("HistoryHeading").Text=="RIPLEY ARCHIVE"&&!C<Button>("RipButton").IsEnabled&&!C<Button>("SendTuckerButton").IsEnabled&&C<Button>("RestoreHistoryButton").IsEnabled,"Archive view provides read-only review and a visible Restore Ripley button");
            using(var db=new Doggo.Sqlite(Path.Combine(state,"doggo.db")))db.Exec("UPDATE handoff_batches SET status='Accepted' WHERE id=?",handoff);
            Call(w,"RefreshHandoffs");
            Check(Run(first.Id).Archived&&Run(first.Id).HandoffStatus=="Accepted","Archived runs still receive Tucker acknowledgements");
            if(renders!=null)await Render(w,1080,740,Path.Combine(renders,"history-archive-minimum.png"));
            menu=await Menu(first.Id);Check(((MenuItem)menu.Items[0]).Header.ToString()=="Restore","Archived history changes the menu action to Restore");Action(menu,0);
            Check(!Run(first.Id).Archived&&Run(first.Id).IsLocked&&C<ListBox>("HistoryList").Items.Count==2&&!C<Button>("SendTuckerButton").IsEnabled,"Restoring sent history preserves acceptance and cannot resend it");

            menu=await Menu(second.Id);Action(menu,0);Click("ShowArchivedHistory");
            Call(w,"SendToTucker",w,new RoutedEventArgs());await (Task)Call(w,"RipDiscCore")!;
            Check(Run(second.Id).HandoffId==""&&!C<Button>("SendTuckerButton").IsEnabled&&!(bool)Field(w,"discBusy")!,"Archived unsent runs cannot send or start ripping until restored");
            w.Close();w=new MainWindow(state,confirmHistoryDeletion:Confirm);
            Check(Run(second.Id).Archived&&C<ListBox>("HistoryList").Items.Count==1&&((RipSession)Field(w,"currentRipSession")!).Id==first.Id,"Reopening keeps archives hidden and selects a visible run");
            Click("ShowArchivedHistory");Click("RestoreHistoryButton");
            Check(!Run(second.Id).Archived&&C<Button>("SendTuckerButton").IsEnabled,"Restoring an unsent completed run restores its explicit handoff action");

            Call(w,"SetDiscBusy",true);menu=await Menu(second.Id);var confirmationCount=confirmations;
            Check(!((MenuItem)menu.Items[0]).IsEnabled&&!((MenuItem)menu.Items[1]).IsEnabled&&!C<Button>("ShowArchivedHistory").IsEnabled,"History management is disabled during a disc operation");
            Call(w,"ChangeArchive",Run(second.Id));Call(w,"DeleteHistory",new Button{DataContext=Run(second.Id)},new RoutedEventArgs());Call(w,"ToggleArchiveHistory",w,new RoutedEventArgs());
            Check(!Run(second.Id).Archived&&History().Count==2&&confirmations==confirmationCount&&!(bool)Field(w,"showArchived")!,"Busy-operation guards also block direct archive, delete and filter actions");
            Call(w,"SetDiscBusy",false);Call(w,"SetAdding",true);Call(w,"ChangeArchive",Run(second.Id));
            Check(!Run(second.Id).Archived,"Sending to Tucker prevents concurrent history changes");Call(w,"SetAdding",false);

            menu=await Menu(second.Id);Action(menu,1);
            Check(confirmedName==Run(second.Id).Title&&History().Count==2,"Delete confirmation names the clicked run and cancellation keeps it");
            approve=true;var before=File.ReadAllBytes(historyPath);
            using(var lockedHistory=new FileStream(historyPath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Action(menu,0);
                Check(!Run(second.Id).Archived&&C<ListBox>("HistoryList").Items.Count==2&&C<TextBlock>("Status").Text.Contains("Could not update rip history"),"Failed archive persistence leaves the visible run unchanged");
                Action(menu,1);
                Check(History().Count==2&&C<TextBlock>("Status").Text.Contains("Could not delete rip history")&&before.SequenceEqual(File.ReadAllBytes(historyPath)),"Failed delete persistence keeps the record and original history file");
            }
            if(renders!=null)await Render(w,1440,920,Path.Combine(renders,"history-active.png"));
            menu=await Menu(first.Id);Action(menu,1);
            Check(History().All(s=>s.Id!=first.Id)&&File.ReadAllBytes(outbox).SequenceEqual(original)&&new Doggo.DoggoInbox(Path.Combine(state,"doggo.db")).Status(handoff)=="Accepted"&&files.All(f=>File.Exists(f.Source)&&File.Exists(f.DiscMetadata)),"Deleting sent history preserves raw files, disc metadata, outbox and accepted inbox request");
            menu=await Menu(second.Id);Action(menu,1);
            Check(History().Count==0&&Field(w,"currentRipSession")==null&&((List<Job>)Field(w,"lastRipFiles")!).Count==0&&C<Button>("SendTuckerButton").Visibility==Visibility.Collapsed&&C<TextBlock>("PageHeading").Text=="New Ripley","Deleting the last entry clears the workspace and cannot send stale files");
            w.Close();w=new MainWindow(state,confirmHistoryDeletion:Confirm);
            Check(History().Count==0&&C<ListBox>("HistoryList").Items.Count==0,"Deleted history stays deleted after reopening despite retained outbox manifests");
            Click("NewRipleyButton");Text(w,"DiscLabel").Text="NEXT_S2_D1";var draft=History().Single();
            menu=await Menu(draft.Id);Action(menu,0);
            Check(C<ListBox>("HistoryList").Items.Count==0&&C<TextBlock>("EmptyHistory").Visibility==Visibility.Visible&&C<Button>("ShowArchivedHistory").Content.ToString()=="Archived (1)","Archiving the last run shows an empty history with access to its archive");
            Click("ShowArchivedHistory");Click("NewRipleyButton");
            Check(History().Count==2&&draft.Archived&&C<ListBox>("HistoryList").Items.Count==1&&!((RipSession)Field(w,"currentRipSession")!).Archived&&Text(w,"DiscLabel").Text=="","New Ripley exits the archive and creates an independent editable run");
        }finally{Call(w,"SetDiscBusy",false);Call(w,"SetAdding",false);w.Close();}
    }
}
