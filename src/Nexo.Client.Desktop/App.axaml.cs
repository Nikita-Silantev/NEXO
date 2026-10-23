using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Nexo.Client.Desktop.ViewModels;

namespace Nexo.Client.Desktop;

public partial class App : Application
{
    private static Window? _main;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel();
            _main = new MainWindow { DataContext = shell };
            desktop.MainWindow = _main;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static async Task CopyToClipboardAsync(string text)
    {
        var cb = _main is null ? null : TopLevel.GetTopLevel(_main)?.Clipboard;
        if (cb is not null) await cb.SetTextAsync(text);
    }
}
