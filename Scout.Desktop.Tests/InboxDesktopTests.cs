using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Doggo;
using Scout.Core;
using Scout.Desktop;

static class InboxDesktopTests
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "inbox"); Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings"); var store = new WorkspaceStore(Path.Combine(root, "workspaces"));
        Environment.SetEnvironmentVariable("SCOUT_SETTINGS", settings); Environment.SetEnvironmentVariable("SCOUT_WORKSPACES", store.Root);
        var db = new ScoutDatabase(Path.Combine(root, "doggo.db"), true);
        ScoutStorage.Register(settings, db); ScoutStorage.Register(store.Root, db);
        new SettingsStore(settings).Save(new AppSettings { ModelDirectory = Path.Combine(root, "models") });
        var transport = new RoutedHandoffs(db.Path);
        string Offer(string name)
        {
            var path = Path.Combine(root, name + ".mkv"); File.WriteAllText(path, "synthetic"); var stat = new FileInfo(path);
            var b = new ScoutHandoff(Guid.NewGuid().ToString("N"), "tucker-run", "Example Show · Season 2",
                [new(Guid.NewGuid().ToString("N"), path, stat.Length, stat.LastWriteTimeUtc, new("tmdb", "42", "Example Show", "2020"))]);
            var payload = b.Serialize(); transport.Offer(b.Id, ScoutHandoff.Digest(payload), payload); return b.Id;
        }
        var id = Offer("episode"); MainWindow? window = null;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
        try
        {
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            T C<T>(string name) => (T)window.FindName(name);
            void Click(string name) => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(C<TextBlock>("InboxCount").Text == "1" && C<Border>("InboxBadge").Visibility == Visibility.Visible, "Scout shows a waiting handoff badge at startup");
            Click("NavInbox"); C<ListBox>("HandoffList").SelectedIndex = 0;
            Check(C<Grid>("InboxPage").Visibility == Visibility.Visible && C<Button>("AcceptHandoffButton").IsEnabled && C<TextBox>("HandoffDetails").Text.Contains("episode.mkv"), "Scout inbox previews exact compressed output before acceptance");
            var renderDirectory = Environment.GetEnvironmentVariable("SCOUT_TEST_RENDERS");
            if (!string.IsNullOrWhiteSpace(renderDirectory)) Render(window, Path.Combine(renderDirectory, "scout-inbox.png"));
            Click("AcceptHandoffButton"); var run = store.ReadAll().Single();
            Check(run.Status == "Draft" && run.SeriesIdentity?.Provider == "tmdb" && run.SelectedFiles.Count == 1 && C<Border>("InboxBadge").Visibility == Visibility.Collapsed, "Accept saves a waiting Scout and clears the inbox badge");
            C<ListBox>("HistoryList").SelectedItem = C<ListBox>("HistoryList").Items.Cast<RunWorkspace>().Single(r => r.Id == run.Id);
            Check(C<TextBlock>("SelectionSummary").Text.Contains("episode.mkv") && store.ReadAll().Single().SeriesIdentity?.Provider == "tmdb", "Opening accepted Scout restores its exact selection and provider");
            var declined = Offer("decline");
            typeof(MainWindow).GetMethod("RefreshInbox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Click("NavInbox"); C<ListBox>("HandoffList").SelectedIndex = 0; Click("DeclineHandoffButton");
            Check(transport.Inbox("scout").Single(b => b.Id == declined).Status == "Declined" && store.ReadAll().Count == 1, "Decline reports a durable response without creating a Scout");
            window.Close(); window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            Check(C<ListBox>("HandoffList").Items.Count == 0 && store.ReadAll().Single().Status == "Draft", "Reopened Scout keeps the acceptance without duplicate work or automatic processing");
        }
        finally { window?.Close(); ScoutStorage.Unregister(settings); ScoutStorage.Unregister(store.Root); }
    }
    static void Render(MainWindow window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); window.ApplyTemplate(); var root = (FrameworkElement)window.Content;
        if (root is Panel panel) panel.Background = window.Background;
        root.Measure(new Size(1080, 740)); root.Arrange(new Rect(0, 0, 1080, 740)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
