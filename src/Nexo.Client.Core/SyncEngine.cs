using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

public enum ConnectionState { Offline, Connecting, Online }

/// <summary>
/// Держит поток Subscribe, применяет обновления к локальной БД и отправляет очередь исходящих (ФТ-СИН-01..05).
/// Обрыв потока не теряет событий: после переподключения клиент указывает последний применённый update_seq.
/// </summary>
public sealed class SyncEngine(NexoApi api, LocalDb db, string myUserId) : IDisposable
{
    private const string LastSeqKey = "last_update_seq";
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _outboxSignal = new(0, int.MaxValue);
    private Task? _sync, _out;

    public ConnectionState State { get; private set; } = ConnectionState.Offline;
    /// <summary>Последняя причина обрыва связи (для показа пользователю).</summary>
    public string? LastError { get; private set; }
    public event Action<ConnectionState>? StateChanged;
    public event Action? ChatsChanged;
    public event Action<string>? MessagesChanged;
    public event Action<string, string>? Typing;            // chatId, userId
    public event Action<string>? ResyncStarted;
    public event Action<LoginApproval>? LoginApprovalRequested;

    public void Start()
    {
        _sync = Task.Run(() => SyncLoopAsync(_cts.Token));
        _out = Task.Run(() => OutboxLoopAsync(_cts.Token));
    }

    public void KickOutbox() { if (_outboxSignal.CurrentCount == 0) _outboxSignal.Release(); }

    private void SetState(ConnectionState s) { if (State == s) return; State = s; StateChanged?.Invoke(s); }

    // ---------------- Подписка ----------------

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(ConnectionState.Connecting);
                if (db.GetLong(LastSeqKey, -1) < 0) await FullSnapshotAsync(ct);
                var last = db.GetLong(LastSeqKey);
                var headers = await api.HeadersAsync(ct);
                using var call = api.Sync.Subscribe(new SubscribeRequest { LastUpdateSeq = last, ClientVersion = "0.1.0" }, headers, cancellationToken: ct);
                while (await call.ResponseStream.MoveNext(ct))
                {
                    LastError = null; SetState(ConnectionState.Online); delay = TimeSpan.FromSeconds(1);
                    var ev = call.ResponseStream.Current;
                    switch (ev.EventCase)
                    {
                        case SubscribeEvent.EventOneofCase.Update: await ApplyAsync(ev.Update, ct); break;
                        case SubscribeEvent.EventOneofCase.ResyncRequired:
                            ResyncStarted?.Invoke(ev.ResyncRequired.Reason);
                            db.ClearSyncData(); await FullSnapshotAsync(ct); throw new RpcException(new Status(StatusCode.Aborted, "resync"));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RpcException e) when (e.StatusCode == StatusCode.Aborted) { /* перезапуск потока после повторной синхронизации */ }
            catch (RpcException e) when (ApiError.Reason(e) is ErrorReason.TokenRevoked or ErrorReason.RefreshReuseDetected) { SetState(ConnectionState.Offline); break; }
            catch (Exception e) { LastError = e is RpcException re ? ApiError.Message(re) : e.Message; /* повторим */ }
            SetState(ConnectionState.Offline);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
        SetState(ConnectionState.Offline);
    }

    /// <summary>Загрузка списка чатов; с позиции snapshot_update_seq продолжается подписка.</summary>
    private async Task FullSnapshotAsync(CancellationToken ct)
    {
        string cursor = ""; long snapshot = 0;
        do
        {
            var r = await api.CallAsync(h => api.Chats.ListChatsAsync(new ListChatsRequest
            { Page = new PageRequest { Limit = 100, Cursor = cursor }, IncludeArchived = true }, h, cancellationToken: ct).ResponseAsync, ct);
            foreach (var c in r.Chats)
            {
                db.UpsertChat(c);
                if (c.LastMessage is { } m && m.Seq > 0) db.UpsertMessage(m);
            }
            snapshot = r.SnapshotUpdateSeq;
            cursor = r.Page?.HasMore == true ? r.Page.NextCursor : "";
        } while (cursor != "");
        db.SetLong(LastSeqKey, snapshot);
        ChatsChanged?.Invoke();
    }

    // ---------------- Применение обновлений ----------------

    private async Task ApplyAsync(Update u, CancellationToken ct)
    {
        switch (u.PayloadCase)
        {
            case Update.PayloadOneofCase.NewMessage: await OnNewMessageAsync(u.NewMessage.Message, ct); break;
            case Update.PayloadOneofCase.MessageEdited: StoreMessage(u.MessageEdited.Message, refreshLast: true); break;
            case Update.PayloadOneofCase.MessageDeleted: OnDeleted(u.MessageDeleted); break;
            case Update.PayloadOneofCase.ReadState: OnReadState(u.ReadState); break;
            case Update.PayloadOneofCase.Chat:
                if (u.Chat.Removed) db.RemoveChat(u.Chat.Chat.ChatId); else db.UpsertChat(u.Chat.Chat);
                ChatsChanged?.Invoke(); break;
            case Update.PayloadOneofCase.LoginApproval: LoginApprovalRequested?.Invoke(u.LoginApproval.Approval); break;
            case Update.PayloadOneofCase.Typing: Typing?.Invoke(u.Typing.ChatId, u.Typing.UserId); break;
            // Остальные виды (папки, черновики, настройки, медиа, реакции...) относятся к этапу Э2 и позже.
        }
        if (u.UpdateSeq > 0) db.SetLong(LastSeqKey, u.UpdateSeq);
    }

