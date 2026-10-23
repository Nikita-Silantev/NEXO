using System.Data.Common;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Sync;
using Npgsql;

namespace Nexo.Server.Messages;

/// <summary>
/// Сообщения (ФТ-СООБ-01..03, ФТ-СООБ-09). Этап Э1: текст, ответы, пересылка, правка (48 ч), удаление, чтение.
/// Медиа, реакции, закрепление и поиск – этап Э2 (методы возвращают Unimplemented).
/// </summary>
public sealed class MessageServiceImpl(NpgsqlDataSource ds, UpdateJournal journal, IRateLimiter limiter) : MessageService.MessageServiceBase
{
    public const int MaxTextLength = 4096;
    public static readonly TimeSpan EditWindow = TimeSpan.FromHours(48);

    private sealed class MemberCtx
    {
        public string Kind { get; set; } = "";
        public long Permissions { get; set; }
        public int? DefaultTtlSeconds { get; set; }
        public Guid? PeerId { get; set; }
    }

    private static async Task<MemberCtx> RequireMemberAsync(DbConnection con, Guid chatId, Guid userId, DbTransaction? tx, CancellationToken ct)
    {
        var m = await con.OneAsync<MemberCtx>(
            @"select c.kind::text as kind, m.permissions, c.default_ttl_seconds,
                     (select case when dp.user_low = m.user_id then dp.user_high else dp.user_low end from direct_pairs dp where dp.chat_id = c.id) as peer_id
              from chat_members m join chats c on c.id = m.chat_id
              where m.chat_id = @c and m.user_id = @u and m.left_at is null and c.deleted_at is null",
            new { c = chatId, u = userId }, tx, ct);
        return m ?? throw NexoException.NotFound();
    }

    private static async Task<List<Guid>> MemberIdsAsync(DbConnection con, Guid chatId, DbTransaction? tx, CancellationToken ct) =>
        (await con.ListAsync<Guid>("select user_id from chat_members where chat_id = @c and left_at is null order by user_id", new { c = chatId }, tx, ct));

