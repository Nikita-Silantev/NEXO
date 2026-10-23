using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Nexo.Client.Desktop.Views;

public partial class AcceptDocsView : UserControl
{
    public AcceptDocsView() => InitializeComponent();

    private void OnOpenDoc(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }
}
