using System.Windows;
using System.Windows.Controls;
namespace Ripley;

public partial class MainWindow
{
    string currentPage="Rip";
    void InitializeShell()
    {
        VersionText.Text="Mutt Stack · "+typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        ShowPage("Rip");
    }
    void ShowPage(string page)
    {
        currentPage=page=="Settings"?"Settings":"Rip";
        RipPage.Visibility=currentPage=="Rip"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=currentPage=="Settings"?Visibility.Visible:Visibility.Collapsed;
        NavSettings.Style=(Style)FindResource(currentPage=="Settings"?"NavActive":"NavButton");
        (PageHeading.Text,PageSubtitle.Text)=currentPage=="Settings"
            ?("Settings","Choose MakeMKV and the shared metadata credential database.")
            :(currentRipSession?.Title??"New Ripley","Save raw files and disc metadata, then send the completed run to Tucker.");
        RefreshShell();
    }
    void RefreshShell()
    {
        UpdateOutputSummary();
        RefreshHistoryControls();
        if(currentPage=="Rip")PageSubtitle.Text=RipLocked?LockedRipMessage:"Save raw files and disc metadata, then send the completed run to Tucker.";
        NewRipleyButton.IsEnabled=!discBusy&&!adding;
        RipSetup.IsEnabled=!discBusy&&!adding&&EditableRip;
        InspectButton.IsEnabled=RipButton.IsEnabled=!discBusy&&!adding&&!driveScanRunning&&EditableRip;
        RefreshButton.IsEnabled=DriveChoice.IsEnabled=!driveScanRunning&&!discBusy;
        RefreshButton.Content=driveScanRunning?"Refreshing…":"Refresh";
        RipActions.Visibility=currentPage=="Rip"?Visibility.Visible:Visibility.Collapsed;
        SaveSettingsButton.Visibility=currentPage=="Settings"?Visibility.Visible:Visibility.Collapsed;
        DiscActivity.Visibility=currentPage=="Rip"||discBusy?Visibility.Visible:Visibility.Collapsed;
        RipProgress.Visibility=discBusy?Visibility.Visible:Visibility.Collapsed;
        CancelRipButton.Visibility=discBusy?Visibility.Visible:Visibility.Collapsed;
        CancelRipButton.IsEnabled=discBusy;
        SendTuckerButton.Visibility=lastRipFiles.Count>0?Visibility.Visible:Visibility.Collapsed;
        SendTuckerButton.Content=currentRipSession?.HandoffStatus switch {
            "Offered"=>"Awaiting Tucker","Accepted"=>"Accepted by Tucker","Declined"=>"Declined by Tucker","Not delivered"=>"Retry send to Tucker",
            _=>currentRipSession?.CompressionOutcome=="Completed"?"Compression completed":currentRipSession?.QueuedAtUtc!=null?"Previously queued":"Send to Tucker"
        };
        SendTuckerButton.IsEnabled=!discBusy&&!adding&&currentRipSession is {Status:"Completed",Archived:false,QueuedAtUtc:null,CompressionOutcome:not "Completed"} run&&run.HandoffStatus is "" or "Not delivered";
    }
    void UpdateOutputSummary()
    {
        if(OutputSummary!=null)OutputSummary.Text="Raw folder: "+RawPathPreview.Text;
    }
    void Navigate(object sender,RoutedEventArgs e)=>ShowPage((string)((Button)sender).Tag);
    void SavePreferences(object sender,RoutedEventArgs e){if(Save())Status.Text="Settings saved.";}
    void MinimizeWindow(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    void ToggleMaximize(object sender,RoutedEventArgs e)
    {
        if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    void CloseWindow(object sender,RoutedEventArgs e)=>Close();
}
