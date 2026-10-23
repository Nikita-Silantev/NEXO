using System.Reactive;
using Nexo.Client.Core;
using Nexo.Core;
using Nexo.Protocol.V1;
using ReactiveUI;

namespace Nexo.Client.Desktop.ViewModels;

public sealed class LoginViewModel : FormVm
{
    private readonly ShellViewModel _shell;
    private string _username = "", _password = "", _server;
    public LoginViewModel(ShellViewModel shell, string? notice = null)
    {
        _shell = shell; _server = shell.Settings.ServerUrl; Error = notice ?? "";
        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        GoRegister = ReactiveCommand.Create(() => _shell.ShowRegister());
        GoRecover = ReactiveCommand.Create(() => _shell.ShowRecover());
    }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string Server { get => _server; set => this.RaiseAndSetIfChanged(ref _server, value); }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> GoRegister { get; }
    public ReactiveCommand<Unit, Unit> GoRecover { get; }

    private Task LoginAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)) { Error = "Введите ник и пароль."; return; }
        if (!_shell.TrySetServer(Server, out var err)) { Error = err; return; }
        using var api = _shell.NewAnonymousApi();
        var outcome = await new AuthFlow(api).LoginAsync(Username, Password, _shell.Settings.DeviceName);
        await _shell.ContinueLoginAsync(outcome, Username, Password);
    });
}

public sealed class RegisterViewModel : FormVm
{
    private readonly ShellViewModel _shell;
    private string _username = "", _display = "", _password = "", _repeat = "", _server;
    private bool _accepted;
    private IReadOnlyList<LegalDocument> _docs = [];

