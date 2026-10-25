using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Nexo.Client.Desktop.ViewModels;

namespace Nexo.Client.Desktop.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.MessagesReloaded -= ScrollToEnd;
            _vm = DataContext as MainViewModel;
            if (_vm is not null) _vm.MessagesReloaded += ScrollToEnd;
        };
        Composer.AddHandler(KeyDownEvent, OnComposerKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Горячие клавиши вешаем на окно: пока ни один элемент не в фокусе, события клавиш до MainView не доходят.
        AttachedToVisualTree += (_, _) =>
        {
            _top = TopLevel.GetTopLevel(this);
            _top?.AddHandler(KeyDownEvent, OnShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) => { _top?.RemoveHandler(KeyDownEvent, OnShortcut); _top = null; };
    }

    private TopLevel? _top;

    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (MessageList.ItemCount > 0) MessageList.ScrollIntoView(MessageList.ItemCount - 1);
        }, DispatcherPriority.Background);
    }

    /// <summary>Enter отправляет сообщение, Shift+Enter вставляет перевод строки.</summary>
    private void OnComposerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm is not null)
        {
            e.Handled = true;
            _vm.Send.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    /// <summary>Горячие клавиши (НФТ-ЭРГ-02): Ctrl/⌘+K поиск, +L блокировка, +, настройки, Ctrl/⌘+↑/↓ смена чата, Esc закрыть.</summary>
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (cmd && e.Key == Key.K) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (cmd && e.Key == Key.L) { _vm.LockNow.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
        else if (cmd && e.Key == Key.OemComma) { _vm.ToggleSettings.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
        else if (cmd && e.Key == Key.Up) { _vm.SelectNext(-1); e.Handled = true; }
        else if (cmd && e.Key == Key.Down) { _vm.SelectNext(1); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_vm.ShowSettings) _vm.ShowSettings = false;
            else if (_vm.HasCompose) _vm.CancelCompose.Execute().Subscribe(_ => { }, _ => { });
            else _vm.CloseChat.Execute().Subscribe(_ => { }, _ => { });
            e.Handled = true;
        }
    }
}
