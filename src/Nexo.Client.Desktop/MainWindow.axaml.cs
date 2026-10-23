using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Nexo.Client.Desktop.ViewModels;

namespace Nexo.Client.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Любая активность пользователя откладывает автоблокировку (НФТ-БЕЗ-05).
        AddHandler(PointerMovedEvent, (_, _) => Shell?.Touch(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => Shell?.Touch(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => Shell?.Touch(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && Shell?.Settings.LockOnMinimize == true) Shell.Lock();
        };
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;
}
