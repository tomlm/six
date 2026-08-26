using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace GixelViewer;

public partial class App : Application
{
    public static string[] Args { get; set; } = [];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (Args.Length > 0)
                window.Initialize(Args[0]);
            else
                window.PickFolderOnLoad();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
