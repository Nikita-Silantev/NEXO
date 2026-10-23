using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Security;
using Nexo.Server.Sync;
using Npgsql;

namespace Nexo.Server.Auth;

/// <summary>Устройства, история входов и подтверждение входа с нового устройства (ФТ-УСТ-02..04).</summary>
public sealed class DeviceServiceImpl(NpgsqlDataSource ds, SessionService sessions, UpdateJournal journal, SecretBox box, SessionGuard guard)
    : DeviceService.DeviceServiceBase
{
    private sealed class DeviceRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Platform { get; set; } = "";
        public string? AppVersion { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public string? LastIp { get; set; }
    }

    internal static Timestamp Ts(DateTime d) => Timestamp.FromDateTime(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    public static LoginApproval BuildApproval(Guid id, string name, DevicePlatform platform, string? ip, string code,
        ApprovalStatus status, DateTime created, DateTime expires, byte[] pubkey) => new()
    {
        ApprovalId = id.ToString(), RequesterName = name, RequesterPlatform = platform, RequesterIpMasked = IpMask.Mask(ip),
        Code = code, Status = status, CreatedAt = Ts(created), ExpiresAt = Ts(expires), RequesterPubkey = ByteString.CopyFrom(pubkey),
    };

    public override async Task<ListDevicesResponse> ListDevices(Empty request, ServerCallContext context)
    {
        var a = context.Auth();
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var rows = await con.ListAsync<DeviceRow>(
            @"select id, name, platform::text as platform, app_version, created_at, last_seen_at, last_ip::text as last_ip
              from devices where user_id = @u and revoked_at is null order by created_at", new { u = a.UserId }, ct: context.CancellationToken);
        var res = new ListDevicesResponse();
        foreach (var d in rows)
            res.Devices.Add(new DeviceSummary
            {
                DeviceId = d.Id.ToString(), Name = d.Name, Platform = SessionService.PlatformFromText(d.Platform), AppVersion = d.AppVersion ?? "",
                CreatedAt = Ts(d.CreatedAt), LastSeenAt = d.LastSeenAt is { } l ? Ts(l) : null, LastIpMasked = IpMask.Mask(d.LastIp), IsCurrent = d.Id == a.DeviceId,
            });
        return res;
    }

    public override async Task<Empty> RenameDevice(RenameDeviceRequest r, ServerCallContext context)
    {
        var a = context.Auth();
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 80) throw NexoException.Invalid(ErrorReason.DeviceNotFound, "name");
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var n = await con.ExecAsync("update devices set name = @n where id = @d and user_id = @u and revoked_at is null",
            new { n = name, d = CallExtensions.ParseId(r.DeviceId), u = a.UserId }, ct: context.CancellationToken);
        if (n == 0) throw new NexoException(ErrorReason.DeviceNotFound, StatusCode.NotFound);
        return new Empty();
    }

    public override async Task<Empty> RevokeDevice(RevokeDeviceRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var deviceId = CallExtensions.ParseId(r.DeviceId);
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var n = await con.ExecAsync("update devices set revoked_at = now() where id = @d and user_id = @u and revoked_at is null",
            new { d = deviceId, u = a.UserId }, tx, ct);
        if (n == 0) throw new NexoException(ErrorReason.DeviceNotFound, StatusCode.NotFound);
        await sessions.RevokeDeviceSessionsAsync(con, tx, deviceId, "DEVICE_REVOKED", ct);
        await AuthEvents.AddAsync(con, a.UserId, deviceId, "DEVICE_REVOKED", context.ClientIp(), context.UserAgent(), tx, ct);
        var upd = await journal.AppendAsync(con, tx, a.UserId, u => u.Device = new DeviceUpdate
        {
            Event = DeviceUpdate.Types.Event.Revoked, Device = new DeviceSummary { DeviceId = deviceId.ToString() },
        }, ct);
        await tx.CommitAsync(ct);
        journal.Publish([upd]);
        return new Empty();
    }

    public override async Task<Empty> RegisterPushToken(RegisterPushTokenRequest r, ServerCallContext context)
    {
        var a = context.Auth();
        if (r.Token.Length > 4096) throw NexoException.Invalid(ErrorReason.DeviceNotFound, "token");
        var provider = r.Provider switch
        {
            PushProvider.Fcm => "FCM", PushProvider.Rustore => "RUSTORE", PushProvider.Apns => "APNS", PushProvider.Webpush => "WEBPUSH", _ => "NONE",
        };
        await using var con = await ds.OpenAsync(context.CancellationToken);
        await con.ExecAsync("update devices set push_provider = @p::push_provider, push_token_enc = @t where id = @d",
            new { p = provider, t = provider == "NONE" ? null : box.Seal(System.Text.Encoding.UTF8.GetBytes(r.Token)), d = a.DeviceId },
            ct: context.CancellationToken);
        return new Empty();
    }

    public override async Task<ListAuthEventsResponse> ListAuthEvents(ListAuthEventsRequest r, ServerCallContext context)
    {
        var a = context.Auth();
        var limit = Math.Clamp(r.Page?.Limit ?? 50, 1, 100);
        long cursor = long.TryParse(r.Page?.Cursor, out var c) ? c : long.MaxValue;
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var rows = await con.ListAsync<AuthEventRow>(
            @"select id, kind::text as kind, device_id, ip::text as ip, user_agent, created_at from auth_events
              where user_id = @u and id < @c order by id desc limit @l",
            new { u = a.UserId, c = cursor, l = limit + 1 }, ct: context.CancellationToken);
        var res = new ListAuthEventsResponse();
        foreach (var e in rows.Take(limit))
            res.Events.Add(new AuthEvent
            {
                Id = e.Id, Kind = ParseKind(e.Kind), DeviceId = e.DeviceId?.ToString() ?? "", IpMasked = IpMask.Mask(e.Ip),
                UserAgent = e.UserAgent ?? "", CreatedAt = Ts(e.CreatedAt),
            });
        res.Page = new PageInfo { HasMore = rows.Count > limit, NextCursor = rows.Count > limit ? rows[limit - 1].Id.ToString() : "" };
        return res;
    }

    private sealed class AuthEventRow
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "";
        public Guid? DeviceId { get; set; }
        public string? Ip { get; set; }
        public string? UserAgent { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private static AuthEventKind ParseKind(string s) =>
        System.Enum.TryParse<AuthEventKind>(s.Replace("_", ""), true, out var k) ? k : AuthEventKind.Unspecified;

    private sealed class ApprovalRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public byte[] RequesterPubkey { get; set; } = [];
        public string RequesterName { get; set; } = "";
        public string RequesterPlatform { get; set; } = "";
        public string? RequesterIp { get; set; }
        public string Code { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private const string ApprovalSelect =
        @"select id, user_id, requester_pubkey, requester_name, requester_platform::text as requester_platform, requester_ip::text as requester_ip,
                 code, status::text as status, created_at, expires_at from login_approvals";

    public override async Task<ListLoginApprovalsResponse> ListLoginApprovals(Empty request, ServerCallContext context)
    {
        var a = context.Auth();
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var rows = await con.ListAsync<ApprovalRow>(
            ApprovalSelect + " where user_id = @u and status = 'PENDING' and expires_at > now() order by created_at desc",
            new { u = a.UserId }, ct: context.CancellationToken);
        var res = new ListLoginApprovalsResponse();
        foreach (var x in rows)
            res.Approvals.Add(BuildApproval(x.Id, x.RequesterName, SessionService.PlatformFromText(x.RequesterPlatform), x.RequesterIp,
                x.Code, ApprovalStatus.Pending, x.CreatedAt, x.ExpiresAt, x.RequesterPubkey));
        return res;
    }

    public override async Task<Empty> ResolveLoginApproval(ResolveLoginApprovalRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var id = CallExtensions.ParseId(r.ApprovalId);
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var x = await con.OneAsync<ApprovalRow>(ApprovalSelect + " where id = @id and user_id = @u for update", new { id, u = a.UserId }, tx, ct);
        if (x is null || x.Status != "PENDING" || x.ExpiresAt <= DateTime.UtcNow) throw NexoException.NotFound();

        if (!r.Approve)
        {
            await con.ExecAsync("update login_approvals set status = 'DENIED', approver_device_id = @d, resolved_at = now() where id = @id",
                new { id, d = a.DeviceId }, tx, ct);
            await AuthEvents.AddAsync(con, a.UserId, a.DeviceId, "LOGIN_DENIED", context.ClientIp(), context.UserAgent(), tx, ct);
            await tx.CommitAsync(ct);
            return new Empty();
        }

        // Подтверждение подписывается ключом устройства-подтвердителя (документ 81 02).
        var approverKey = await con.OneAsync<byte[]>("select device_pubkey from devices where id = @d and revoked_at is null", new { d = a.DeviceId }, tx, ct);
        var message = DeviceSignatures.ApprovalMessage(id.ToString(), x.RequesterPubkey, x.Code);
        if (approverKey is null || !AuthServiceImpl.Ed25519Ok(approverKey, message, r.Signature.Span))
            throw NexoException.Denied(ErrorReason.PermissionDenied);

        var info = new DeviceInfo
        {
            Name = x.RequesterName, Platform = SessionService.PlatformFromText(x.RequesterPlatform),
            DevicePubkey = ByteString.CopyFrom(x.RequesterPubkey),
        };
        var (deviceId, pending) = await sessions.CreateDeviceAsync(con, tx, a.UserId, info, x.RequesterIp, notify: true, ct);
        await con.ExecAsync("update login_approvals set status = 'APPROVED', approver_device_id = @d, resolved_at = now() where id = @id",
            new { id, d = a.DeviceId }, tx, ct);
        await AuthEvents.AddAsync(con, a.UserId, deviceId, "LOGIN_APPROVED", context.ClientIp(), context.UserAgent(), tx, ct);
        await tx.CommitAsync(ct);
        journal.Publish(pending);
        return new Empty();
    }
}
