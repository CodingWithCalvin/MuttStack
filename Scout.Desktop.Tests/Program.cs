using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Reflection;
using Scout.Core;
using Scout.Desktop;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0)
            {
                if (args.Length != 2 || args[0] != "--app-directory")
                    throw new ArgumentException("Usage: Scout.Desktop.Tests [--app-directory <published app folder>]");
                var directory = Path.GetFullPath(args[1]);
                foreach (var name in new[] { "Scout.Core.dll", "Scout.dll" })
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, name));
                Console.WriteLine("Checking published app: " + directory);
            }
            return Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // Resolve the requested published assemblies before compiling the WPF check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Scout-desktop-tests-" + Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(root, "settings");
        var workspaces = Path.Combine(root, "workspaces");
        Environment.SetEnvironmentVariable("SCOUT_SETTINGS", settings);
        Environment.SetEnvironmentVariable("SCOUT_WORKSPACES", workspaces);
        try
        {
            var database = new ScoutDatabase(Path.Combine(root, "scout.db"));
            ScoutStorage.Register(settings, database);
            ScoutStorage.Register(workspaces, database);
            new SettingsStore(settings).Save(new AppSettings { ModelDirectory = Path.Combine(root, "models") });
            var store = new WorkspaceStore(workspaces);
            var selected = new RunWorkspace { Name = "Selected Scout", Library = root, Status = "Finished" };
            var other = new RunWorkspace { Name = "Other Scout", Library = root, Status = "Finished" };
            store.Save(selected);
            store.Save(other);
            var app = new App { IsolatedStartup = true };
            app.InitializeComponent();
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            window.Show();
            window.UpdateLayout();
            void Pump() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Pump();
            Console.WriteLine("PASS Scout window initializes and lays out with saved history.");
            TmdbDesktopTests.Run(root);
            var media=Path.Combine(root,"selection-media");var seasonFolder=Path.Combine(media,"Season 02");
            Directory.CreateDirectory(seasonFolder);Directory.CreateDirectory(Path.Combine(media,"Season 03"));
            var first=Path.Combine(seasonFolder,"one.mkv");var second=Path.Combine(seasonFolder,"two.mkv");var third=Path.Combine(media,"Season 03","three.mkv");
            foreach(var file in new[]{first,second,third})File.WriteAllText(file,"synthetic selector fixture");
            foreach(var (scope,expected) in new[]{(new IdentificationScope("season",seasonFolder),2),(new IdentificationScope("show"),3),(new IdentificationScope("files"),1)}) {
                var picker=new VideoSelectionWindow(media,[first],scope);
                var chosen=(List<string>)typeof(VideoSelectionWindow).GetMethod("Selection",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(picker,null)!;
                Assert(chosen.Count==expected&&chosen.Contains(first),"Saved identification scope must restore the season/show/files selector.");
                picker.Close();
            }
            Console.WriteLine("PASS Identification selector restores saved season, full-show and individual-file scopes.");
            var keyStore=new SettingsStore(settings);keyStore.SetKey("metadata|tvdb","other-provider-key");
            var scoutsBefore=System.Text.Json.JsonSerializer.Serialize(store.ReadAll(includeArchived:true),Data.Options);
            var keyEntry=(PasswordBox)window.FindName("SubdlKeyEntry");
            var keyStatus=(TextBlock)window.FindName("SubdlKeyStatus");
            var saveKey=(Button)window.FindName("SubdlSaveButton");
            keyEntry.Password="fake-subdl-ui-key";saveKey.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(keyEntry.Password==""&&keyStatus.Text=="Key configured","SubDL must clear the input and retain its configured indicator.");
            saveKey.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(SubtitleCredentials.ReadSubdl(database.Path,settings)=="fake-subdl-ui-key","A blank save must retain the protected key.");
            keyEntry.Password="replacement-subdl-key";saveKey.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            typeof(MainWindow).GetMethod("LoadSettings",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            Assert(keyEntry.Password==""&&keyStatus.Text=="Key configured"&&keyStore.ReadKey(SubtitleCredentials.Subdl)=="replacement-subdl-key","The saved key status must survive settings reload.");
            ((Button)window.FindName("SubdlRemoveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(keyStatus.Text=="Key not configured"&&keyStore.ReadKey(SubtitleCredentials.Subdl)==null&&keyStore.ReadKey("metadata|tvdb")=="other-provider-key","Removal must affect only SubDL.");
            Assert(scoutsBefore==System.Text.Json.JsonSerializer.Serialize(store.ReadAll(includeArchived:true),Data.Options),"Saving or removing a subtitle key must not change Scouts.");
            Console.WriteLine("PASS SubDL key save, blank retention, replacement, reload and removal preserve Scout history and other credentials.");
            var subtitleMatching=(CheckBox)window.FindName("SubtitleMatchingEntry");
            Assert(subtitleMatching.IsChecked==true,"Subtitle-first identification should be enabled by default.");
            subtitleMatching.IsChecked=false;
            var preferences=(AppSettings)typeof(MainWindow).GetMethod("ReadSettingsControls",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Assert(!preferences.UseSubtitleMatching,"Subtitle matching preference must be read from Settings.");
            keyStore.Save(preferences);
            typeof(MainWindow).GetMethod("LoadSettings",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            Assert(subtitleMatching.IsChecked==false,"Subtitle matching preference must survive reload.");
            Assert(scoutsBefore==System.Text.Json.JsonSerializer.Serialize(store.ReadAll(includeArchived:true),Data.Options),"Subtitle settings must not alter Scout history.");
            Console.WriteLine("PASS Subtitle-first preference persists without changing saved Scouts.");
            var history = (ListBox)window.FindName("HistoryList");
            ListBoxItem Row(string id)
            {
                var item = history.Items.Cast<RunWorkspace>().SingleOrDefault(w => w.Id == id)
                    ?? throw new Exception($"History is missing {id}; visible: {string.Join(", ", history.Items.Cast<RunWorkspace>().Select(w => w.Id + " " + w.Name))}; saved: {string.Join(", ", store.ReadAll(includeArchived: true).Select(w => w.Id + " " + w.Name + " " + w.Library))}");
                history.ScrollIntoView(item);
                Pump();
                return (ListBoxItem)history.ItemContainerGenerator.ContainerFromItem(item);
            }
            ContextMenu OpenMenu(ListBoxItem row)
            {
                var menu = row.ContextMenu ?? throw new Exception("History row has no context menu.");
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                Pump();
                Assert(menu.DataContext == row.DataContext, "Menu must target its own row.");
                Assert(menu.Items.Count == 2 && ((MenuItem)menu.Items[1]).Header.Equals("Delete…"), "Archive and delete must both be available.");
                return menu;
            }
            var firstRow = Row(selected.Id);
            var otherRow = Row(other.Id);
            Assert(!ReferenceEquals(firstRow.ContextMenu, otherRow.ContextMenu), "Rows must not share a menu.");
            history.SelectedItem = firstRow.DataContext;
            Pump();
            var archiveMenu = OpenMenu(Row(other.Id));
            Assert(((MenuItem)archiveMenu.Items[0]).Header.Equals("Archive"), "Unarchived Scout must offer Archive.");
            archiveMenu.IsOpen = false;
            ((MenuItem)archiveMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            Assert(store.ReadAll(includeArchived: true).Single(w => w.Id == other.Id).Archived, "Clicked Scout was not archived.");
            Assert(!store.ReadAll(includeArchived: true).Single(w => w.Id == selected.Id).Archived, "Archiving affected the selected Scout instead.");
            Assert(history.Items.Count == 1, "Archived Scout must be hidden by default.");
            Console.WriteLine("PASS History menu archives the clicked Scout without changing the selected Scout.");
            var filter = (CheckBox)window.FindName("ShowArchivedScouts");
            filter.IsChecked = true;
            filter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var restoreMenu = OpenMenu(Row(other.Id));
            Assert(((MenuItem)restoreMenu.Items[0]).Header.Equals("Restore from archive"), "Archived Scout must offer Restore.");
            restoreMenu.IsOpen = false;
            ((MenuItem)restoreMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            Assert(!store.ReadAll(includeArchived: true).Single(w => w.Id == other.Id).Archived, "Restore did not persist.");
            Console.WriteLine("PASS Archived Scout can be shown and restored from its menu.");
            var runningRow = Row(other.Id);
            ((RunWorkspace)runningRow.DataContext).Status = "Running";
            var runningMenu = OpenMenu(runningRow);
            Assert(runningMenu.Items.Cast<MenuItem>().All(i => !i.IsEnabled), "Running Scout actions must be disabled.");
            runningMenu.IsOpen = false;
            Console.WriteLine("PASS Running Scout cannot be archived or deleted from the menu.");
            void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
            object? Call(string name, params object[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
            var active = (RunWorkspace)typeof(MainWindow).GetField("workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            active.Status = "Running";
            active.Model = "test-model";
            active.SelectedFiles = [Path.Combine(root, "unfinished.mkv")];
            var pause = new ScoutPause();
            using (var stop = new CancellationTokenSource())
            {
                Set("operation", stop);
                Set("pauseRequest", pause);
                try
                {
                    Call("SetBusy", true);
                    var pauseButton = (Button)window.FindName("CancelButton");
                    var forceButton = (Button)window.FindName("ForceStopButton");
                    Assert(forceButton.Visibility == Visibility.Visible && forceButton.IsEnabled, "Identification must offer Force stop.");
                    pauseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert(pause.IsRequested && !stop.IsCancellationRequested, "Pause must finish the current file without cancelling it.");
                    Assert(!pauseButton.IsEnabled && forceButton.IsEnabled, "Force stop must remain available after Pause is queued.");
                    Call("ShowPage", "Settings");
                    Assert(forceButton.IsVisible && forceButton.IsEnabled, "Force stop must remain accessible from Settings.");
                    forceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert(stop.IsCancellationRequested && !forceButton.IsEnabled && !pauseButton.IsEnabled, "Force stop must cancel immediately and prevent repeated commands.");
                    forceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Call("Log", "Late progress update");
                    Assert(((TextBlock)window.FindName("Status")).Text.StartsWith("Force stop requested"), "Late progress must not overwrite the stop acknowledgement.");
                    Call("FinishPaused", true);
                    Assert(active.Status == "Paused", "Force stop must leave Scout resumable.");
                }
                finally
                {
                    Set("operation", null);
                    Set("pauseRequest", null);
                    Set("forceStopRequested", false);
                    Call("SetBusy", false);
                }
                Call("ShowPage", "Activity");
                Assert(((Button)window.FindName("NextStepButton")).Content.Equals("Resume Scout"), "Stopped Scout must offer Resume Scout.");
                Assert(((Button)window.FindName("ForceStopButton")).Visibility == Visibility.Collapsed, "Force stop must hide when idle.");
                Assert(((TextBlock)window.FindName("PauseNotice")).Text.StartsWith("Scout stopped."), "Finished cancellation must be acknowledged.");
            }
            Console.WriteLine("PASS Force stop stays available after Pause, cancels immediately, and offers Resume Scout.");
            using (var otherOperation = new CancellationTokenSource())
            {
                Set("operation", otherOperation);
                try
                {
                    Call("SetBusy", true);
                    Assert(((Button)window.FindName("ForceStopButton")).Visibility == Visibility.Collapsed, "Other operations must retain their existing cancel action.");
                }
                finally { Set("operation", null); Call("SetBusy", false); }
            }
            Console.WriteLine("PASS Force stop is limited to resumable identification work.");
            ModelActivityDesktopTests.Run(window,store);
            RunOptionsDesktopTests.Run(window,store);
            DestinationSettingsDesktopTests.Run(window,store);
            DialogueAvailabilityDesktopTests.Run(window,store);
            SpeechSettingsDesktopTests.Run(window);
            window.Close();
            InboxDesktopTests.Run(root);
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            ScoutStorage.Unregister(settings);
            ScoutStorage.Unregister(workspaces);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

}
