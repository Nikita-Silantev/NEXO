using System.Data.Common;
using Google.Protobuf.WellKnownTypes;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Messages;
using Nexo.Server.Users;

namespace Nexo.Server.Chats;

/// <summary>Сборка Chat для конкретного пользователя (личное состояние, последнее сообщение, собеседник).</summary>
public sealed class ChatQueries(UserQueries users)
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string? Title { get; set; }
        public string? Description { get; set; }
        public Guid? AvatarFileId { get; set; }
        public string? PublicHandle { get; set; }
        public Guid? OwnerId { get; set; }
        public int? DefaultTtlSeconds { get; set; }
        public int? SlowModeSeconds { get; set; }
        public long TopSeq { get; set; }
        public int MemberCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Role { get; set; } = "MEMBER";
        public long Permissions { get; set; }
        public DateTime? MutedUntil { get; set; }
        public short? PinnedPosition { get; set; }
        public bool Archived { get; set; }
        public long LastReadSeq { get; set; }
        public long LastDeliveredSeq { get; set; }
        public long Unread { get; set; }
        public Guid? PeerId { get; set; }
    }

    private const string Select =
        @"select c.id, c.kind::text as kind, c.title, c.description, c.avatar_file_id, c.public_handle::text as public_handle, c.owner_id,
                 c.default_ttl_seconds, c.slow_mode_seconds, c.top_seq, c.member_count, c.created_at,
                 m.role::text as role, m.permissions, m.muted_until, m.pinned_position, m.archived, m.last_read_seq, m.last_delivered_seq,
                 (select count(*) from messages x where x.chat_id = c.id and x.seq > m.last_read_seq and x.sender_id <> m.user_id and x.deleted_at is null) as unread,
                 (select case when dp.user_low = m.user_id then dp.user_high else dp.user_low end from direct_pairs dp where dp.chat_id = c.id) as peer_id
          from chats c join chat_members m on m.chat_id = c.id and m.user_id = @u and m.left_at is null
          where c.deleted_at is null";

    public static ChatKind ParseKind(string s) => s switch
    {
        "DIRECT" => ChatKind.Direct, "GROUP" => ChatKind.Group, "CHANNEL" => ChatKind.Channel, "SECRET" => ChatKind.Secret, _ => ChatKind.Unspecified,
    };

    private static MemberRole ParseRole(string s) => s switch
    {
        "OWNER" => MemberRole.Owner, "ADMIN" => MemberRole.Admin, "SUBSCRIBER" => MemberRole.Subscriber, _ => MemberRole.Member,
    };

    /// <summary>Загружает чаты пользователя по идентификаторам (только те, где он состоит).</summary>
    public async Task<List<Chat>> LoadAsync(DbConnection con, Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken ct, DbTransaction? tx = null)
    {
        if (ids.Count == 0) return [];
        var rows = await con.ListAsync<Row>(Select + " and c.id = any(@ids) order by c.updated_at desc", new { u = userId, ids = ids.ToArray() }, tx, ct);
        return await BuildAsync(con, userId, rows, ct, tx);
    }

    public async Task<List<Chat>> PageAsync(DbConnection con, Guid userId, bool includeArchived, int limit, int offset, CancellationToken ct)
    {
        var rows = await con.ListAsync<Row>(
            Select + @" and (@arch or not m.archived)
                        order by coalesce(m.pinned_position, 0) desc, c.updated_at desc limit @l offset @o",
            new { u = userId, arch = includeArchived, l = limit, o = offset }, ct: ct);
        return await BuildAsync(con, userId, rows, ct, null);
    }

    private async Task<List<Chat>> BuildAsync(DbConnection con, Guid userId, List<Row> rows, CancellationToken ct, DbTransaction? tx)
    {
        if (rows.Count == 0) return [];
        var chatIds = rows.Select(r => r.Id).ToList();
        var lastRows = await con.ListAsync<MessageRow>(
            "select distinct on (chat_id) " + MessageRow.Select["select ".Length..] + " where chat_id = any(@ids) order by chat_id, seq desc",
            new { ids = chatIds.ToArray() }, tx, ct);
        var last = lastRows.ToDictionary(x => x.ChatId);
        var peerIds = rows.Where(r => r.PeerId.HasValue).Select(r => r.PeerId!.Value).Distinct().ToList();
        var peers = await users.ProfilesAsync(con, userId, peerIds, ct, tx);
        var result = new List<Chat>(rows.Count);
        foreach (var r in rows)
        {
            var c = new Chat
            {
                ChatId = r.Id.ToString(), Kind = ParseKind(r.Kind), Title = r.Title ?? "", Description = r.Description ?? "",
                AvatarFileId = r.AvatarFileId?.ToString() ?? "", PublicHandle = r.PublicHandle ?? "", OwnerId = r.OwnerId?.ToString() ?? "",
                DefaultTtlSeconds = r.DefaultTtlSeconds ?? 0, SlowModeSeconds = r.SlowModeSeconds ?? 0, TopSeq = r.TopSeq,
                MemberCount = r.MemberCount, CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
                Me = new MemberState
                {
                    Role = ParseRole(r.Role), Permissions = r.Permissions,
                    MutedUntil = r.MutedUntil is { } mu ? Timestamp.FromDateTime(DateTime.SpecifyKind(mu, DateTimeKind.Utc)) : null,
                    PinnedPosition = r.PinnedPosition ?? 0, Archived = r.Archived, LastReadSeq = r.LastReadSeq,
                    LastDeliveredSeq = r.LastDeliveredSeq, UnreadCount = r.Unread,
                },
            };
            if (r.PeerId is { } p && peers.TryGetValue(p, out var prof)) c.DirectPeer = prof;
            if (last.TryGetValue(r.Id, out var lm)) c.LastMessage = MessageMapper.ToProto(lm);
            result.Add(c);
        }
        return result;
    }
}
