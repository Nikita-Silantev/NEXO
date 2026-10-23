using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Sync;
using Npgsql;

namespace Nexo.Server.Chats;

/// <summary>Личные чаты и личное состояние чата (ФТ-ЧАТ-01). Группы и каналы – этап Э2.</summary>
public sealed class ChatServiceImpl(NpgsqlDataSource ds, ChatQueries chats, UpdateJournal journal) : ChatService.ChatServiceBase
{
    // права участника личного чата: SEND_MESSAGES | SEND_MEDIA
    private const long DirectPermissions = 1 | 2;

    public override async Task<ListChatsResponse> ListChats(ListChatsRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var limit = Math.Clamp(r.Page?.Limit ?? 50, 1, 100);
        var offset = int.TryParse(r.Page?.Cursor, out var o) ? Math.Max(o, 0) : 0;
        await using var con = await ds.OpenAsync(ct);
        // Позиция журнала читается ДО выборки: события, случившиеся во время чтения, придут повторно и применятся идемпотентно.
        var seq = await con.ScalarAsync<long>("select coalesce((select last_seq from user_update_counters where user_id = @u), 0)", new { u = a.UserId }, ct: ct);
        var list = await chats.PageAsync(con, a.UserId, r.IncludeArchived, limit + 1, offset, ct);
        var res = new ListChatsResponse
        {
            SnapshotUpdateSeq = seq,
            Page = new PageInfo { HasMore = list.Count > limit, NextCursor = list.Count > limit ? (offset + limit).ToString() : "" },
        };
        res.Chats.AddRange(list.Take(limit));
        return res;
    }

    public override async Task<Chat> GetChat(GetChatRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        var list = await chats.LoadAsync(con, a.UserId, [CallExtensions.ParseId(r.ChatId)], ct);
        return list.FirstOrDefault() ?? throw NexoException.NotFound();
    }

    public override async Task<Chat> GetOrCreateDirectChat(GetOrCreateDirectChatRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var peer = CallExtensions.ParseId(r.UserId);
        if (peer == a.UserId) throw NexoException.Invalid(ErrorReason.PermissionDenied, "self");
        await using var con = await ds.OpenAsync(ct);
        var peerOk = await con.ScalarAsync<bool>("select exists(select 1 from users where id = @p and state = 'ACTIVE')", new { p = peer }, ct: ct);
        if (!peerOk) throw NexoException.NotFound();
        if (await con.ScalarAsync<bool>("select exists(select 1 from user_blocks where blocker_id = @p and blocked_id = @u)", new { p = peer, u = a.UserId }, ct: ct))
            throw NexoException.Denied(ErrorReason.BlockedByPeer);

        var chatId = await FindDirectAsync(con, a.UserId, peer, ct);
        if (chatId is null)
        {
            var newId = Guid.CreateVersion7();
            await using var tx = await con.BeginTransactionAsync(ct);
            try
            {
                await con.ExecAsync("insert into chats(id, kind, member_count) values (@i, 'DIRECT', 2)", new { i = newId }, tx, ct);
                await con.ExecAsync("insert into direct_pairs(chat_id, user_low, user_high) values (@i, least(@a::uuid, @b::uuid), greatest(@a::uuid, @b::uuid))",
                    new { i = newId, a = a.UserId, b = peer }, tx, ct);
                foreach (var u in new[] { a.UserId, peer })
                    await con.ExecAsync("insert into chat_members(chat_id, user_id, role, permissions) values (@c, @u, 'MEMBER', @p)",
                        new { c = newId, u, p = DirectPermissions }, tx, ct);
                var pending = new List<(Guid, Update)>();
                foreach (var u in new[] { a.UserId, peer }.Order())
                {
                    var chat = (await chats.LoadAsync(con, u, [newId], ct, tx)).First();
                    pending.Add(await journal.AppendAsync(con, tx, u, up => up.Chat = new ChatUpdate { Chat = chat }, ct));
                }
                await tx.CommitAsync(ct);
                journal.Publish(pending);
                chatId = newId;
            }
            catch (PostgresException e) when (e.SqlState == "23505")
            {
                // Параллельный запрос уже создал чат.
                await tx.RollbackAsync(ct);
                chatId = await FindDirectAsync(con, a.UserId, peer, ct);
                if (chatId is null) throw;
            }
        }
        return (await chats.LoadAsync(con, a.UserId, [chatId.Value], ct)).FirstOrDefault() ?? throw NexoException.NotFound();
    }

    private static Task<Guid?> FindDirectAsync(NpgsqlConnection con, Guid a, Guid b, CancellationToken ct) =>
        con.OneAsync<Guid?>("select chat_id from direct_pairs where user_low = least(@a::uuid, @b::uuid) and user_high = greatest(@a::uuid, @b::uuid)", new { a, b }, ct: ct);

    // ---------------- Личные настройки чата ----------------

    private async Task UpdateMemberAsync(ServerCallContext context, string chatId, string set, object extra)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var id = CallExtensions.ParseId(chatId);
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var p = new Dapper.DynamicParameters(extra);
        p.Add("c", id); p.Add("u", a.UserId);
        var n = await con.ExecAsync($"update chat_members set {set} where chat_id = @c and user_id = @u and left_at is null", p, tx, ct);
        if (n == 0) throw NexoException.NotFound();
        var chat = (await chats.LoadAsync(con, a.UserId, [id], ct, tx)).First();
        var upd = await journal.AppendAsync(con, tx, a.UserId, up => up.Chat = new ChatUpdate { Chat = chat }, ct);
        await tx.CommitAsync(ct);
        journal.Publish([upd]);
    }

    public override async Task<Empty> MuteChat(MuteChatRequest r, ServerCallContext context)
    {
        DateTime? until = r.MutedUntil is null ? null : r.MutedUntil.ToDateTime();
        await UpdateMemberAsync(context, r.ChatId, "muted_until = @m", new { m = until });
        return new Empty();
    }

    public override async Task<Empty> PinChat(PinChatRequest r, ServerCallContext context)
    {
        // Закреплённые чаты упорядочены по убыванию pinned_position; новый закреплённый чат встаёт первым.
        await UpdateMemberAsync(context, r.ChatId,
            r.Pinned ? "pinned_position = (select coalesce(max(pinned_position), 0) + 1 from chat_members where user_id = @u)" : "pinned_position = null",
            new { });
        return new Empty();
    }

    public override async Task<Empty> ArchiveChat(ArchiveChatRequest r, ServerCallContext context)
    {
        await UpdateMemberAsync(context, r.ChatId, "archived = @x", new { x = r.Archived });
        return new Empty();
    }
}
