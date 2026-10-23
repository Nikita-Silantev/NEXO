using System.Collections.ObjectModel;
using System.Reactive;
using Google.Protobuf.WellKnownTypes;
using Nexo.Client.Core;
using Nexo.Protocol.V1;
using ReactiveUI;

namespace Nexo.Client.Desktop.ViewModels;

public sealed class ChatItemViewModel(Chat chat, string myId) : VmBase
{
    public MainViewModel? Owner { get; init; }
    public string MuteText => Muted ? "Включить уведомления" : "Отключить уведомления";
    public string PinText => Pinned ? "Открепить" : "Закрепить";
    public string ArchiveText => Chat.Me?.Archived == true ? "Вернуть из архива" : "В архив";
    public Chat Chat { get; } = chat;
    public string ChatId => Chat.ChatId;
    public string Title => Chat.Kind == ChatKind.Direct && Chat.DirectPeer is { } p
        ? (string.IsNullOrWhiteSpace(p.DisplayName) ? "@" + p.Username : p.DisplayName)
        : Chat.Title;
    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string Preview
    {
        get
        {
            var m = Chat.LastMessage;
            if (m is null) return "Нет сообщений";
            if (m.Deleted) return "Сообщение удалено";
            var text = m.Content?.ContentCase == MessageContent.ContentOneofCase.Text ? m.Content.Text.Text.Replace('\n', ' ') : "Сообщение";
            return (m.SenderId == myId ? "Вы: " : "") + text;
        }
    }
    public string Time
    {
        get
        {
            var m = Chat.LastMessage; if (m is null) return "";
            var t = m.CreatedAt.ToDateTime().ToLocalTime();
            return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.Year == DateTime.Today.Year ? t.ToString("d MMM") : t.ToString("d.MM.yy");
        }
    }
    public int Unread => (int)(Chat.Me?.UnreadCount ?? 0);
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
    public bool Muted => Chat.Me?.MutedUntil is { } t && t.ToDateTime() > DateTime.UtcNow;
    public bool Pinned => (Chat.Me?.PinnedPosition ?? 0) > 0;
}

public sealed class MessageViewModel(Message? msg, LocalDb.OutboxItem? pending, string myId, long peerRead, string? replyText) : VmBase
{
    public MainViewModel? Owner { get; init; }
    public Message? Source { get; } = msg;
    public LocalDb.OutboxItem? Pending { get; } = pending;
    public string Id => Source?.MessageId ?? Pending!.ClientMsgId;
    public bool IsMine => Pending is not null || Source!.SenderId == myId;
    public bool Deleted => Source?.Deleted ?? false;
    public string Text => Pending?.Body ?? (Deleted ? "Сообщение удалено" : Source!.Content?.ContentCase == MessageContent.ContentOneofCase.Text ? Source.Content.Text.Text : "Сообщение этого типа недоступно в текущей версии");
    public string Time => (Pending?.CreatedAt ?? Source!.CreatedAt.ToDateTime()).ToLocalTime().ToString("HH:mm");
    public bool Edited => Source?.EditedAt is not null && !Deleted;
    public bool Failed => Pending?.State == 2;
    public string? Error => Pending?.Error;
    public bool IsForwarded => Source?.ForwardOrigin is not null;
    public string? ReplyText { get; } = replyText;
    public bool HasReply => !string.IsNullOrEmpty(ReplyText);
    public long Seq => Source?.Seq ?? long.MaxValue;
    /// <summary>⌛ ожидает отправки, ! ошибка, ✓ доставлено на сервер, ✓✓ прочитано.</summary>
    public string Status => !IsMine ? "" : Pending is not null ? (Failed ? "!" : "⌛") : peerRead >= Source!.Seq ? "✓✓" : "✓";
    public bool CanEdit => Source is not null && !Deleted && IsMine && DateTime.UtcNow - Source.CreatedAt.ToDateTime() < TimeSpan.FromHours(48);
    public bool CanDeleteForEveryone => Source is not null && !Deleted && IsMine;
}

