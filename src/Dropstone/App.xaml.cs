using Microsoft.UI.Xaml;

namespace Dropstone;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Only one installer may run at a time. Two instances would fight over
        // the install folder and can leave a half written payload behind.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\DropstoneInstaller", out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;

            var existing = new MainWindow();
            existing.Activate();
            _ = existing.ShowNoticeAsync(
                "Dropstone",
                "Dropstone is already running. Close the other window and try again.");
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
