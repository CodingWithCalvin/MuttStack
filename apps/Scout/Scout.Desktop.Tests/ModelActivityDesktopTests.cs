using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scout.Core;
using Scout.Desktop;

static class ModelActivityDesktopTests
{
    public static void Run(MainWindow window, WorkspaceStore store)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        T C<T>(string name) => (T)window.FindName(name);
        void Pump() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Check(bool yes, string message) { if (!yes) throw new Exception(message); Console.WriteLine("PASS " + message); }
        using var operation = new CancellationTokenSource();
        Set("operation", operation); Set("pauseRequest", null); Set("forceStopRequested", false);
        try {
            Call("SetBusy", true); Call("ShowPage", "Activity");
            var progress = (IProgress<ModelActivity>)Call("BeginModelActivity")!;
            progress.Report(new(3, "Verifying guide evidence · pass 1/2", "[2/10] Example Show - Season 01 - Episode 02.mkv", "Waiting for model response", TimeSpan.Zero)); Pump();
            Check(C<Border>("ModelActivityPanel").Visibility == Visibility.Visible && C<TextBlock>("ModelActivityHeading").Text.Contains("Verifying guide evidence") && C<TextBlock>("ModelActivitySource").Text.Contains("[2/10]"), "Model activity identifies the current file, request, and verification stage");
            var history = JsonSerializer.Serialize(store.ReadAll(includeArchived: true)); var log = C<ActivityLogView>("ActivityLog").Text;
            progress.Report(new(3, "Verifying guide evidence · pass 1/2", "[2/10] Example Show - Season 01 - Episode 02.mkv", "Reading model input · 12,288 tokens processed", TimeSpan.FromSeconds(65), true)); Pump();
            Check(C<TextBlock>("ModelActivityDetail").Text.Contains("65s elapsed") && C<TextBlock>("Status").Text.Contains("Reading model input") && C<ActivityLogView>("ActivityLog").Text == log && JsonSerializer.Serialize(store.ReadAll(includeArchived: true)) == history, "Live model counters update without filling saved history or the activity log");
            if (Environment.GetEnvironmentVariable("SCOUT_TEST_RENDERS") is string render) {
                var activityLog = C<ActivityLogView>("ActivityLog");
                activityLog.Text = "Reading dialogue [1/3] Example-S01E01.mkv\n" +
                    "Match found: Example-S01E01.mkv → S01E01 — Opening Night (subtitles; collection checks pending).\n" +
                    "Subtitle match unresolved: no reference passed the dialogue comparison.\n" +
                    "10:15  Saved match: Example-S01E02.mkv → S01E02 — The Long Journey Home (AI; collection checks pending).\n" +
                    "Held for review: Example-S01E03.mkv — competing episode claims.\n" +
                    "10:16  Matched: Example-S01E01.mkv → S01E01 — Opening Night (collection checks complete).";
                Directory.CreateDirectory(render); var root = (FrameworkElement)window.Content;
                if (root is Panel panel) panel.Background = window.Background;
                root.Measure(new Size(1080, 740)); root.Arrange(new Rect(0, 0, 1080, 740)); root.UpdateLayout();
                var image = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(render, "scout-model-activity.png")); encoder.Save(file);
                activityLog.Text = log;
            }
            var pause = new ScoutPause(); pause.Request(); Set("pauseRequest", pause); C<TextBlock>("Status").Text = "Pause requested";
            progress.Report(new(3, "Verifying guide evidence", "file.mkv", "Generating response · 8 tokens generated", TimeSpan.FromSeconds(70), true)); Pump();
            Check(C<TextBlock>("Status").Text == "Pause requested" && C<TextBlock>("ModelActivityDetail").Text.Contains("Generating response"), "Model progress preserves the pending pause acknowledgment");
            Set("pauseRequest", null); Set("operation", null); Call("SetBusy", false); C<TextBlock>("Status").Text = "Matching complete";
            progress.Report(new(3, "old request", "old file", "late update", TimeSpan.Zero, true)); Pump();
            Check(C<Border>("ModelActivityPanel").Visibility == Visibility.Collapsed && C<TextBlock>("Status").Text == "Matching complete", "Late model updates cannot overwrite a finished run or reopen its activity panel");
        } finally { Set("operation", null); Set("pauseRequest", null); Call("SetBusy", false); }
    }
}
