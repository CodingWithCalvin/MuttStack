using System.Windows;
using System.IO;
using System.Diagnostics;
using Scout.Core;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Scout.Desktop.Tests")]
namespace Scout.Desktop;
public partial class App : Application
{
    // The desktop regression harness supplies its own temporary storage.
    internal bool IsolatedStartup { get; init; }
    Doggo.StorageLease? selectionLease, databaseLease;
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        if(IsolatedStartup)return;
        try {
            if(e.Args.FirstOrDefault()=="--preview") {
                if(e.Args.Length!=6)throw new ArgumentException("--preview report.json root series output catalog.json");
                var plan=PreviewPlanner.Create(Data.Read(e.Args[1]),e.Args[2],e.Args[3],Data.Catalog(e.Args[5]));
                PreviewPlanner.Write(plan,e.Args[4]);Shutdown(0);return;
            }
            if(!ClaimInstance(e.Args))return;
            using var current=Process.GetCurrentProcess();
            if(Process.GetProcessesByName(current.ProcessName).Any(p=>p.Id!=current.Id&&p.SessionId==current.SessionId&&p.StartTime<current.StartTime))throw new InvalidOperationException("Close the other Scout window before opening this build. Scout uses one shared database.");
            var explicitRoot=Environment.GetEnvironmentVariable("SCOUT_DATA_ROOT");
            if(explicitRoot!=null&&!Path.IsPathFullyQualified(explicitRoot))throw new ArgumentException("SCOUT_DATA_ROOT must be an absolute path.");
            var dataRoot=explicitRoot??Doggo.ScoutStorageSelection.DefaultRoot;
            selectionLease=Doggo.StorageLease.Acquire(false,Doggo.ScoutStorageSelection.ConfigPath(dataRoot));
            var choice=Doggo.ScoutStorageSelection.Resolve(dataRoot,
                explicitRoot==null?Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial"):null,
                Environment.GetEnvironmentVariable("SCOUT_DOGGO_DATABASE"),
                Environment.GetEnvironmentVariable("SCOUT_SETTINGS"),
                Environment.GetEnvironmentVariable("SCOUT_WORKSPACES")??Environment.GetEnvironmentVariable("EPISCOUT_WORKSPACES"));
            databaseLease=Doggo.StorageLease.Acquire(false,choice.DatabasePath);
            var db=choice.UsesDoggo?ScoutDatabase.OpenExistingDoggo(choice.DatabasePath):new ScoutDatabase(choice.DatabasePath);
            var settings=choice.SettingsRoot;var workspaces=choice.WorkspacesRoot;
            Environment.SetEnvironmentVariable("SCOUT_SETTINGS",settings);
            Environment.SetEnvironmentVariable("SCOUT_WORKSPACES",workspaces);
            ScoutStorage.Register(settings,db);ScoutStorage.Register(workspaces,db);
            var window=new MainWindow();MainWindow=window;
            if(e.Args.Length==2&&e.Args[0]=="--report")window.LoadReport(e.Args[1]);
            window.Show();
            ListenForActivation(window);
            if(e.Args.Length==2&&e.Args[0]=="--scout")window.OpenTaskbarScout(e.Args[1]);
        }catch(Exception ex){Console.Error.WriteLine(ex);MessageBox.Show(ex.GetBaseException().Message,"Scout",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
    }
}
