using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using Scout.Desktop;

static class SpeechSettingsDesktopTests {
    public static void Run(MainWindow window) {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
        T C<T>(string name)=>(T)window.FindName(name);
        void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
        var settingsStore=(SettingsStore)typeof(MainWindow).GetField("settingsStore",flags)!.GetValue(window)!;var old=settingsStore.Load();
        try {
            var saved=settingsStore.Load();saved.Speech=new(){Model="whisper-turbo",Device="cuda",Enabled=false};settingsStore.Save(saved);Call("LoadSettings");Call("ShowPage","Settings");
            var tabs=C<TabControl>("SettingsTabs");tabs.SelectedItem=tabs.Items.Cast<TabItem>().Single(t=>Equals(t.Header,"Speech"));
            Check((C<ComboBox>("SpeechModelChoice").SelectedItem as SpeechProfile)?.Id=="whisper-turbo"&&C<ComboBox>("SpeechDeviceChoice").SelectedIndex==1&&C<CheckBox>("SpeechEnabledEntry").IsChecked==false,"Speech settings restore the model, device and enabled state");
            C<ComboBox>("SpeechModelChoice").SelectedItem=SpeechModels.Profiles[0];C<ComboBox>("SpeechDeviceChoice").SelectedIndex=0;C<CheckBox>("SpeechEnabledEntry").IsChecked=true;
            var read=(AppSettings)Call("ReadSettingsControls")!;Check(read.Speech.Model=="parakeet-v3"&&read.Speech.Device=="cpu"&&read.Speech.Enabled&&read.Provider==saved.Provider,"Speech selection stays independent of the episode-matching model");
            Call("SaveSettingsCore");Call("LoadSettings");Check(settingsStore.Load().Speech==read.Speech,"Saving speech selection persists across reload without downloading assets");
            Call("SetBusy",true);Check(!tabs.IsEnabled&&!C<Button>("SettingsSaveButton").IsEnabled,"Speech controls cannot change an active operation");Call("SetBusy",false);
            if(Environment.GetEnvironmentVariable("SCOUT_TEST_RENDERS") is string render) {
                Directory.CreateDirectory(render);window.Width=1420;window.Height=1000;window.UpdateLayout();C<Button>("SpeechDownloadButton").BringIntoView();
                window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var content=(FrameworkElement)window.Content;if(content is Panel panel)panel.Background=window.Background;
                var image=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(render,"speech-settings.png"));encoder.Save(file);
            }
        } finally {settingsStore.Save(old);Call("SetBusy",false);Call("LoadSettings");}
    }
}
