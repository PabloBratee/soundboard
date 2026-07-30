using System.Windows;
using Soundboard.App.Lifetime;

namespace Soundboard.App;

public partial class App : Application
{
    private SingleInstanceGuard? singleInstanceGuard;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        if (!SingleInstanceGuard.TryAcquireForApplication(
                out singleInstanceGuard))
        {
            MessageBox.Show(
                "Soundboard is already running in this Windows session.",
                "Soundboard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(eventArgs);
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        singleInstanceGuard?.Dispose();
        singleInstanceGuard = null;
        base.OnExit(eventArgs);
    }
}
