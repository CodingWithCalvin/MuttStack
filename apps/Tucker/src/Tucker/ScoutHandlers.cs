using System.Windows;

namespace Tucker;

public partial class MainWindow
{
    ScoutOutbox? scoutOutbox;
    void InitializeScoutOutbox()
    {
        try { scoutOutbox = new(Manager.Database.Path); RefreshScoutDelivery(true); }
        catch (Exception ex) { ScoutSendSummary.Text = "Scout handoffs unavailable: " + ex.Message; }
    }
    void RefreshScoutDelivery(bool retry = false)
    {
        if (scoutOutbox == null) { SendToScoutButton.IsEnabled = false; return; }
        try
        {
            if (retry) scoutOutbox.RetryPending();
            var jobs = SelectedJobs();
            var deliveries = jobs.Count == 0 ? [] : scoutOutbox.Deliveries(jobs[0].QueueId);
            SendToScoutButton.IsEnabled = !busy && (scoutOutbox.HasUnsent(jobs) || deliveries.Any(d => d.Status == "Not delivered"));
            ScoutSendSummary.Text = deliveries.Count == 0 ? "Send completed outputs to Scout when you're ready." :
                string.Join(" · ", deliveries.GroupBy(d => d.Status).Select(g => $"{g.Sum(d => d.Files)} {g.Key switch { "Offered" => "awaiting Scout", "Accepted" => "accepted by Scout", "Declined" => "declined by Scout", _ => "waiting to send" }}"));
        }
        catch (Exception ex) { ScoutSendSummary.Text = "Scout handoff needs attention: " + ex.Message; }
    }
    void SendToScout(object sender, RoutedEventArgs e)
    {
        if (busy || scoutOutbox == null || QueueList.SelectedItem is not QueueTile run) return;
        try
        {
            var id = scoutOutbox.SendCompleted(run.Id, SelectedJobs());
            Status.Text = id == null ? "No additional completed outputs to send." : "Sent to Scout's inbox. Scout can accept it whenever it opens.";
        }
        catch (Exception ex) { Error(ex); }
        RefreshScoutDelivery();
    }
}
