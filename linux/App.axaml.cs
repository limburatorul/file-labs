using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FileExplorer;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings.Load();
            SizeCache.StartLoading();
            desktop.MainWindow = new MainWindow(desktop.Args ?? Array.Empty<string>());
            desktop.ShutdownRequested += (_, _) => { Settings.Save(); SizeCache.Save(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