    public RegisterViewModel(ShellViewModel shell)
    {
        _shell = shell; _server = shell.Settings.ServerUrl;
        RegisterCommand = ReactiveCommand.CreateFromTask(RegisterAsync);
        LoadDocs = ReactiveCommand.CreateFromTask(LoadDocsAsync);
        Back = ReactiveCommand.Create(() => _shell.ShowLogin());
    }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string DisplayName { get => _display; set => this.RaiseAndSetIfChanged(ref _display, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public string Repeat { get => _repeat; set => this.RaiseAndSetIfChanged(ref _repeat, value); }
    public string Server { get => _server; set => this.RaiseAndSetIfChanged(ref _server, value); }
    public bool Accepted { get => _accepted; set => this.RaiseAndSetIfChanged(ref _accepted, value); }
    public IReadOnlyList<LegalDocument> Docs { get => _docs; private set => this.RaiseAndSetIfChanged(ref _docs, value); }
    public ReactiveCommand<Unit, Unit> RegisterCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadDocs { get; }
    public ReactiveCommand<Unit, Unit> Back { get; }

    private Task LoadDocsAsync() => RunAsync(async () =>
    {
        if (!_shell.TrySetServer(Server, out var err)) { Error = err; return; }
        using var api = _shell.NewAnonymousApi();
        Docs = (await new AuthFlow(api).GetDocumentsAsync()).ToList();
    });

    private Task RegisterAsync() => RunAsync(async () =>
    {
        var name = UsernameRules.Normalize(Username);
        if (!UsernameRules.IsValid(name)) { Error = "Ник: 3–32 символа a–z, 0–9, _; начинается с буквы."; return; }
        if (!UsernameRules.IsValidDisplayName(DisplayName)) { Error = "Введите отображаемое имя (до 64 символов)."; return; }
        if (PasswordPolicy.Check(Password, name) is { } bad) { Error = bad; return; }
        if (Password != Repeat) { Error = "Пароли не совпадают."; return; }
        if (!_shell.TrySetServer(Server, out var err)) { Error = err; return; }
        using var api = _shell.NewAnonymousApi();
        var flow = new AuthFlow(api);
        if (Docs.Count == 0) Docs = (await flow.GetDocumentsAsync()).ToList();
        if (!Accepted) { Error = "Необходимо принять пользовательское соглашение, политику конфиденциальности и согласие на обработку персональных данных."; return; }
        if (!await flow.IsUsernameAvailableAsync(name)) { Error = "Этот ник уже занят."; return; }
        var res = await flow.RegisterAsync(name, DisplayName, Password, _shell.Settings.DeviceName, Docs);
        _shell.ShowRecoveryKey(res.RecoveryKeyText, res.Session, Password);
    });
}

public sealed class RecoveryKeyViewModel : FormVm
{
    private bool _saved;
    public RecoveryKeyViewModel(ShellViewModel shell, string keyText, Func<Task> proceed)
    {
        KeyText = keyText;
        Copy = ReactiveCommand.CreateFromTask(async () => await App.CopyToClipboardAsync(keyText));
        // Ошибки открытия сессии показываются на экране, а не теряются.
        Continue = ReactiveCommand.CreateFromTask(() => RunAsync(proceed), this.WhenAnyValue(x => x.Saved));
    }
    public string KeyText { get; }
    public bool Saved { get => _saved; set => this.RaiseAndSetIfChanged(ref _saved, value); }
    public ReactiveCommand<Unit, Unit> Copy { get; }
    public ReactiveCommand<Unit, Unit> Continue { get; }
}

public sealed class SecondFactorViewModel : FormVm
{
    private readonly ShellViewModel _shell; private readonly LoginNeedsSecondFactor _p; private readonly string _user, _pass;
    private string _code = ""; private bool _backup;
    public SecondFactorViewModel(ShellViewModel shell, LoginNeedsSecondFactor p, string user, string pass)
    {
        _shell = shell; _p = p; _user = user; _pass = pass;
        Confirm = ReactiveCommand.CreateFromTask(ConfirmAsync);
        Back = ReactiveCommand.Create(() => _shell.ShowLogin());
    }
    public string Code { get => _code; set => this.RaiseAndSetIfChanged(ref _code, value); }
    public bool UseBackup { get => _backup; set => this.RaiseAndSetIfChanged(ref _backup, value); }
    public bool BackupAvailable => _p.BackupCode;
    public ReactiveCommand<Unit, Unit> Confirm { get; }
    public ReactiveCommand<Unit, Unit> Back { get; }
    private Task ConfirmAsync() => RunAsync(async () =>
    {
        using var api = _shell.NewAnonymousApi();
        var o = await new AuthFlow(api).CompleteSecondFactorAsync(_p, _user, Code, UseBackup);
        await _shell.ContinueLoginAsync(o, _user, _pass);
    });
}

/// <summary>Ожидание подтверждения входа на другом устройстве; код сверяется на обоих экранах (ФТ-УСТ-02).</summary>
public sealed class ApprovalWaitViewModel : FormVm
{
    private readonly CancellationTokenSource _cts = new();
    public ApprovalWaitViewModel(ShellViewModel shell, LoginNeedsApproval p, string pass)
    {
        Code = p.Code;
        Cancel = ReactiveCommand.Create(() => { _cts.Cancel(); shell.ShowLogin(); });
        _ = Task.Run(async () =>
        {
            try
            {
                using var api = shell.NewAnonymousApi();
                var o = await new AuthFlow(api).WaitForApprovalAsync(p, _cts.Token);
                if (o is null) { Ui(() => shell.ShowLogin("Вход не подтверждён: запрос отклонён или истёк срок.")); return; }
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => shell.ContinueLoginAsync(o, p.Username, pass));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Ui(() => shell.ShowLogin("Не удалось дождаться подтверждения: " + e.Message)); }
        });
    }
    public string Code { get; }
    public ReactiveCommand<Unit, Unit> Cancel { get; }
}

public sealed class RecoverViewModel : FormVm
{
    private readonly ShellViewModel _shell;
    private string _user = "", _key = "", _pass = "", _repeat = "", _code = "", _server;
    private bool _backup;
    public RecoverViewModel(ShellViewModel shell)
    {
        _shell = shell; _server = shell.Settings.ServerUrl;
        Recover = ReactiveCommand.CreateFromTask(RecoverAsync);
        Back = ReactiveCommand.Create(() => _shell.ShowLogin());
    }
    public string Username { get => _user; set => this.RaiseAndSetIfChanged(ref _user, value); }
    public string RecoveryKey { get => _key; set => this.RaiseAndSetIfChanged(ref _key, value); }
    public string NewPassword { get => _pass; set => this.RaiseAndSetIfChanged(ref _pass, value); }
    public string Repeat { get => _repeat; set => this.RaiseAndSetIfChanged(ref _repeat, value); }
    public string SecondFactor { get => _code; set => this.RaiseAndSetIfChanged(ref _code, value); }
    public bool UseBackup { get => _backup; set => this.RaiseAndSetIfChanged(ref _backup, value); }
    public string Server { get => _server; set => this.RaiseAndSetIfChanged(ref _server, value); }
    public ReactiveCommand<Unit, Unit> Recover { get; }
    public ReactiveCommand<Unit, Unit> Back { get; }

