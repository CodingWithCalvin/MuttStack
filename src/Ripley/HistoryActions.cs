using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace Ripley;

public partial class MainWindow
{
    bool showArchived;
    readonly Func<string,bool>? confirmHistoryDeletion;
    bool CanManageHistory=>historyReady&&!discBusy&&!adding;

    void RefreshHistoryControls()
    {
        var count=ripHistory.Count(s=>s.Archived);
        HistoryHeading.Text=showArchived?"RIPLEY ARCHIVE":"RIPLEY HISTORY";
        ShowArchivedHistory.Content=showArchived?"Back":$"Archived ({count})";
        ShowArchivedHistory.Visibility=showArchived||count>0?Visibility.Visible:Visibility.Collapsed;
        ShowArchivedHistory.IsEnabled=CanManageHistory;
        ShowArchivedHistory.ToolTip=showArchived?"Return to Ripley history":"View and restore archived runs";
        EmptyHistory.Text=showArchived?"No archived runs.":"Choose New Ripley to start a run. Right-click a saved run to archive or delete it.";
        EmptyHistory.Visibility=ripHistory.Any(s=>s.Archived==showArchived)?Visibility.Collapsed:Visibility.Visible;
        RestoreHistoryButton.Visibility=showArchived&&currentRipSession?.Archived==true?Visibility.Visible:Visibility.Collapsed;
        RestoreHistoryButton.IsEnabled=CanManageHistory;
    }
    void ToggleArchiveHistory(object sender,RoutedEventArgs e)
    {
        if(!CanManageHistory)return;
        CaptureHistory();showArchived=!showArchived;
        SelectHistory(ripHistory.FirstOrDefault(s=>s.Archived==showArchived));
    }
    void HistoryRightClick(object sender,MouseButtonEventArgs e)
    {
        if(CanManageHistory&&sender is ListBoxItem item){item.IsSelected=true;item.Focus();}
    }
    void HistoryMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(sender is not ListBoxItem {DataContext:RipSession session} item)return;
        if(CanManageHistory)item.IsSelected=true;
        var menu=item.ContextMenu;menu.PlacementTarget=item;
        var archive=(MenuItem)menu.Items[0];var delete=(MenuItem)menu.Items[1];
        archive.Header=session.Archived?"Restore":"Archive";
        archive.IsEnabled=delete.IsEnabled=CanManageHistory;
        archive.ToolTip=delete.ToolTip=CanManageHistory?null:"Finish or cancel the disc operation before changing history.";
        archive.Click-=ArchiveHistory;archive.Click+=ArchiveHistory;
        delete.Click-=DeleteHistory;delete.Click+=DeleteHistory;
    }
    void ArchiveHistory(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement {DataContext:RipSession session})ChangeArchive(session);
    }
    void RestoreHistory(object sender,RoutedEventArgs e)
    {
        if(currentRipSession is {Archived:true} session)ChangeArchive(session);
    }
    void ChangeArchive(RipSession session)
    {
        if(!CanManageHistory||!ripHistory.Contains(session))return;
        CaptureHistory();var wasArchived=session.Archived;
        session.Archived=!wasArchived;
        var next=wasArchived?session:ripHistory.FirstOrDefault(s=>!s.Archived);
        try {QueueStore.Write(HistoryPath,new HistoryDocument(next?.Id,ripHistory.ToList()));}
        catch(Exception ex){session.Archived=wasArchived;Error(new IOException("Could not update rip history: "+ex.Message,ex));return;}
        showArchived=false;session.Refresh();SelectHistory(next);
        Status.Text=wasArchived?$"Restored {session.Title}. Its previous status and handoff lock are kept.":$"Archived {session.Title}. Find it under Archived to restore it.";
    }
    void DeleteHistory(object sender,RoutedEventArgs e)
    {
        if(!CanManageHistory||sender is not FrameworkElement {DataContext:RipSession session}||!ripHistory.Contains(session))return;
        var confirmed=confirmHistoryDeletion?.Invoke(session.Title)??MessageBox.Show(this,$"Delete \"{session.Title}\" from Ripley history? This cannot be undone. Raw files, disc metadata and requests already sent to Tucker are kept.","Delete Ripley history",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
        if(!confirmed||!CanManageHistory)return;
        CaptureHistory();var remaining=ripHistory.Where(s=>s!=session).ToList();
        var next=remaining.FirstOrDefault(s=>s.Archived==showArchived);
        try {QueueStore.Write(HistoryPath,new HistoryDocument(next?.Id,remaining));}
        catch(Exception ex){Error(new IOException("Could not delete rip history: "+ex.Message,ex));return;}
        ripHistory.Remove(session);SelectHistory(next);
        Status.Text=$"Deleted {session.Title} from history. Raw files and Tucker requests were kept.";
    }
    void SelectHistory(RipSession? session)
    {
        restoringHistory=true;
        try {currentRipSession=null;historyView?.Refresh();HistoryList.SelectedItem=null;}
        finally {restoringHistory=false;}
        if(session!=null)HistoryList.SelectedItem=session;
        else {
            // A removed or hidden run must not remain available in the ripping workspace.
            var ready=namingReady;namingReady=false;
            try {
                DiscLabel.Text=Season.Text=Volume.Text=DiscNumber.Text="";lastSeasonLabel="";
                inspected=null;inspectedSource="";lastRipFiles.Clear();ripQueue.Reset(0);
                RipQueuePanel.Visibility=Visibility.Collapsed;RipProgress.Value=0;
                RipStatus.Text=showArchived?"Select an archived run to review or restore it.":"Choose New Ripley to start a run.";
                RefreshRipQueue();
            }finally {namingReady=ready;}
            UpdateNamingPreview();ShowPage("Rip");SaveHistory();
        }
        RefreshShell();
    }
}