public sealed class MainViewModel : FormVm
{
    private readonly ShellViewModel _shell;
    private readonly AppSession _s;
    private ChatItemViewModel? _selected;
    private string _draft = "", _search = "", _connection = "Подключение…", _typing = "", _editingId = "", _replyToId = "", _replyPreview = "";
    private bool _showSettings, _showArchive;
    private MessageViewModel? _contextMessage;
    private int _openGen;

    public ObservableCollection<ChatItemViewModel> Chats { get; } = [];
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<UserProfile> FoundUsers { get; } = [];
    public ObservableCollection<LoginApproval> Approvals { get; } = [];
    public SettingsViewModel SettingsVm { get; }

    public MainViewModel(ShellViewModel shell, AppSession s)
    {
        _shell = shell; _s = s;
        SettingsVm = new SettingsViewModel(shell, s);
        Send = ReactiveCommand.Create(DoSend);
        OpenUser = ReactiveCommand.CreateFromTask<UserProfile>(OpenUserAsync);
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);
        ToggleSettings = ReactiveCommand.Create(() => { ShowSettings = !ShowSettings; if (ShowSettings) SettingsVm.Refresh.Execute().Subscribe(_ => { }, _ => { }); });
        LockNow = ReactiveCommand.Create(shell.Lock);
        CloseChat = ReactiveCommand.Create(() => { Selected = null; });
        ReplyTo = ReactiveCommand.Create<MessageViewModel>(m => { _editingId = ""; _replyToId = m.Id; ReplyPreview = "Ответ: " + Trim(m.Text); });
        EditStart = ReactiveCommand.Create<MessageViewModel>(m => { _replyToId = ""; ReplyPreview = "Редактирование"; _editingId = m.Id; Draft = m.Text; });
        CancelCompose = ReactiveCommand.Create(() => { if (_editingId != "") Draft = ""; _editingId = ""; _replyToId = ""; ReplyPreview = ""; });
        DeleteForMe = ReactiveCommand.CreateFromTask<MessageViewModel>(m => DeleteAsync(m, false));
        DeleteForAll = ReactiveCommand.CreateFromTask<MessageViewModel>(m => DeleteAsync(m, true));
        CopyText = ReactiveCommand.CreateFromTask<MessageViewModel>(m => App.CopyToClipboardAsync(m.Text));
        RetryMessage = ReactiveCommand.Create<MessageViewModel>(m => _s.Messenger.RetryFailed(m.Id));
        DiscardMessage = ReactiveCommand.Create<MessageViewModel>(m => { _s.Messenger.DiscardFailed(m.Id); ReloadMessages(); });
        ApproveLogin = ReactiveCommand.CreateFromTask<LoginApproval>(a => ResolveAsync(a, true));
        DenyLogin = ReactiveCommand.CreateFromTask<LoginApproval>(a => ResolveAsync(a, false));
        LoadOlder = ReactiveCommand.CreateFromTask(LoadOlderAsync);
        ToggleMute = ReactiveCommand.CreateFromTask<ChatItemViewModel>(c => RunAsync(() => _s.Messenger.MuteAsync(c.ChatId, !c.Muted)));
        TogglePin = ReactiveCommand.CreateFromTask<ChatItemViewModel>(c => RunAsync(() => _s.Messenger.PinAsync(c.ChatId, !c.Pinned)));
        ToggleArchiveChat = ReactiveCommand.CreateFromTask<ChatItemViewModel>(c => RunAsync(async () =>
        { await _s.Messenger.ArchiveAsync(c.ChatId, c.Chat.Me?.Archived != true); if (ReferenceEquals(Selected, c)) Selected = null; }));
        ToggleArchive = ReactiveCommand.Create(() => { ShowArchive = !ShowArchive; ReloadChats(); });