    private void StoreMessage(Message m, bool refreshLast)
    {
        db.UpsertMessage(m);
        if (refreshLast && db.GetChat(m.ChatId) is { } chat && chat.LastMessage?.MessageId == m.MessageId)
        {
            chat.LastMessage = m; db.UpsertChat(chat); ChatsChanged?.Invoke();
        }
        MessagesChanged?.Invoke(m.ChatId);
    }

    private async Task OnNewMessageAsync(Message m, CancellationToken ct)
    {
        var chat = db.GetChat(m.ChatId);
        if (chat is null)
        {
            chat = await api.CallAsync(h => api.Chats.GetChatAsync(new GetChatRequest { ChatId = m.ChatId }, h, cancellationToken: ct).ResponseAsync, ct);
        }
        else
        {
            chat.TopSeq = Math.Max(chat.TopSeq, m.Seq);
            chat.LastMessage = m;
            if (m.SenderId != myUserId && chat.Me is not null && m.Seq > chat.Me.LastReadSeq) chat.Me.UnreadCount++;
            if (m.SenderId == myUserId && chat.Me is not null) { /* своё сообщение с другого устройства */ }
        }
        db.UpsertChat(chat);
        db.UpsertMessage(m);
        // Свою копию из очереди заменяет сообщение с сервера (то же client_msg_id).
        if (m.SenderId == myUserId && !string.IsNullOrEmpty(m.ClientMsgId)) db.RemoveOutbox(m.ClientMsgId);
        ChatsChanged?.Invoke();
        MessagesChanged?.Invoke(m.ChatId);
    }

    private void OnDeleted(MessageDeletedUpdate d)
    {
        for (var i = 0; i < d.MessageIds.Count; i++)
        {
            var m = db.GetMessageById(d.MessageIds[i]);
            if (m is null) continue;
            // «Удалить у себя» приходит и как удаление для всех; различить нечем – оставляем надгробие только при deleted у всех.
            m.Deleted = true; m.Content = null; db.UpsertMessage(m);
        }
        if (db.GetChat(d.ChatId) is { } chat && chat.LastMessage is { } lm && d.MessageIds.Contains(lm.MessageId))
        {
            lm.Deleted = true; lm.Content = null; chat.LastMessage = lm; db.UpsertChat(chat); ChatsChanged?.Invoke();
        }
        MessagesChanged?.Invoke(d.ChatId);
    }

    private void OnReadState(ReadStateUpdate r)
    {
        if (r.IsSelf)
        {
            if (db.GetChat(r.ChatId) is { } chat && chat.Me is not null)
            {
                var advanced = Math.Max(0, r.UpToSeq - chat.Me.LastReadSeq);
                chat.Me.LastReadSeq = Math.Max(chat.Me.LastReadSeq, r.UpToSeq);
                chat.Me.UnreadCount = r.UpToSeq >= chat.TopSeq ? 0 : Math.Max(0, chat.Me.UnreadCount - advanced);
                db.UpsertChat(chat); ChatsChanged?.Invoke();
            }
        }
        else { db.SetPeerRead(r.ChatId, r.UpToSeq); MessagesChanged?.Invoke(r.ChatId); }
    }

    // ---------------- Исходящие ----------------

    private async Task OutboxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _outboxSignal.WaitAsync(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
            try { await FlushOutboxAsync(ct); } catch (OperationCanceledException) { break; } catch { /* повтор по таймеру */ }
        }
    }

    private async Task FlushOutboxAsync(CancellationToken ct)
    {
        var blockedChats = new HashSet<string>();
        foreach (var item in db.Outbox())
        {
            if (item.State == 2 || blockedChats.Contains(item.ChatId)) continue;   // 2 – отклонено сервером; порядок в чате сохраняется
            try
            {
                var res = await api.CallAsync(h => api.Messages.SendMessageAsync(new SendMessageRequest
                {
                    ChatId = item.ChatId, ClientMsgId = item.ClientMsgId, ReplyToId = item.ReplyToId,
                    Content = new MessageContent { Text = new TextContent { Text = item.Body } },
                }, h, cancellationToken: ct).ResponseAsync, ct);
                db.UpsertMessage(res.Message);
                db.RemoveOutbox(item.ClientMsgId);
                if (db.GetChat(item.ChatId) is { } chat)
                {
                    chat.TopSeq = Math.Max(chat.TopSeq, res.Message.Seq);
                    if (chat.LastMessage is null || chat.LastMessage.Seq <= res.Message.Seq) chat.LastMessage = res.Message;
                    db.UpsertChat(chat);
                }
                ChatsChanged?.Invoke(); MessagesChanged?.Invoke(item.ChatId);
            }
            catch (RpcException e) when (ApiError.IsNetwork(e)) { blockedChats.Add(item.ChatId); break; }
            catch (RpcException e) when (ApiError.Reason(e) is ErrorReason.TokenRevoked or ErrorReason.RefreshReuseDetected) { return; }
            catch (RpcException e)
            {
                db.SetOutboxState(item.ClientMsgId, 2, ApiError.Message(e));     // постоянная ошибка: не повторять, показать пользователю
                MessagesChanged?.Invoke(item.ChatId);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([_sync ?? Task.CompletedTask, _out ?? Task.CompletedTask], TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
