using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Sync;
using Npgsql;

namespace Nexo.Server.Users;

/// <summary>Профиль, поиск, приватность, контакты и блокировки (ФТ-ЧАТ-06, ФТ-ЧАТ-07, ФТ-ПРИ-04).</summary>
public sealed class UserServiceImpl(NpgsqlDataSource ds, UserQueries users, UpdateJournal journal, IRateLimiter limiter) : UserService.UserServiceBase
{
    private sealed class PrivacyRow
    {
        public bool Searchable { get; set; }
        public string LastSeenVis { get; set; } = "";
        public string StatusVis { get; set; } = "";
        public string AvatarVis { get; set; } = "";
        public string BioVis { get; set; } = "";
        public string MessageFrom { get; set; } = "";
        public string GroupInviteFrom { get; set; } = "";
        public string CallFrom { get; set; } = "";
        public bool ReadReceipts { get; set; }
        public bool TypingIndicator { get; set; }
    }

    private const string PrivacySelect =
        @"select searchable, last_seen_vis::text as last_seen_vis, status_vis::text as status_vis, avatar_vis::text as avatar_vis, bio_vis::text as bio_vis,
                 message_from::text as message_from, group_invite_from::text as group_invite_from, call_from::text as call_from,
                 read_receipts, typing_indicator from privacy_settings";

    private static Visibility Vis(string s) => s switch { "EVERYONE" => Visibility.Everyone, "CONTACTS" => Visibility.Contacts, _ => Visibility.Nobody };
    private static string VisText(Visibility v) => v switch { Visibility.Everyone => "EVERYONE", Visibility.Contacts => "CONTACTS", Visibility.Nobody => "NOBODY", _ => throw NexoException.Invalid(ErrorReason.PrivacyRestricted) };
    private static Audience Aud(string s) => s == "EVERYONE" ? Audience.Everyone : Audience.Contacts;
    private static string AudText(Audience a) => a switch { Audience.Everyone => "EVERYONE", Audience.Contacts => "CONTACTS", _ => throw NexoException.Invalid(ErrorReason.PrivacyRestricted) };

    private static PrivacySettings ToProto(PrivacyRow r) => new()
    {
        Searchable = r.Searchable, LastSeen = Vis(r.LastSeenVis), Status = Vis(r.StatusVis), Avatar = Vis(r.AvatarVis), Bio = Vis(r.BioVis),
        MessageFrom = Aud(r.MessageFrom), GroupInviteFrom = Aud(r.GroupInviteFrom), CallFrom = Aud(r.CallFrom),
        ReadReceipts = r.ReadReceipts, TypingIndicator = r.TypingIndicator,
    };

    public override async Task<GetMeResponse> GetMe(Empty request, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        var profile = await users.ProfileAsync(con, a.UserId, a.UserId, ct) ?? throw NexoException.NotFound();
        var priv = await con.OneAsync<PrivacyRow>(PrivacySelect + " where user_id = @u", new { u = a.UserId }, ct: ct) ?? throw NexoException.NotFound();
        var extra = await con.OneAsync<MeExtra>(
            @"select totp_enabled, recovery_set_at, coalesce((select last_seq from user_update_counters where user_id = users.id), 0) as seq
              from users where id = @u", new { u = a.UserId }, ct: ct) ?? throw NexoException.NotFound();
        return new GetMeResponse
        {
            Profile = profile, Privacy = ToProto(priv), TotpEnabled = extra.TotpEnabled,
            RecoverySetAt = UserQueries.Ts(extra.RecoverySetAt), CurrentUpdateSeq = extra.Seq,
        };
    }

    private sealed class MeExtra
    {
        public bool TotpEnabled { get; set; }
        public DateTime RecoverySetAt { get; set; }
        public long Seq { get; set; }
    }

    public override async Task<UserProfile> UpdateProfile(UpdateProfileRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        if (!UsernameRules.IsValidDisplayName(r.DisplayName)) throw NexoException.Invalid(ErrorReason.UsernameInvalid, "display_name");
        var bio = (r.Bio ?? "").Trim();
        if (bio.Length > 300) throw NexoException.Invalid(ErrorReason.MessageTooLong, "bio");
        await using var con = await ds.OpenAsync(ct);
        await con.ExecAsync("update users set display_name = @d, bio = @b, updated_at = now() where id = @u",
            new { u = a.UserId, d = r.DisplayName.Trim(), b = bio.Length == 0 ? null : bio }, ct: ct);
        return await users.ProfileAsync(con, a.UserId, a.UserId, ct) ?? throw NexoException.NotFound();
    }

    public override async Task<UserProfile> GetUser(GetUserRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        var id = await ResolveAsync(con, r.RefCase == GetUserRequest.RefOneofCase.UserId ? r.UserId : null,
            r.RefCase == GetUserRequest.RefOneofCase.Username ? r.Username : null, ct);
        return await users.ProfileAsync(con, a.UserId, id, ct) ?? throw NexoException.NotFound();
    }

    internal static async Task<Guid> ResolveAsync(NpgsqlConnection con, string? userId, string? username, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            if (!Guid.TryParse(userId, out var g)) throw NexoException.NotFound();
            var ok = await con.ScalarAsync<bool>("select exists(select 1 from users where id = @i and state = 'ACTIVE')", new { i = g }, ct: ct);
            return ok ? g : throw NexoException.NotFound();
        }
        var name = UsernameRules.Normalize(username);
        var found = await con.OneAsync<Guid?>("select id from users where username = @n::citext and state = 'ACTIVE'", new { n = name }, ct: ct);
        return found ?? throw NexoException.NotFound();
    }

