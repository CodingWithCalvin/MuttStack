using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow {
    void LoadSpeechSettings() {
        SpeechEnabledEntry.IsChecked=appSettings.Speech.Enabled;
        SpeechModelChoice.ItemsSource=SpeechModels.Profiles;
        SpeechModelChoice.SelectedItem=SpeechModels.Profiles.First(p=>p.Id==appSettings.Speech.Model);
        SpeechDeviceChoice.SelectedIndex=appSettings.Speech.Device=="cuda"?1:0;
        RefreshSpeechSettings();
    }
    SpeechSettings ReadSpeechSettings()=>appSettings.Speech with {
        Enabled=SpeechEnabledEntry.IsChecked==true,
        Model=(SpeechModelChoice.SelectedItem as SpeechProfile)?.Id??"parakeet-v3",
        Device=SpeechDeviceChoice.SelectedIndex==1?"cuda":"cpu"
    };
    void SpeechSelectionChanged(object sender,SelectionChangedEventArgs e)=>RefreshSpeechSettings();
    void RefreshSpeechSettings() {
        if(SpeechModelChoice?.SelectedItem is not SpeechProfile profile||SpeechDeviceChoice==null||ModelFolderEntry==null||SpeechModelStatus==null)return;
        try {
            var s=ReadSpeechSettings();
            SpeechModelStatus.Text=SpeechModels.Installation(s,ModelFolderEntry.Text.Trim());
            SpeechModelHint.Text=(profile.Engine=="parakeet"?"Recommended from the local benchmark. ":"Includes speech detection to reduce invented text during silence. ")+
                $"Model and runtime download: {(profile.Model.Bytes+SpeechModels.Runtime(s).Bytes+(profile.Engine=="whisper"?SpeechModels.Vad.Bytes:0))/1e9:0.00} GB. "+
                (s.Device=="cuda"?"Requires an NVIDIA GPU with a compatible driver.":"CPU works without an NVIDIA GPU; GPU processing is faster on supported hardware.");
        } catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){SpeechModelStatus.Text=ex.Message;}
    }
    async void DownloadSpeechModel(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        try {
            var settings=SaveSettingsCore();operation=new();settingsDownload=true;SetBusy(true);SettingsCancelButton.Visibility=Visibility.Visible;
            using var http=new HttpClient{Timeout=TimeSpan.FromHours(12)};
            var progress=new Progress<string>(message=>SettingsStatus.Text=message);
            await Task.Run(()=>SpeechModels.EnsureAsync(settings.Speech,settings.ModelDirectory,true,http,progress,operation.Token));
            SettingsStatus.Text="Speech model verified and ready. Resume unfinished files, or reanalyze to replace saved transcription with the selected model.";
        } catch(OperationCanceledException) when(operation?.IsCancellationRequested==true){SettingsStatus.Text="Speech download cancelled. Completed verified assets are retained.";}
        catch(Exception ex){SettingsStatus.Text="Speech model preparation failed: "+ex.Message;}
        finally{operation?.Dispose();operation=null;settingsDownload=false;SetBusy(false);SettingsCancelButton.Visibility=Visibility.Collapsed;RefreshSpeechSettings();}
    }
}
