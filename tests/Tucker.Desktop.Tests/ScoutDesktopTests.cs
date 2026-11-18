using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Doggo;
using Tucker;

static class ScoutDesktopTests
{
    public static async Task<int> Run(string root, string? renders)
    {
        var home = Path.Combine(root, "scout-handoff-ui"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "doggo.db"); var db = new DoggoDatabase(path);
        var output = Path.Combine(home, "compressed"); Directory.CreateDirectory(output);
        var job = new Job { QueueId = "series-run", QueueName = "Example Show · Season 2", Source = Path.Combine(home, "raw.mkv"),
            Name = "episode.mkv", Status = "Completed", Encoding = new("", "", "", false, output, "mkv"), Series = new("tvdb", "42", "Example Show", "2020", "") };
        File.WriteAllText(job.OutputPath, "synthetic compressed"); File.WriteAllText(job.Source, "raw");
        db.Save(new QueueState { Jobs = [job] });
        var window = new MainWindow(home, path); var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        T C<T>(string name) => (T)window.FindName(name);
        try
        {
            Check(C<Button>("SendToScoutButton").IsEnabled, "Completed Tucker exposes Send to Scout");
            C<Button>("SendToScoutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(C<TextBlock>("ScoutSendSummary").Text == "1 awaiting Scout" && !C<Button>("SendToScoutButton").IsEnabled && !window.Manager.Running, "Send action shows durable awaiting status without starting processing");
            if (renders != null) await Render(window, Path.Combine(renders, "tucker-send-to-scout.png"));
            window.Close();
            var row = new RoutedHandoffs(path).Inbox("scout").Single();
            // The receiver's atomic transaction is covered in Scout.Core and the cross-process rehearsal.
            using (var sql = new Sqlite(path)) sql.Exec("UPDATE handoff_routed_batches SET status='Accepted' WHERE id=?", row.Id);
            window = new MainWindow(home, path);
            Check(C<TextBlock>("ScoutSendSummary").Text == "1 accepted by Scout" && !C<Button>("SendToScoutButton").IsEnabled, "Reopened Tucker displays Scout's acceptance without resending");
            window.Manager.State.Jobs.Add(new Job { QueueId = job.QueueId, QueueName = job.QueueName, Status = "Waiting", Source = "waiting.mkv" });
            typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(!C<Button>("SendToScoutButton").IsEnabled, "Pending additions do not make unfinished outputs eligible for Scout");
        }
        finally { window.Close(); }
        return passed;
    }
    static async Task Render(MainWindow window, string path)
    {
        window.ApplyTemplate(); var root = (FrameworkElement)window.Content; root.Measure(new Size(1080, 740)); root.Arrange(new Rect(0, 0, 1080, 740)); root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