    private Task RecoverAsync() => RunAsync(async () =>
    {
        if (PasswordPolicy.Check(NewPassword, Username) is { } bad) { Error = bad; return; }
        if (NewPassword != Repeat) { Error = "Пароли не совпадают."; return; }
        if (!_shell.TrySetServer(Server, out var err)) { Error = err; return; }
        using var api = _shell.NewAnonymousApi();
        var (session, newKey, delayed, _) = await new AuthFlow(api).RecoverAsync(Username, RecoveryKey, NewPassword, _shell.Settings.DeviceName, SecondFactor, UseBackup);
        if (delayed is { } until) { Error = $"Включена двухфакторная защита, а кода нет: восстановление отложено до {until.LocalDateTime:g}."; return; }
        _shell.ShowRecoveryKey(newKey!, session!, NewPassword);
    });
}

public sealed class UnlockViewModel : FormVm
{
    private readonly ShellViewModel _shell; private string _secret = ""; private bool _usePin;
    public UnlockViewModel(ShellViewModel shell)
    {
        _shell = shell;
        HasPin = shell.Vault.HasPin; _usePin = HasPin;
        Unlock = ReactiveCommand.CreateFromTask(UnlockAsync);
        SwitchMode = ReactiveCommand.Create(() => { UsePin = !UsePin; Error = ""; });
        SignOut = ReactiveCommand.Create(() => _shell.WipeAndShowLogin("Вы вышли из учётной записи. Локальные данные удалены."));
    }
    public bool HasPin { get; }
    public bool UsePin { get => _usePin; set { this.RaiseAndSetIfChanged(ref _usePin, value); this.RaisePropertyChanged(nameof(Prompt)); this.RaisePropertyChanged(nameof(SwitchText)); } }
    public string Prompt => UsePin ? "Введите PIN-код" : "Введите пароль";
    public string SwitchText => UsePin ? "Войти по паролю" : "Войти по PIN-коду";
    public string Secret { get => _secret; set => this.RaiseAndSetIfChanged(ref _secret, value); }
    public ReactiveCommand<Unit, Unit> Unlock { get; }
    public ReactiveCommand<Unit, Unit> SwitchMode { get; }
    public ReactiveCommand<Unit, Unit> SignOut { get; }

    private async Task UnlockAsync()
    {
        try
        {
            await RunAsync(async () =>
            {
                if (string.IsNullOrEmpty(Secret)) return;
                await _shell.UnlockAsync(Secret, UsePin);
            });
        }
        finally { Secret = ""; }
    }
}

/// <summary>Принятие новой редакции документов при входе (ФТ-ПРА-02).</summary>
public sealed class AcceptDocsViewModel : FormVm
{
    private bool _ok;
    public AcceptDocsViewModel(AppSession session, IReadOnlyList<LegalDocument> docs, Action done)
    {
        Docs = docs;
        Accept = ReactiveCommand.CreateFromTask(() => RunAsync(async () =>
        {
            var req = new AcceptDocumentsRequest();
            foreach (var d in docs) req.Documents.Add(new AcceptedDocument { Kind = d.Kind, Version = d.Version });
            await session.Api.CallAsync(h => session.Api.Legal.AcceptDocumentsAsync(req, h).ResponseAsync);
            done();
        }), this.WhenAnyValue(x => x.Accepted));
    }
    public IReadOnlyList<LegalDocument> Docs { get; }
    public bool Accepted { get => _ok; set => this.RaiseAndSetIfChanged(ref _ok, value); }
    public ReactiveCommand<Unit, Unit> Accept { get; }
}
