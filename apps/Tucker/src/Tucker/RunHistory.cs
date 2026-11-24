using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace Tucker;

public partial class MainWindow
{
    bool showArchived;
    string draftRunName="";
    readonly Func<string,bool>? confirmRunDeletion;
    void UpdateSuggestedRunName(){if(appendRunId!=null)return;draftRunName=RunNames.Unique(RunNames.Suggest(preview,folders,stagedBatches),Manager.State.Jobs.Select(j=>j.QueueName));}
    List<QueueTile> HistoryTiles()=>Manager.State.Jobs.GroupBy(j=>j.QueueId).Where(g=>Manager.IsArchived(g.Key)==showArchived).Select(g=>new QueueTile(g.Key,g.First().QueueName,(showArchived?"Archived · ":"")+RunSummary(g.ToList()),showArchived,Manager.CanManageRun(g.Key))).Reverse().ToList();
    List<Job> ActiveJobs()=>Manager.State.Jobs.Where(j=>!Manager.IsArchived(j.QueueId)).ToList();
    void RefreshHistoryControls()
    {
        var count=Manager.State.ArchivedRuns.Count;
        HistoryHeading.Text=showArchived?"TUCKER ARCHIVE":"TUCKER HISTORY";
        ShowArchivedHistory.Content=showArchived?"Back":$"Archived ({count})";
        ShowArchivedHistory.Visibility=showArchived||count>0?Visibility.Visible:Visibility.Collapsed;
        ShowArchivedHistory.ToolTip=showArchived?"Return to active Tucker history":"View archived runs and restore them";
        var archived=QueueList.SelectedItem is QueueTile {Archived:true};
        RestoreRunButton.Visibility=archived?Visibility.Visible:Visibility.Collapsed;
        RestoreRunButton.IsEnabled=archived&&!busy;
        if(archived){StartSelected.IsEnabled=false;AddMoreButton.IsEnabled=false;Summary.Text="Archived · "+Summary.Text;}
    }
    void ToggleArchiveHistory(object sender,RoutedEventArgs e){showArchived=!showArchived;Refresh();OpenRun(QueueList.Items.Cast<QueueTile>().FirstOrDefault()?.Id);}
    void HistoryRightClick(object sender,MouseButtonEventArgs e){if(sender is ListBoxItem item){item.IsSelected=true;item.Focus();}}
    void HistoryMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(sender is not ListBoxItem item)return;
        item.IsSelected=true;var menu=item.ContextMenu;menu.PlacementTarget=item;
        var archive=(MenuItem)menu.Items[0];var delete=(MenuItem)menu.Items[1];
        archive.Click-=ArchiveFromMenu;archive.Click+=ArchiveFromMenu;
        delete.Click-=DeleteFromMenu;delete.Click+=DeleteFromMenu;
        HistoryMenuOpened(menu,e);
    }
    void HistoryMenuOpened(object sender,RoutedEventArgs e)
    {
        if(sender is not ContextMenu menu||menu.PlacementTarget is not FrameworkElement {DataContext:QueueTile run})return;
        var archive=(MenuItem)menu.Items[0];var delete=(MenuItem)menu.Items[1];
        archive.Header=Manager.IsArchived(run.Id)?"Restore":"Archive";
        archive.IsEnabled=delete.IsEnabled=!busy&&Manager.CanManageRun(run.Id);
        var reason=archive.IsEnabled?null:"Pause or stop this Tucker before archiving or deleting it.";
        archive.ToolTip=delete.ToolTip=reason;
    }
    void ArchiveFromMenu(object sender,RoutedEventArgs e){if(sender is FrameworkElement {DataContext:QueueTile run})ChangeArchive(run);}
    void RestoreSelected(object sender,RoutedEventArgs e){if(QueueList.SelectedItem is QueueTile run)ChangeArchive(run);}
    void ChangeArchive(QueueTile run)
    {
        if(busy)return;
        try{var archive=!Manager.IsArchived(run.Id);Manager.SetArchived(run.Id,archive);showArchived=false;Refresh();OpenRun(archive?QueueList.Items.Cast<QueueTile>().FirstOrDefault()?.Id:run.Id);Status.Text=archive?$"Archived {run.Name}. Restore it from Archived to process it again.":$"Restored {run.Name}. Processing starts only when you choose Start.";}catch(Exception ex){Error(ex);}
    }
    void DeleteFromMenu(object sender,RoutedEventArgs e)
    {
        if(busy||sender is not FrameworkElement {DataContext:QueueTile run})return;
        if(!Manager.CanManageRun(run.Id)){Status.Text="Pause or stop this Tucker before deleting it.";return;}
        var confirmed=confirmRunDeletion?.Invoke(run.Name)??MessageBox.Show(this,$"Delete \"{run.Name}\" from Tucker? Its saved run and pending work will be removed. Raw files, completed media and inbox receipts are kept.","Delete Tucker run",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
        if(!confirmed)return;
        try{Manager.DeleteRun(run.Id);Refresh();OpenRun(QueueList.Items.Cast<QueueTile>().FirstOrDefault()?.Id);Status.Text=$"Deleted {run.Name} from Tucker. Media files were kept.";}catch(Exception ex){Error(ex);}
    }
}