    /// <summary>Проверка запретов для личного чата: блокировка и настройка «кто может писать».</summary>
    private static async Task CheckDirectAllowedAsync(DbConnection con, Guid me, Guid peer, DbTransaction? tx, CancellationToken ct)
    {
        var blocked = await con.ScalarAsync<bool>("select exists(select 1 from user_blocks where blocker_id = @p and blocked_id = @m)", new { p = peer, m = me }, tx, ct);
        if (blocked) throw NexoException.Denied(ErrorReason.BlockedByPeer);
        var restricted = await con.ScalarAsync<bool>(
            @"select exists(select 1 from privacy_settings ps where ps.user_id = @p and ps.message_from = 'CONTACTS'
                and not exists(select 1 from contact_links cl where cl.status = 'ACCEPTED'
                               and ((cl.requester_id = @p and cl.addressee_id = @m) or (cl.requester_id = @m and cl.addressee_id = @p))))",
            new { p = peer, m = me }, tx, ct);
        if (restricted) throw NexoException.Denied(ErrorReason.PrivacyRestricted);
    }

    // ---------------- Отправка ----------------

    public override async Task<SendMessageResponse> SendMessage(SendMessageRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        if (!limiter.TryAcquire("send:" + a.UserId, 120, TimeSpan.FromMinutes(1), out var ra)) throw NexoException.RateLimited(ra);
        var chatId = CallExtensions.ParseId(r.ChatId);
        var clientId = CallExtensions.ParseId(r.ClientMsgId);
        var (text, entities) = ValidateText(r.Content);
        Guid? replyTo = string.IsNullOrEmpty(r.ReplyToId) ? null : CallExtensions.ParseId(r.ReplyToId);

        await using var con = await ds.OpenAsync(ct);
        var dup = await FindByClientIdAsync(con, chatId, a.UserId, clientId, ct);
        if (dup is not null) return new SendMessageResponse { Message = MessageMapper.ToProto(dup), Duplicate = true };

        try
        {
            await using var tx = await con.BeginTransactionAsync(ct);
            var (row, pending) = await InsertAsync(con, tx, a, chatId, clientId, text, entities, replyTo, r.TtlSeconds, null, ct);
            await tx.CommitAsync(ct);
            journal.Publish(pending);
            return new SendMessageResponse { Message = MessageMapper.ToProto(row) };
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            // Параллельная повторная отправка с тем же client_msg_id.
            var again = await FindByClientIdAsync(con, chatId, a.UserId, clientId, ct) ?? throw NexoException.Invalid(ErrorReason.VersionConflict);
            return new SendMessageResponse { Message = MessageMapper.ToProto(again), Duplicate = true };
        }
    }

    private static (string Text, IReadOnlyList<TextEntity> Entities) ValidateText(MessageContent? c)
    {
        if (c is null || c.ContentCase != MessageContent.ContentOneofCase.Text)
            throw new NexoException(ErrorReason.NotFound, StatusCode.Unimplemented, "Этап Э1 поддерживает только текстовые сообщения");
        var text = c.Text.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) throw NexoException.Invalid(ErrorReason.MessageTooLong, "empty");
        if (text.Length > MaxTextLength) throw NexoException.Invalid(ErrorReason.MessageTooLong);
        foreach (var e in c.Text.Entities)
            if (e.Offset < 0 || e.Length <= 0 || e.Offset + e.Length > text.Length) throw NexoException.Invalid(ErrorReason.MessageTooLong, "entity");
        return (text, c.Text.Entities.ToList());
    }

    private static Task<MessageRow?> FindByClientIdAsync(DbConnection con, Guid chatId, Guid sender, Guid clientId, CancellationToken ct) =>
        con.OneAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and sender_id = @s and client_msg_id = @m", new { c = chatId, s = sender, m = clientId }, ct: ct);

    /// <summary>Создаёт сообщение с очередным seq и добавляет обновление каждому участнику (включая другие устройства отправителя).</summary>
    private async Task<(MessageRow Row, List<(Guid, Update)> Pending)> InsertAsync(
        DbConnection con, DbTransaction tx, AuthContext a, Guid chatId, Guid clientId, string text, IReadOnlyList<TextEntity> entities,
        Guid? replyTo, int ttlSeconds, ForwardOrigin? forward, CancellationToken ct)
    {
        var ctx = await RequireMemberAsync(con, chatId, a.UserId, tx, ct);
        if (ctx.Kind == "DIRECT")
        {
            if (ctx.PeerId is not { } peer) throw NexoException.NotFound();
            await CheckDirectAllowedAsync(con, a.UserId, peer, tx, ct);
        }
        if ((ctx.Permissions & 1) == 0) throw NexoException.Denied();
        if (replyTo is { } rt)
        {
            var ok = await con.ScalarAsync<bool>("select exists(select 1 from messages where id = @i and chat_id = @c)", new { i = rt, c = chatId }, tx, ct);
            if (!ok) throw NexoException.Invalid(ErrorReason.NotFound, "reply_to");
        }
        var ttl = ttlSeconds > 0 ? ttlSeconds : ctx.DefaultTtlSeconds;

        // UPDATE блокирует строку чата: номера seq выдаются строго по очереди (ФТ-СООБ-09).
        var seq = await con.ScalarAsync<long>("update chats set top_seq = top_seq + 1, updated_at = now() where id = @c returning top_seq", new { c = chatId }, tx, ct);
        var id = Guid.CreateVersion7();
        await con.ExecAsync(
            @"insert into messages(id, chat_id, seq, sender_id, sender_device_id, client_msg_id, kind, body, entities, reply_to_id, forward_origin, ttl_seconds)
              values (@id, @c, @seq, @s, @d, @cm, 'TEXT', @body, @ent::jsonb, @rt, @fw::jsonb, @ttl)",
            new
            {
                id, c = chatId, seq, s = a.UserId, d = a.DeviceId, cm = clientId, body = text, ent = MessageMapper.SerializeEntities(entities),
                rt = replyTo, fw = forward is null ? null : MessageMapper.SerializeForward(forward), ttl,
            }, tx, ct);
        var row = (await con.OneAsync<MessageRow>(MessageRow.Select + " where id = @i", new { i = id }, tx, ct))!;
        var msg = MessageMapper.ToProto(row);
        var pending = new List<(Guid, Update)>();
        foreach (var u in await MemberIdsAsync(con, chatId, tx, ct))
            pending.Add(await journal.AppendAsync(con, tx, u, up => up.NewMessage = new NewMessageUpdate { Message = msg }, ct));
        return (row, pending);
    }

    // ---------------- Правка и удаление ----------------

    public override async Task<Message> EditMessage(EditMessageRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var chatId = CallExtensions.ParseId(r.ChatId); var id = CallExtensions.ParseId(r.MessageId);
        var (text, entities) = ValidateText(new MessageContent { Text = r.Text });
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, tx, ct);
        var row = await con.OneAsync<MessageRow>(MessageRow.Select + " where id = @i and chat_id = @c for update", new { i = id, c = chatId }, tx, ct) ?? throw NexoException.NotFound();
        if (row.SenderId != a.UserId || row.DeletedAt is not null || row.Kind != "TEXT") throw NexoException.Denied();
        if (DateTime.UtcNow - DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc) > EditWindow) throw NexoException.Denied(ErrorReason.EditWindowExpired);
        await con.ExecAsync("update messages set body = @b, entities = @e::jsonb, edited_at = now() where id = @i",
            new { i = id, b = text, e = MessageMapper.SerializeEntities(entities) }, tx, ct);
        var msg = MessageMapper.ToProto((await con.OneAsync<MessageRow>(MessageRow.Select + " where id = @i", new { i = id }, tx, ct))!);
        var pending = new List<(Guid, Update)>();
        foreach (var u in await MemberIdsAsync(con, chatId, tx, ct))
            pending.Add(await journal.AppendAsync(con, tx, u, up => up.MessageEdited = new MessageEditedUpdate { Message = msg }, ct));
        await tx.CommitAsync(ct);
        journal.Publish(pending);
        return msg;
    }

    public override async Task<Empty> DeleteMessages(DeleteMessagesRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var chatId = CallExtensions.ParseId(r.ChatId);
        var ids = r.MessageIds.Select(CallExtensions.ParseId).Distinct().Take(100).ToList();
        if (ids.Count == 0) return new Empty();
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, tx, ct);
        var pending = new List<(Guid, Update)>();
        if (r.ForEveryone)
        {
            // Удалять у всех можно только свои сообщения (права модераторов групп – этап Э2).
            var rows = await con.ListAsync<(Guid Id, long Seq)>(
                @"update messages set body = null, entities = null, deleted_at = now()
                  where chat_id = @c and sender_id = @s and deleted_at is null and id in @ids returning id, seq",
                new { c = chatId, s = a.UserId, ids }, tx, ct);
            if (rows.Count > 0)
            {
                var upd = new MessageDeletedUpdate { ChatId = chatId.ToString() };
                foreach (var (mid, seq) in rows) { upd.MessageIds.Add(mid.ToString()); upd.Seqs.Add(seq); }
                foreach (var u in await MemberIdsAsync(con, chatId, tx, ct))
                    pending.Add(await journal.AppendAsync(con, tx, u, up => up.MessageDeleted = upd, ct));
            }
        }
        else
        {
            // «Удалить у себя»: на других устройствах пользователя сообщение убирается обновлением.
            // Ограничение Э1: схема БД не хранит скрытые сообщения, поэтому после переустановки они вернутся из истории.
            var rows = await con.ListAsync<(Guid Id, long Seq)>("select id, seq from messages where chat_id = @c and id in @ids", new { c = chatId, ids }, tx, ct);
            if (rows.Count > 0)
            {
                var upd = new MessageDeletedUpdate { ChatId = chatId.ToString() };
                foreach (var (mid, seq) in rows) { upd.MessageIds.Add(mid.ToString()); upd.Seqs.Add(seq); }
                pending.Add(await journal.AppendAsync(con, tx, a.UserId, up => up.MessageDeleted = upd, ct));
            }
        }
        await tx.CommitAsync(ct);
        journal.Publish(pending);
        return new Empty();
    }

    // ---------------- Чтение истории ----------------

    public override async Task<GetHistoryResponse> GetHistory(GetHistoryRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var chatId = CallExtensions.ParseId(r.ChatId);
        var limit = Math.Clamp(r.Limit == 0 ? 50 : r.Limit, 1, 100);
        await using var con = await ds.OpenAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, null, ct);
        var top = await con.ScalarAsync<long>("select top_seq from chats where id = @c", new { c = chatId }, ct: ct);
        var anchor = r.AnchorSeq <= 0 ? top + 1 : r.AnchorSeq;
        var dir = r.Direction == GetHistoryRequest.Types.Direction.Unspecified
            ? (r.AnchorSeq <= 0 ? GetHistoryRequest.Types.Direction.Before : GetHistoryRequest.Types.Direction.Before) : r.Direction;

        List<MessageRow> rows;
        switch (dir)
        {
            case GetHistoryRequest.Types.Direction.After:
                rows = await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and seq > @a order by seq asc limit @l", new { c = chatId, a = anchor, l = limit + 1 }, ct: ct);
                break;
            case GetHistoryRequest.Types.Direction.Around:
                var half = Math.Max(limit / 2, 1);
                var before = await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and seq <= @a order by seq desc limit @l", new { c = chatId, a = anchor, l = half + 1 }, ct: ct);
                var after = await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and seq > @a order by seq asc limit @l", new { c = chatId, a = anchor, l = limit - half + 1 }, ct: ct);
                var res0 = new GetHistoryResponse { HasMoreBefore = before.Count > half, HasMoreAfter = after.Count > limit - half };
                foreach (var m in before.Take(half).Reverse().Concat(after.Take(limit - half))) res0.Messages.Add(MessageMapper.ToProto(m));
                return res0;
            default:
                rows = await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and seq < @a order by seq desc limit @l", new { c = chatId, a = anchor, l = limit + 1 }, ct: ct);
                break;
        }
        var more = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        if (dir != GetHistoryRequest.Types.Direction.After) page.Reverse();
        var res = new GetHistoryResponse { HasMoreBefore = dir != GetHistoryRequest.Types.Direction.After && more, HasMoreAfter = dir == GetHistoryRequest.Types.Direction.After && more };
        foreach (var m in page) res.Messages.Add(MessageMapper.ToProto(m));
        return res;
    }

    public override async Task<GetMessagesResponse> GetMessages(GetMessagesRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var chatId = CallExtensions.ParseId(r.ChatId);
        var ids = r.MessageIds.Select(CallExtensions.ParseId).Distinct().Take(100).ToList();
        await using var con = await ds.OpenAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, null, ct);
        var rows = ids.Count == 0 ? [] : await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and id in @ids order by seq", new { c = chatId, ids }, ct: ct);
        var res = new GetMessagesResponse();
        foreach (var m in rows) res.Messages.Add(MessageMapper.ToProto(m));
        return res;
    }

    // ---------------- Прочтение ----------------

    public override async Task<Empty> MarkRead(MarkReadRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var chatId = CallExtensions.ParseId(r.ChatId);
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, tx, ct);
        var top = await con.ScalarAsync<long>("select top_seq from chats where id = @c", new { c = chatId }, tx, ct);
        var upTo = Math.Min(r.UpToSeq, top);
        var prev = await con.ScalarAsync<long>("select last_read_seq from chat_members where chat_id = @c and user_id = @u for update", new { c = chatId, u = a.UserId }, tx, ct);
        if (upTo <= prev) { await tx.RollbackAsync(ct); return new Empty(); }
        await con.ExecAsync("update chat_members set last_read_seq = @s, last_delivered_seq = greatest(last_delivered_seq, @s) where chat_id = @c and user_id = @u",
            new { s = upTo, c = chatId, u = a.UserId }, tx, ct);
        // Автоудаление считается с момента первого прочтения получателем (ФТ-СООБ-06).
        await con.ExecAsync(
            @"update messages set first_read_at = now(), expires_at = now() + ttl_seconds * interval '1 second'
              where chat_id = @c and seq > @p and seq <= @s and sender_id <> @u and ttl_seconds is not null and first_read_at is null and deleted_at is null",
            new { c = chatId, p = prev, s = upTo, u = a.UserId }, tx, ct);
        var showReceipt = await con.ScalarAsync<bool>("select read_receipts from privacy_settings where user_id = @u", new { u = a.UserId }, tx, ct);
        var pending = new List<(Guid, Update)>();
        foreach (var u in await MemberIdsAsync(con, chatId, tx, ct))
        {
            var self = u == a.UserId;
            if (!self && !showReceipt) continue;
            var ru = new ReadStateUpdate { ChatId = chatId.ToString(), ReaderId = a.UserId.ToString(), UpToSeq = upTo, IsSelf = self };
            pending.Add(await journal.AppendAsync(con, tx, u, up => up.ReadState = ru, ct));
        }
        await tx.CommitAsync(ct);
        journal.Publish(pending);
        return new Empty();
    }

    // ---------------- Пересылка и «печатает» ----------------

    public override async Task<ForwardMessagesResponse> ForwardMessages(ForwardMessagesRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var from = CallExtensions.ParseId(r.FromChatId); var to = CallExtensions.ParseId(r.ToChatId);
        var ids = r.MessageIds.Select(CallExtensions.ParseId).Take(100).ToList();
        if (ids.Count == 0 || r.ClientMsgIds.Count != ids.Count) throw NexoException.Invalid(ErrorReason.NotFound, "client_msg_ids");
        var clientIds = r.ClientMsgIds.Select(CallExtensions.ParseId).ToList();
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        await RequireMemberAsync(con, from, a.UserId, tx, ct);
        var rows = (await con.ListAsync<MessageRow>(MessageRow.Select + " where chat_id = @c and id in @ids and deleted_at is null", new { c = from, ids }, tx, ct))
            .ToDictionary(x => x.Id);
        var res = new ForwardMessagesResponse();
        var pendingAll = new List<(Guid, Update)>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (!rows.TryGetValue(ids[i], out var src) || src.Kind != "TEXT") continue;   // другие виды – этап Э2
            var existing = await FindByClientIdAsync(con, to, a.UserId, clientIds[i], ct);
            if (existing is not null) { res.Messages.Add(MessageMapper.ToProto(existing)); continue; }
            var origin = new ForwardOrigin
            {
                FromUserId = src.SenderId.ToString(), FromChatId = from.ToString(), FromMessageId = src.Id.ToString(),
                OriginalDate = Timestamp.FromDateTime(DateTime.SpecifyKind(src.CreatedAt, DateTimeKind.Utc)),
            };
            var (row, pending) = await InsertAsync(con, tx, a, to, clientIds[i], src.Body ?? "", MessageMapper.ParseEntities(src.Entities).ToList(), null, 0, origin, ct);
            res.Messages.Add(MessageMapper.ToProto(row));
            pendingAll.AddRange(pending);
        }
        await tx.CommitAsync(ct);
        journal.Publish(pendingAll);
        return res;
    }

    public override async Task<Empty> SetTyping(SetTypingRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        if (!limiter.TryAcquire("typing:" + a.UserId, 30, TimeSpan.FromSeconds(10), out _)) return new Empty();   // тихо отбрасываем
        var chatId = CallExtensions.ParseId(r.ChatId);
        await using var con = await ds.OpenAsync(ct);
        await RequireMemberAsync(con, chatId, a.UserId, null, ct);
        if (!await con.ScalarAsync<bool>("select typing_indicator from privacy_settings where user_id = @u", new { u = a.UserId }, ct: ct)) return new Empty();
        foreach (var u in await MemberIdsAsync(con, chatId, null, ct))
        {
            if (u == a.UserId) continue;
            journal.PublishEphemeral(u, up => up.Typing = new TypingUpdate { ChatId = chatId.ToString(), UserId = a.UserId.ToString(), Action = r.Action });
        }
        return new Empty();
    }
}