    public override async Task<SearchUsersResponse> SearchUsers(SearchUsersRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var q = (r.Query ?? "").Trim().ToLowerInvariant().TrimStart('@');
        if (q.Length < 3 || q.Length > 64) throw NexoException.Invalid(ErrorReason.UsernameInvalid, "query");
        if (!limiter.TryAcquire("search:" + a.UserId, 30, TimeSpan.FromMinutes(1), out var ra)) throw NexoException.RateLimited(ra);
        var like = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var limit = Math.Clamp(r.Page?.Limit ?? 20, 1, 50);
        var offset = int.TryParse(r.Page?.Cursor, out var o) ? Math.Max(o, 0) : 0;
        await using var con = await ds.OpenAsync(ct);
        var ids = await con.ListAsync<Guid>(
            @"select u.id from users u join privacy_settings p on p.user_id = u.id
              where u.state = 'ACTIVE' and u.id <> @v::uuid and p.searchable
                and (u.username::text like @prefix::text escape '\' or lower(u.display_name) like @contains::text escape '\')
                and not exists(select 1 from user_blocks b where b.blocker_id = u.id and b.blocked_id = @v::uuid)
              order by (u.username::text = @q::text) desc, u.username::text limit @l::int offset @o::int",
            new { v = a.UserId, prefix = like + "%", contains = "%" + like + "%", q, l = limit + 1, o = offset }, ct: ct);
        var page = ids.Take(limit).ToList();
        var profiles = await users.ProfilesAsync(con, a.UserId, page, ct);
        var res = new SearchUsersResponse { Page = new PageInfo { HasMore = ids.Count > limit, NextCursor = ids.Count > limit ? (offset + limit).ToString() : "" } };
        foreach (var id in page) if (profiles.TryGetValue(id, out var p)) res.Users.Add(p);
        return res;
    }

    public override async Task<PrivacySettings> GetPrivacy(Empty request, ServerCallContext context)
    {
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var row = await con.OneAsync<PrivacyRow>(PrivacySelect + " where user_id = @u", new { u = context.Auth().UserId }, ct: context.CancellationToken);
        return ToProto(row ?? throw NexoException.NotFound());
    }

    public override async Task<PrivacySettings> UpdatePrivacy(PrivacySettings r, ServerCallContext context)
    {
        var a = context.Auth();
        await using var con = await ds.OpenAsync(context.CancellationToken);
        await con.ExecAsync(
            @"update privacy_settings set searchable = @s, last_seen_vis = @ls::visibility, status_vis = @st::visibility, avatar_vis = @av::visibility,
                     bio_vis = @bv::visibility, message_from = @mf::audience, group_invite_from = @gi::audience, call_from = @cf::audience,
                     read_receipts = @rr, typing_indicator = @ti, updated_at = now() where user_id = @u",
            new
            {
                u = a.UserId, s = r.Searchable, ls = VisText(r.LastSeen), st = VisText(r.Status), av = VisText(r.Avatar), bv = VisText(r.Bio),
                mf = AudText(r.MessageFrom), gi = AudText(r.GroupInviteFrom), cf = AudText(r.CallFrom), rr = r.ReadReceipts, ti = r.TypingIndicator,
            }, ct: context.CancellationToken);
        return r;
    }

    // ---------------- Контакты ----------------

    private async Task<ContactLink> BuildLinkAsync(NpgsqlConnection con, Guid viewer, Guid linkId, CancellationToken ct)
    {
        var row = await con.OneAsync<LinkRow>(
            "select id, requester_id, addressee_id, status::text as status, created_at from contact_links where id = @i", new { i = linkId }, ct: ct)
            ?? throw NexoException.NotFound();
        var other = row.RequesterId == viewer ? row.AddresseeId : row.RequesterId;
        var profile = await users.ProfileAsync(con, viewer, other, ct);
        return new ContactLink
        {
            LinkId = row.Id.ToString(), User = profile, Status = UserQueries.ParseContact(row.Status),
            Incoming = row.AddresseeId == viewer, CreatedAt = UserQueries.Ts(row.CreatedAt),
        };
    }

    private sealed class LinkRow
    {
        public Guid Id { get; set; }
        public Guid RequesterId { get; set; }
        public Guid AddresseeId { get; set; }
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private async Task PublishContactAsync(NpgsqlConnection con, Guid linkId, Guid userA, Guid userB, bool removed, CancellationToken ct)
    {
        await using var tx = await con.BeginTransactionAsync(ct);
        var pending = new List<(Guid, Update)>();
        foreach (var u in new[] { userA, userB }.Order())
        {
            var link = removed ? new ContactLink { LinkId = linkId.ToString() } : await BuildLinkAsync(con, u, linkId, ct);
            pending.Add(await journal.AppendAsync(con, tx, u, up => up.Contact = new ContactUpdate { Link = link, Removed = removed }, ct));
        }
        await tx.CommitAsync(ct);
        journal.Publish(pending);
    }

    public override async Task<ContactLink> SendContactRequest(SendContactRequestRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        var target = await ResolveAsync(con, r.RefCase == SendContactRequestRequest.RefOneofCase.UserId ? r.UserId : null,
            r.RefCase == SendContactRequestRequest.RefOneofCase.Username ? r.Username : null, ct);
        if (target == a.UserId) throw NexoException.Invalid(ErrorReason.PermissionDenied, "self");
        var blocked = await con.ScalarAsync<bool>("select exists(select 1 from user_blocks where blocker_id = @t and blocked_id = @u)", new { t = target, u = a.UserId }, ct: ct);
        if (blocked) throw NexoException.Denied(ErrorReason.BlockedByPeer);
        var existing = await con.OneAsync<LinkRow>(
            @"select id, requester_id, addressee_id, status::text as status, created_at from contact_links
              where (requester_id = @u and addressee_id = @t) or (requester_id = @t and addressee_id = @u)", new { u = a.UserId, t = target }, ct: ct);
        Guid linkId;
        if (existing is null)
        {
            linkId = Guid.CreateVersion7();
            await con.ExecAsync("insert into contact_links(id, requester_id, addressee_id) values (@i, @u, @t)", new { i = linkId, u = a.UserId, t = target }, ct: ct);
        }
        else
        {
            linkId = existing.Id;
            // Встречный запрос = принятие; отклонённый запрос можно отправить повторно только мне.
            if (existing.Status == "PENDING" && existing.AddresseeId == a.UserId)
                await con.ExecAsync("update contact_links set status = 'ACCEPTED', responded_at = now() where id = @i", new { i = linkId }, ct: ct);
            else if (existing.Status == "DECLINED" && existing.RequesterId == a.UserId)
                throw NexoException.Denied(ErrorReason.PrivacyRestricted);
            else if (existing.Status == "DECLINED")
                await con.ExecAsync("update contact_links set requester_id = @u, addressee_id = @t, status = 'PENDING', responded_at = null where id = @i", new { i = linkId, u = a.UserId, t = target }, ct: ct);
        }
        await PublishContactAsync(con, linkId, a.UserId, target, false, ct);
        return await BuildLinkAsync(con, a.UserId, linkId, ct);
    }

    public override async Task<ContactLink> RespondContactRequest(RespondContactRequestRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var id = CallExtensions.ParseId(r.LinkId);
        await using var con = await ds.OpenAsync(ct);
        var row = await con.OneAsync<LinkRow>(
            "select id, requester_id, addressee_id, status::text as status, created_at from contact_links where id = @i and addressee_id = @u and status = 'PENDING'",
            new { i = id, u = a.UserId }, ct: ct) ?? throw NexoException.NotFound();
        await con.ExecAsync("update contact_links set status = @s::contact_status, responded_at = now() where id = @i",
            new { i = id, s = r.Accept ? "ACCEPTED" : "DECLINED" }, ct: ct);
        await PublishContactAsync(con, id, row.RequesterId, row.AddresseeId, false, ct);
        return await BuildLinkAsync(con, a.UserId, id, ct);
    }

    public override async Task<Empty> RemoveContact(RemoveContactRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var other = CallExtensions.ParseId(r.UserId);
        await using var con = await ds.OpenAsync(ct);
        var id = await con.OneAsync<Guid?>(
            "delete from contact_links where (requester_id = @u and addressee_id = @o) or (requester_id = @o and addressee_id = @u) returning id",
            new { u = a.UserId, o = other }, ct: ct);
        if (id is { } linkId) await PublishContactAsync(con, linkId, a.UserId, other, true, ct);
        return new Empty();
    }

    public override async Task<ListContactsResponse> ListContacts(ListContactsRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var limit = Math.Clamp(r.Page?.Limit ?? 100, 1, 100);
        var offset = int.TryParse(r.Page?.Cursor, out var o) ? Math.Max(o, 0) : 0;
        await using var con = await ds.OpenAsync(ct);
        var ids = await con.ListAsync<Guid>(
            @"select id from contact_links where (requester_id = @u or addressee_id = @u)
                 and (status = 'ACCEPTED' or (@p and status = 'PENDING')) order by created_at desc limit @l offset @o",
            new { u = a.UserId, p = r.IncludePending, l = limit + 1, o = offset }, ct: ct);
        var res = new ListContactsResponse { Page = new PageInfo { HasMore = ids.Count > limit, NextCursor = ids.Count > limit ? (offset + limit).ToString() : "" } };
        foreach (var id in ids.Take(limit)) res.Contacts.Add(await BuildLinkAsync(con, a.UserId, id, ct));
        return res;
    }

    // ---------------- Блокировки ----------------

    public override async Task<Empty> BlockUser(BlockUserRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var target = CallExtensions.ParseId(r.UserId);
        if (target == a.UserId) throw NexoException.Invalid(ErrorReason.PermissionDenied, "self");
        await using var con = await ds.OpenAsync(context.CancellationToken);
        await con.ExecAsync("insert into user_blocks(blocker_id, blocked_id) values (@u, @t) on conflict do nothing", new { u = a.UserId, t = target }, ct: context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> UnblockUser(BlockUserRequest r, ServerCallContext context)
    {
        var a = context.Auth();
        await using var con = await ds.OpenAsync(context.CancellationToken);
        await con.ExecAsync("delete from user_blocks where blocker_id = @u and blocked_id = @t", new { u = a.UserId, t = CallExtensions.ParseId(r.UserId) }, ct: context.CancellationToken);
        return new Empty();
    }

    public override async Task<ListBlockedUsersResponse> ListBlockedUsers(PageRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var limit = Math.Clamp(r?.Limit ?? 100, 1, 100);
        var offset = int.TryParse(r?.Cursor, out var o) ? Math.Max(o, 0) : 0;
        await using var con = await ds.OpenAsync(ct);
        var ids = await con.ListAsync<Guid>("select blocked_id from user_blocks where blocker_id = @u order by created_at desc limit @l offset @o",
            new { u = a.UserId, l = limit + 1, o = offset }, ct: ct);
        var page = ids.Take(limit).ToList();
        var profiles = await users.ProfilesAsync(con, a.UserId, page, ct);
        var res = new ListBlockedUsersResponse { Page = new PageInfo { HasMore = ids.Count > limit, NextCursor = ids.Count > limit ? (offset + limit).ToString() : "" } };
        foreach (var id in page) if (profiles.TryGetValue(id, out var p)) res.Users.Add(p);
        return res;
    }
}
