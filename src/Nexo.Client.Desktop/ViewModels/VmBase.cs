using Avalonia.Threading;
using ReactiveUI;

namespace Nexo.Client.Desktop.ViewModels;

public abstract class VmBase : ReactiveObject
{
    protected static void Ui(Action a) { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); }
}

/// <summary>Общая часть экранов с вводом: сообщение об ошибке и признак занятости.</summary>
public abstract class FormVm : VmBase
{
    private string _error = ""; private bool _busy;
    public string Error { get => _error; set => this.RaiseAndSetIfChanged(ref _error, value); }
    public bool Busy { get => _busy; set { this.RaiseAndSetIfChanged(ref _busy, value); this.RaisePropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;

    /// <summary>Выполняет действие с индикатором занятости; ошибки API превращает в понятный текст.</summary>
    protected async Task RunAsync(Func<Task> action)
    {
        Error = ""; Busy = true;
        try { await action(); }
        catch (Grpc.Core.RpcException e) { Error = Nexo.Client.Core.ApiError.Message(e); }
        catch (Nexo.Client.Core.VaultWipedException e) { Error = e.Message; }
        catch (Nexo.Client.Core.VaultLockedOutException e) { Error = e.Message; }
        catch (FormatException e) { Error = e.Message; }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = "Не удалось выполнить действие: " + e.Message; }
        finally { Busy = false; }
    }
}
