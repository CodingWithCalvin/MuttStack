using System.IO;
using System.Windows;
using System.Security.Cryptography;
using System.Text;
namespace Tucker;
public partial class App : Application
{
    Mutex? instance;
    Doggo.StorageLease? storageLease;
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        try {
            var test=Array.IndexOf(e.Args,"--state");
            var root=test>=0&&test+1<e.Args.Length?Path.GetFullPath(e.Args[test+1]):AppStorage.Prepare(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var database=test>=0?Path.Combine(root,"doggo.db"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Doggo","doggo.db");
            var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.ToUpperInvariant())))[..20];
            instance=new Mutex(true,"Local\\Tucker."+key,out var created);
            if(!created){MessageBox.Show("Tucker is already open for this library.","Tucker");Shutdown();return;}
            storageLease=Doggo.StorageLease.Acquire(false,database);
            MainWindow=new MainWindow(root,database);MainWindow.Show();
        }catch(Exception ex){MessageBox.Show("Could not open Tucker. Existing data was kept.\n\n"+ex.Message,"Tucker",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
    }
    protected override void OnExit(ExitEventArgs e){storageLease?.Dispose();instance?.Dispose();base.OnExit(e);}
}
