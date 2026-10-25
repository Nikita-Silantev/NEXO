using Avalonia.Threading;
using Nexo.Client.Core;
using Nexo.Protocol.V1;
using ReactiveUI;

namespace Nexo.Client.Desktop.ViewModels;

/// <summary>Корень приложения: выбирает экран (вход, регистрация, разблокировка, основной интерфейс) и управляет блокировкой.</summary>
public sealed class ShellViewModel : VmBase
{
    private object? _content;
    private AppSession? _session;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private DateTime _lastActivity = DateTime.UtcNow;

    public AppSettings Settings { get; } = AppSettings.Load();
    public LocalVault Vault { get; } = new(AppSettings.Directory);
    public object? Content { get => _content; private set => this.RaiseAndSetIfChanged(ref _content, value); }
    public AppSession? Session => _session;

    public ShellViewModel()
    {
        _lockTimer.Tick += (_, _) => CheckAutoLock();
        _lockTimer.Start();
        if (Vault.Exists) Content = new UnlockViewModel(this); else ShowLogin();
    }

    // ---- навигация ----
    public void ShowLogin(string? notice = null) => Content = new LoginViewModel(this, notice);
    public void ShowRegister() { var vm = new RegisterViewModel(this); Content = vm; vm.LoadDocs.Execute().Subscribe(_ => { }, _ => { }); }
    public void ShowRecover() => Content = new RecoverViewModel(this);

    public bool TrySetServer(string url, out string error)
    {
        error = "";
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        { error = "Адрес сервера должен начинаться с https:// (или http:// для локальной проверки)."; return false; }
        if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback) { error = "Незащищённое соединение (http) допустимо только для localhost."; return false; }
        if (Settings.ServerUrl != url) { Settings.ServerUrl = url; Settings.Save(); }
        return true;
    }

    public NexoApi NewAnonymousApi() => new(new Uri(Settings.ServerUrl), _ => { });

    // ---- вход ----
    public async Task ContinueLoginAsync(LoginOutcome outcome, string username, string password)
    {
        switch (outcome)
        {
            case LoginSucceeded ok: await OpenNewAsync(ok.Session, password, ok.DocumentsToAccept.Count > 0); break;
            case LoginNeedsSecondFactor sf: Content = new SecondFactorViewModel(this, sf, username, password); break;
            case LoginNeedsApproval ap: Content = new ApprovalWaitViewModel(this, ap, password); break;
        }
    }

    public void ShowRecoveryKey(string keyText, SessionData session, string password) =>
        Content = new RecoveryKeyViewModel(this, keyText, () => OpenNewAsync(session, password, false));

    /// <summary>Создаёт хранилище ключа (секрет – пароль учётной записи), открывает локальную БД и сессию.</summary>
    private async Task OpenNewAsync(SessionData data, string password, bool needDocs)
    {
        Vault.Wipe();
        var ldk = await Vault.CreateAsync(password);
        var db = new LocalDb(Vault.DbPath, ldk);
        AppSession.SaveSession(db, data);
        await StartSessionAsync(db, data, ldk, needDocs);
    }

    public async Task UnlockAsync(string secret, bool usePin)
    {
        if (!Vault.Exists) { ShowLogin("Локальные данные удалены. Войдите заново."); return; }
        byte[] ldk;
        try { ldk = await Vault.UnlockAsync(secret, usePin); }
        catch (VaultWipedException e) { ShowLogin(e.Message + " Войдите заново."); return; }
        LocalDb? db = null;
        try
        {
            db = new LocalDb(Vault.DbPath, ldk);
            var data = AppSession.LoadSession(db);
            if (data is null) { db.Dispose(); WipeAndShowLogin("Локальные данные повреждены. Войдите снова."); return; }
            await StartSessionAsync(db, data, ldk, false);
        }
        catch { db?.Dispose(); throw; }
    }

    private async Task StartSessionAsync(LocalDb db, SessionData data, byte[] ldk, bool needDocs)
    {
        var s = await AppSession.OpenAsync(new Uri(Settings.ServerUrl), db, data, ldk);
        _session = s;
        s.Ended += reason => Ui(() => WipeAndShowLogin(reason));
        _lastActivity = DateTime.UtcNow;
        if (needDocs)
        {
            var docs = (await s.Api.CallAsync(h => s.Api.Legal.GetConsentStatusAsync(new Google.Protobuf.WellKnownTypes.Empty(), h).ResponseAsync)).Pending;
            if (docs.Count > 0) { Content = new AcceptDocsViewModel(s, docs, EnterMain); return; }
        }
        EnterMain();
    }

    private void EnterMain()
    {
        var s = _session!;
        s.StartSync();
        Content = new MainViewModel(this, s);
    }

    // ---- блокировка и выход ----
    public void Touch() => _lastActivity = DateTime.UtcNow;

    private void CheckAutoLock()
    {
        if (_session is null || Settings.AutoLockMinutes <= 0) return;
        if (DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(Settings.AutoLockMinutes)) Lock();
    }

    /// <summary>Выгружает ключ и закрывает БД; для продолжения нужен пароль или PIN (ФТ-ПРИ, НФТ-БЕЗ-05).</summary>
    public void Lock()
    {
        if (_session is null) return;
        var s = _session; _session = null;
        Content = new UnlockViewModel(this);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(s.Ldk);
        s.Dispose();
    }

    public async Task SignOutAsync()
    {
        var s = _session;
        if (s is not null) { await s.Messenger.LogoutAsync(); }
        WipeAndShowLogin("Вы вышли из учётной записи. Локальные данные удалены.");
    }

    public void WipeAndShowLogin(string notice)
    {
        var s = _session; _session = null;
        s?.Dispose();
        Vault.Wipe();
        ShowLogin(notice);
    }
}
