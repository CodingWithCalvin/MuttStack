using System.Windows;
using Scout.Core;

namespace Scout.Desktop;

public partial class MainWindow
{
    int modelActivityGeneration;
    private IProgress<ModelActivity> BeginModelActivity()
    {
        ClearModelActivity();
        var generation = modelActivityGeneration;
        return new ModelActivityObserver(update => {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => {
                if (generation == modelActivityGeneration && operation != null) ShowModelActivity(update);
            }));
        });
    }
    private sealed class ModelActivityObserver(Action<ModelActivity> report) : IProgress<ModelActivity>
    { public void Report(ModelActivity value) => report(value); }
    private void ShowModelActivity(ModelActivity update)
    {
        ModelActivityPanel.Visibility = Visibility.Visible;
        ModelActivityHeading.Text = update.Heading;
        ModelActivitySource.Text = update.Source;
        ModelActivitySource.Visibility = update.Source.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ModelActivityDetail.Text = update.Detail;
        var text = update.Heading + (update.Source.Length == 0 ? "" : " · " + update.Source) + " · " + update.Detail;
        if (!update.IsHeartbeat) Log(text);
        else if (!forceStopRequested && pauseRequest?.IsRequested != true) Status.Text = text;
    }
    private void ClearModelActivity()
    {
        modelActivityGeneration++;
        ModelActivityPanel.Visibility = Visibility.Collapsed;
        ModelActivityHeading.Text = ModelActivitySource.Text = ModelActivityDetail.Text = "";
    }
}
