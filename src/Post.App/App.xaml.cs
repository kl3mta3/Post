using Post.Core;
using System.Windows;

namespace Post.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Post reads, plays and exports everything through ffmpeg, and the main window asks
    /// for it while it is being built. So it is looked for first, and offered if missing,
    /// rather than letting the app fall over on startup with nothing on screen.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (FfmpegLocator.TryFind() is null)
        {
            var setup = new FfmpegSetupWindow();
            if (setup.ShowDialog() != true) { Shutdown(); return; }
        }

        new MainWindow().Show();
    }
}
