using Doggo;
using Scout.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Scout.Desktop;

public partial class MainWindow
{
    ScoutInbox? inbox;
    readonly DispatcherTimer handoffTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    void InitializeInbox()
    {
        try
        {
            if (ScoutStorage.For(workspaces.Root) is { UsesDoggo: true } database)
                inbox = new(database, workspaces.Root);
            RefreshInbox();
            handoffTimer.Tick += (_, _) => RefreshInbox();
            handoffTimer.Start();
            Closed += (_, _) => handoffTimer.Stop();
        }
        catch (Exception ex) { InboxSummary.Text = "Inbox unavailable: " + ex.Message; }
    }
    void RefreshInbox()
    {
        try
        {
            var selected = (HandoffList.SelectedItem as RoutedBatch)?.Id;
            var pending = inbox?.Items().Where(b => b.Status == "Offered").ToList() ?? [];
            InboxCount.Text = pending.Count.ToString();
            InboxBadge.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HandoffList.ItemsSource = pending;
            HandoffList.SelectedItem = pending.FirstOrDefault(b => b.Id == selected);
            InboxSummary.Text = inbox == null ? "The inbox becomes available when Scout uses the shared Mutt Stack database." :
                pending.Count == 0 ? "Nothing waiting. Tucker can send completed outputs while Scout is closed." :
                $"{pending.Count} {(pending.Count == 1 ? "batch" : "batches")} waiting. Accepting saves draft Scouts; processing starts when you choose Start Scout.";
            HandoffSelected(this, null!);
        }
        catch (Exception ex) { InboxSummary.Text = "Inbox could not refresh: " + ex.Message; }
    }
    void HandoffSelected(object sender, SelectionChangedEventArgs e)
    {
        AcceptHandoffButton.IsEnabled = DeclineHandoffButton.IsEnabled = inbox != null && operation == null && HandoffList.SelectedItem != null;
        try
        {
            HandoffDetails.Text = HandoffList.SelectedItem is RoutedBatch row ?
                string.Join("\n\n", row.Read().Files.Select(f => $"{f.Series?.Name ?? "Series not identified"}\n{f.Path}")) :
                "Select a handoff to review its compressed outputs.";
        }
        catch (Exception ex) { HandoffDetails.Text = ex.Message; AcceptHandoffButton.IsEnabled = false; }
    }
    void AcceptHandoff(object sender, RoutedEventArgs e)
    {
        if (operation != null || inbox == null || HandoffList.SelectedItem is not RoutedBatch row) return;
        try
        {
            var receipt = inbox.Accept(row.Id);
            RefreshHistory(); RefreshInbox();
            Status.Text = receipt.NewFiles == 0 ? "These outputs were already accepted. No duplicate Scouts were created." :
                "Accepted. Choose a saved Scout from history when you are ready to begin.";
        }
        catch (Exception ex) { Error(ex); }
    }
    void DeclineHandoff(object sender, RoutedEventArgs e)
    {
        if (operation != null || inbox == null || HandoffList.SelectedItem is not RoutedBatch row) return;
        try { inbox.Decline(row.Id); RefreshInbox(); Status.Text = "Handoff declined. Tucker will see the response when it next checks."; }
        catch (Exception ex) { Error(ex); }
    }
}
