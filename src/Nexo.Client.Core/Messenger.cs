using Google.Protobuf.WellKnownTypes;
using Nexo.Core;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

/// <summary>Действия пользователя над чатами и сообщениями. Чтение идёт из локальной БД, запись – на сервер или в очередь.</summary>
public sealed class Messenger(NexoApi api, LocalDb db, SyncEngine sync, string myUserId)
{
    public string MyUserId => myUserId;
    public LocalDb Db => db;

    public async Task<Chat> OpenDirectAsync(string username, CancellationToken ct = default)
    {
        var user = await api.CallAsync(h => api.Users.GetUserAsync(new GetUserRequest { Username = UsernameRules.Normalize(username.TrimStart('@')) }, h, cancellationToken: ct).ResponseAsync, ct);
        var chat = await api.CallAsync(h => api.Chats.GetOrCreateDirectChatAsync(new GetOrCreateDirectChatRequest { UserId = user.UserId }, h, cancellationToken: ct).ResponseAsync, ct);
        db.UpsertChat(chat);
        return chat;
    }

    public async Task<List<UserProfile>> SearchUsersAsync(string query, CancellationToken ct = default)
    {
        var r = await api.CallAsync(h => api.Users.SearchUsersAsync(new SearchUsersRequest { Query = query.Trim(), Page = new PageRequest { Limit = 20 } }, h, cancellationToken: ct).ResponseAsync, ct);
        return r.Users.ToList();
    }

    /// <summary>Помещает текст в очередь (мгновенно показывается в чате) и запускает отправку (ФТ-СИН-05).</summary>
    public string Send(string chatId, string text, string replyToId = "")
    {
        var id = Guid.CreateVersion7().ToString();
        db.Enqueue(new LocalDb.OutboxItem(id, chatId, text, replyToId, DateTime.UtcNow, 0, null));
        sync.KickOutbox();
        return id;
    }

    public void RetryFailed(string clientMsgId) { db.SetOutboxState(clientMsgId, 0); sync.KickOutbox(); }
    public void DiscardFailed(string clientMsgId) => db.RemoveOutbox(clientMsgId);

    public async Task EditAsync(string chatId, string messageId, string text, CancellationToken ct = default)
    {
        var m = await api.CallAsync(h => api.Messages.EditMessageAsync(new EditMessageRequest
        { ChatId = chatId, MessageId = messageId, Text = new TextContent { Text = text } }, h, cancellationToken: ct).ResponseAsync, ct);
        db.UpsertMessage(m);
    }

    public async Task DeleteAsync(string chatId, IEnumerable<string> ids, bool forEveryone, CancellationToken ct = default)
    {
        var req = new DeleteMessagesRequest { ChatId = chatId, ForEveryone = forEveryone };
        req.MessageIds.AddRange(ids);
        await api.CallAsync(h => api.Messages.DeleteMessagesAsync(req, h, cancellationToken: ct).ResponseAsync, ct);
        if (!forEveryone) foreach (var id in req.MessageIds) db.RemoveMessage(id);
    }

    /// <summary>Подгружает более старые сообщения с сервера, если локально их недостаточно.</summary>
    public async Task<bool> LoadOlderAsync(string chatId, int limit = 50, CancellationToken ct = default)
    {
        var min = db.MinSeq(chatId) ?? 0;
        var r = await api.CallAsync(h => api.Messages.GetHistoryAsync(new GetHistoryRequest
        { ChatId = chatId, AnchorSeq = min, Direction = GetHistoryRequest.Types.Direction.Before, Limit = limit }, h, cancellationToken: ct).ResponseAsync, ct);
        foreach (var m in r.Messages) db.UpsertMessage(m);
        return r.HasMoreBefore;
    }

    public async Task MarkReadAsync(string chatId, CancellationToken ct = default)
    {
        var chat = db.GetChat(chatId);
        if (chat?.Me is null || chat.Me.UnreadCount == 0) return;
        await api.CallAsync(h => api.Messages.MarkReadAsync(new MarkReadRequest { ChatId = chatId, UpToSeq = chat.TopSeq }, h, cancellationToken: ct).ResponseAsync, ct);
        chat.Me.LastReadSeq = chat.TopSeq; chat.Me.UnreadCount = 0;
        db.UpsertChat(chat);
    }

    public async Task SendTypingAsync(string chatId, CancellationToken ct = default)
    {
        try { await api.CallAsync(h => api.Messages.SetTypingAsync(new SetTypingRequest { ChatId = chatId, Action = SetTypingRequest.Types.Action.Typing }, h, cancellationToken: ct).ResponseAsync, ct); }
        catch { /* индикатор набора не критичен */ }
    }

    public Task MuteAsync(string chatId, bool mute) => api.CallAsync(h => api.Chats.MuteChatAsync(new MuteChatRequest
    { ChatId = chatId, MutedUntil = mute ? Timestamp.FromDateTime(DateTime.UtcNow.AddYears(10)) : null }, h).ResponseAsync);

    public Task PinAsync(string chatId, bool pin) => api.CallAsync(h => api.Chats.PinChatAsync(new PinChatRequest { ChatId = chatId, Pinned = pin }, h).ResponseAsync);

    public Task ArchiveAsync(string chatId, bool archive) => api.CallAsync(h => api.Chats.ArchiveChatAsync(new ArchiveChatRequest { ChatId = chatId, Archived = archive }, h).ResponseAsync);

    public async Task LogoutAsync()
    {
        try { await api.CallAsync(h => api.Auth.LogoutAsync(new Empty(), h).ResponseAsync); } catch { /* сервер недоступен – локальные данные всё равно удаляются */ }
    }
}