        s.Sync.StateChanged += st => Ui(() => Connection = st switch { ConnectionState.Online => "", ConnectionState.Connecting => "Подключение…", _ => "Нет связи. Сообщения будут отправлены позже." });
        s.Sync.ChatsChanged += () => Ui(ReloadChats);
        s.Sync.MessagesChanged += id => Ui(() => { if (Selected?.ChatId == id) ReloadMessages(); });
        s.Sync.Typing += (chat, user) => Ui(() => OnTyping(chat, user));
        s.Sync.LoginApprovalRequested += a => Ui(() => { if (Approvals.All(x => x.ApprovalId != a.ApprovalId)) Approvals.Add(a); });
        Approvals.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasApprovals));
        FoundUsers.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasFoundUsers));
        ReloadChats();
        _ = LoadApprovalsAsync();
    }

    // ---- свойства ----
    public ChatItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection)); this.RaisePropertyChanged(nameof(NoSelection));
            this.RaisePropertyChanged(nameof(ChatTitle));
            _editingId = ""; _replyToId = ""; ReplyPreview = ""; Draft = ""; Typing = "";
            ReloadMessages();
            _ = OnOpenedAsync();
        }
    }
    public bool HasSelection => _selected is not null;
    public bool NoSelection => _selected is null;
    public string ChatTitle => _selected?.Title ?? "";
    public string Draft { get => _draft; set { this.RaiseAndSetIfChanged(ref _draft, value); if (value.Length > 0) _ = TypingPingAsync(); } }
    public string SearchText { get => _search; set { this.RaiseAndSetIfChanged(ref _search, value); ApplySearchFilter(); } }
    public string Connection { get => _connection; set { this.RaiseAndSetIfChanged(ref _connection, value); this.RaisePropertyChanged(nameof(HasConnectionNote)); } }
    public bool HasConnectionNote => Connection != "";
    public string Typing { get => _typing; set { this.RaiseAndSetIfChanged(ref _typing, value); this.RaisePropertyChanged(nameof(HasTyping)); } }
    public bool HasTyping => _typing != "";
    public string ReplyPreview { get => _replyPreview; set { this.RaiseAndSetIfChanged(ref _replyPreview, value); this.RaisePropertyChanged(nameof(HasCompose)); } }
    public bool HasCompose => _replyPreview != "";
    public bool ShowSettings { get => _showSettings; set => this.RaiseAndSetIfChanged(ref _showSettings, value); }
    public bool ShowArchive { get => _showArchive; set { this.RaiseAndSetIfChanged(ref _showArchive, value); this.RaisePropertyChanged(nameof(ArchiveButtonText)); } }
    public string ArchiveButtonText => ShowArchive ? "Назад к чатам" : "Архив";
    public bool HasApprovals => Approvals.Count > 0;
    public bool HasFoundUsers => FoundUsers.Count > 0;
    public string MyName => _s.Data.Username;

    public ReactiveCommand<Unit, Unit> Send { get; }
    public ReactiveCommand<UserProfile, Unit> OpenUser { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSettings { get; }
    public ReactiveCommand<Unit, Unit> LockNow { get; }
    public ReactiveCommand<Unit, Unit> CloseChat { get; }
    public ReactiveCommand<MessageViewModel, Unit> ReplyTo { get; }
    public ReactiveCommand<MessageViewModel, Unit> EditStart { get; }
    public ReactiveCommand<Unit, Unit> CancelCompose { get; }
    public ReactiveCommand<MessageViewModel, Unit> DeleteForMe { get; }
    public ReactiveCommand<MessageViewModel, Unit> DeleteForAll { get; }
    public ReactiveCommand<MessageViewModel, Unit> CopyText { get; }
    public ReactiveCommand<MessageViewModel, Unit> RetryMessage { get; }
    public ReactiveCommand<MessageViewModel, Unit> DiscardMessage { get; }
    public ReactiveCommand<LoginApproval, Unit> ApproveLogin { get; }
    public ReactiveCommand<LoginApproval, Unit> DenyLogin { get; }
    public ReactiveCommand<Unit, Unit> ToggleArchive { get; }
    public ReactiveCommand<Unit, Unit> LoadOlder { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ToggleMute { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> TogglePin { get; }
    public ReactiveCommand<ChatItemViewModel, Unit> ToggleArchiveChat { get; }

    // ---- список чатов ----
    private string _chatFilter = "";
    private void ApplySearchFilter()
    {
        _chatFilter = SearchText.Trim();
        ReloadChats();
        FoundUsers.Clear();
        if (_chatFilter.Length >= 3) SearchCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void ReloadChats()
    {
        var selectedId = _selected?.ChatId;
        var items = _s.Db.ListChats(ShowArchive).Select(c => new ChatItemViewModel(c, _s.Data.UserId) { Owner = this })
            .Where(c => _chatFilter.Length == 0 || c.Title.Contains(_chatFilter.TrimStart('@'), StringComparison.OrdinalIgnoreCase)).ToList();
        Chats.Clear();
        foreach (var i in items) Chats.Add(i);
        if (selectedId is not null)
        {
            var again = Chats.FirstOrDefault(c => c.ChatId == selectedId);
            if (again is not null && !ReferenceEquals(again, _selected)) { _selected = again; this.RaisePropertyChanged(nameof(Selected)); }
        }
    }

    private async Task SearchAsync()
    {
        try
        {
            var q = _chatFilter;
            var users = await _s.Messenger.SearchUsersAsync(q);
            if (q != _chatFilter) return;
            Ui(() => { FoundUsers.Clear(); foreach (var u in users.Where(u => !u.IsSelf)) FoundUsers.Add(u); });
        }
        catch (Grpc.Core.RpcException) { /* поиск вторичен: ошибки не показываем */ }
    }

    private async Task OpenUserAsync(UserProfile u)
    {
        await RunAsync(async () =>
        {
            var chat = await _s.Messenger.OpenDirectAsync(u.Username);
            SearchText = "";
            ReloadChats();
            Selected = Chats.FirstOrDefault(c => c.ChatId == chat.ChatId);
        });
    }

    // ---- переписка ----
    private void ReloadMessages()
    {
        var chat = _selected;
        Messages.Clear();
        if (chat is null) return;
        var peerRead = _s.Db.GetPeerRead(chat.ChatId);
        var stored = _s.Db.GetMessages(chat.ChatId, 0, 200);
        var byId = stored.ToDictionary(m => m.MessageId);
        string? ReplyOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var m = byId.TryGetValue(id, out var x) ? x : _s.Db.GetMessageById(id);
            return m is null ? "Сообщение недоступно" : m.Deleted ? "Сообщение удалено" : Trim(m.Content?.Text?.Text ?? "");
        }
        foreach (var m in stored) Messages.Add(new MessageViewModel(m, null, _s.Data.UserId, peerRead, ReplyOf(m.ReplyToId)) { Owner = this });
        foreach (var o in _s.Db.Outbox(chat.ChatId)) Messages.Add(new MessageViewModel(null, o, _s.Data.UserId, peerRead, ReplyOf(o.ReplyToId)) { Owner = this });
        MessagesReloaded?.Invoke();
    }

    public event Action? MessagesReloaded;

    private async Task OnOpenedAsync()
    {
        var chat = _selected; if (chat is null) return;
        var gen = ++_openGen;
        try
        {
            if (_s.Db.GetMessages(chat.ChatId, 0, 30).Count < 30 && chat.Chat.TopSeq > 0) { await _s.Messenger.LoadOlderAsync(chat.ChatId, 50); if (gen == _openGen) Ui(ReloadMessages); }
            await _s.Messenger.MarkReadAsync(chat.ChatId);
            Ui(ReloadChats);
        }
        catch (Grpc.Core.RpcException) { /* нет связи: покажем то, что есть локально */ }
    }

    public async Task LoadOlderAsync()
    {
        var chat = _selected; if (chat is null) return;
        try { await _s.Messenger.LoadOlderAsync(chat.ChatId, 50); Ui(ReloadMessages); } catch (Grpc.Core.RpcException) { }
    }

    private void DoSend()
    {
        var chat = _selected; var text = Draft.Trim();
        if (chat is null || text.Length == 0) return;
        if (text.Length > 4096) { Error = "Сообщение длиннее 4096 символов."; return; }
        Error = "";
        if (_editingId != "")
        {
            var id = _editingId; _editingId = ""; ReplyPreview = ""; Draft = "";
            _ = RunAsync(async () => { await _s.Messenger.EditAsync(chat.ChatId, id, text); ReloadMessages(); });
            return;
        }
        _s.Messenger.Send(chat.ChatId, text, _replyToId);
        _replyToId = ""; ReplyPreview = ""; Draft = "";
        ReloadMessages();
        _ = MarkReadSoonAsync(chat.ChatId);
    }

    private async Task MarkReadSoonAsync(string chatId)
    {
        try { await _s.Messenger.MarkReadAsync(chatId); } catch { }
    }

    private async Task DeleteAsync(MessageViewModel m, bool everyone)
    {
        if (m.Source is null || _selected is null) return;
        await RunAsync(async () => { await _s.Messenger.DeleteAsync(_selected.ChatId, [m.Id], everyone); ReloadMessages(); });
    }

    private DateTime _lastTypingSent = DateTime.MinValue;
    private async Task TypingPingAsync()
    {
        var chat = _selected;
        if (chat is null || DateTime.UtcNow - _lastTypingSent < TimeSpan.FromSeconds(4)) return;
        _lastTypingSent = DateTime.UtcNow;
        await _s.Messenger.SendTypingAsync(chat.ChatId);
    }

    private CancellationTokenSource? _typingCts;
    private void OnTyping(string chatId, string userId)
    {
        if (_selected?.ChatId != chatId || userId == _s.Data.UserId) return;
        Typing = "печатает…";
        _typingCts?.Cancel(); _typingCts = new();
        var ct = _typingCts.Token;
        _ = Task.Delay(6000, ct).ContinueWith(t => { if (!t.IsCanceled) Ui(() => Typing = ""); });
    }

    // ---- подтверждение входа с нового устройства ----
    private async Task LoadApprovalsAsync()
    {
        try
        {
            var r = await _s.Api.CallAsync(h => _s.Api.Devices.ListLoginApprovalsAsync(new Empty(), h).ResponseAsync);
            Ui(() => { foreach (var a in r.Approvals.Where(a => a.Status == ApprovalStatus.Pending)) if (Approvals.All(x => x.ApprovalId != a.ApprovalId)) Approvals.Add(a); this.RaisePropertyChanged(nameof(HasApprovals)); });
        }
        catch { }
    }

    private async Task ResolveAsync(LoginApproval a, bool approve)
    {
        await RunAsync(async () =>
        {
            var req = new ResolveLoginApprovalRequest { ApprovalId = a.ApprovalId, Approve = approve };
            if (approve)
            {
                using var key = DeviceKey.Import(_s.Data.DevicePrivateKey);
                req.Signature = Google.Protobuf.ByteString.CopyFrom(key.Sign(Nexo.Core.DeviceSignatures.ApprovalMessage(a.ApprovalId, a.RequesterPubkey.Span, a.Code)));
            }
            await _s.Api.CallAsync(h => _s.Api.Devices.ResolveLoginApprovalAsync(req, h).ResponseAsync);
            Ui(() => Approvals.Remove(a));
        });
    }

    private static string Trim(string s) { s = s.Replace('\n', ' '); return s.Length > 60 ? s[..60] + "…" : s; }

    // ---- переключение чатов с клавиатуры ----
    public void SelectNext(int delta)
    {
        if (Chats.Count == 0) return;
        var i = _selected is null ? -1 : Chats.IndexOf(_selected);
        Selected = Chats[Math.Clamp(i + delta, 0, Chats.Count - 1)];
    }
}
