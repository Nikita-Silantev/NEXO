using System.Data.Common;
using Google.Protobuf.WellKnownTypes;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Security;
using Nexo.Server.Sync;

namespace Nexo.Server.Auth;

public sealed record NewDevice(Guid Id, DeviceSummary Summary);

/// <summary>Создание устройств и сессий, выпуск пар токенов, ротация refresh-токенов.</summary>
public sealed class SessionService(AccessTokenService tokens, UpdateJournal journal)
{
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);   // с последнего использования

    public static string PlatformText(DevicePlatform p) => p switch
    {
        DevicePlatform.Windows => "WINDOWS", DevicePlatform.Macos => "MACOS", DevicePlatform.Linux => "LINUX",
        DevicePlatform.Android => "ANDROID", DevicePlatform.Ios => "IOS", DevicePlatform.Web => "WEB",
        _ => throw NexoException.Invalid(ErrorReason.DeviceNotFound, "platform"),
    };

    public static DevicePlatform PlatformFromText(string s) => s switch
    {
        "WINDOWS" => DevicePlatform.Windows, "MACOS" => DevicePlatform.Macos, "LINUX" => DevicePlatform.Linux,
        "ANDROID" => DevicePlatform.Android, "IOS" => DevicePlatform.Ios, "WEB" => DevicePlatform.Web,
        _ => DevicePlatform.Unspecified,
    };

    public static void ValidateDevice(DeviceInfo? d)
    {
        if (d is null || d.DevicePubkey.Length != 32 || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 80)
            throw NexoException.Invalid(ErrorReason.DeviceNotFound, "device");
        _ = PlatformText(d.Platform);
    }

    /// <summary>Создаёт устройство и добавляет обновление «устройство добавлено» остальным устройствам пользователя.</summary>
    public async Task<(Guid DeviceId, List<(Guid, Update)> Pending)> CreateDeviceAsync(
        DbConnection con, DbTransaction tx, Guid userId, DeviceInfo d, string? ip, bool notify, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await con.ExecAsync(
            @"insert into devices(id, user_id, name, platform, app_version, os_version, device_pubkey, last_seen_at, last_ip)
              values (@id, @u, @name, @pl::device_platform, @av, @ov, @pk, now(), @ip::inet)",
            new
            {
                id, u = userId, name = d.Name.Trim(), pl = PlatformText(d.Platform), av = Trunc(d.AppVersion, 32),
                ov = Trunc(d.OsVersion, 64), pk = d.DevicePubkey.ToByteArray(), ip = string.IsNullOrEmpty(ip) ? null : ip,
            }, tx, ct);
        var pending = new List<(Guid, Update)>();
        if (notify)
        {
            var summary = new DeviceSummary
            {
                DeviceId = id.ToString(), Name = d.Name.Trim(), Platform = d.Platform, AppVersion = d.AppVersion,
                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow), LastSeenAt = Timestamp.FromDateTime(DateTime.UtcNow),
                LastIpMasked = IpMask.Mask(ip),
            };
            pending.Add(await journal.AppendAsync(con, tx, userId,
                u => u.Device = new DeviceUpdate { Event = DeviceUpdate.Types.Event.Added, Device = summary }, ct));
        }
        return (id, pending);
    }

    /// <summary>Создаёт сессию и возвращает пару токенов. familyId = null – новая цепочка ротации.</summary>
    public async Task<TokenPair> IssueAsync(DbConnection con, DbTransaction? tx, Guid userId, Guid deviceId, Guid? familyId, CancellationToken ct)
    {
        var sessionId = Guid.CreateVersion7();
        var refresh = Tokens.NewOpaque();
        var refreshExp = DateTime.UtcNow + RefreshLifetime;
        await con.ExecAsync(
            "insert into sessions(id, user_id, device_id, family_id, refresh_hash, expires_at) values (@id, @u, @d, @f, @h, @e)",
            new { id = sessionId, u = userId, d = deviceId, f = familyId ?? Guid.CreateVersion7(), h = Tokens.Hash(refresh), e = refreshExp }, tx, ct);
        var (access, accessExp) = tokens.Issue(userId, deviceId, sessionId);
        return new TokenPair
        {
            AccessToken = access, AccessExpiresAt = Timestamp.FromDateTimeOffset(accessExp),
            RefreshToken = refresh, RefreshExpiresAt = Timestamp.FromDateTime(refreshExp),
            DeviceId = deviceId.ToString(), SessionId = sessionId.ToString(),
        };
    }

    /// <summary>Отзывает все сессии цепочки.</summary>
    public Task RevokeFamilyAsync(DbConnection con, DbTransaction? tx, Guid sessionId, string reason, CancellationToken ct)
        => con.ExecAsync(
            @"update sessions set revoked_at = now(), revoke_reason = @r::session_revoke_reason
              where revoked_at is null and family_id = (select family_id from sessions where id = @s)",
            new { s = sessionId, r = reason }, tx, ct);

    public Task RevokeDeviceSessionsAsync(DbConnection con, DbTransaction? tx, Guid deviceId, string reason, CancellationToken ct)
        => con.ExecAsync(
            "update sessions set revoked_at = now(), revoke_reason = @r::session_revoke_reason where device_id = @d and revoked_at is null",
            new { d = deviceId, r = reason }, tx, ct);

    public Task RevokeUserSessionsAsync(DbConnection con, DbTransaction? tx, Guid userId, Guid? exceptSession, string reason, CancellationToken ct)
        => con.ExecAsync(
            @"update sessions set revoked_at = now(), revoke_reason = @r::session_revoke_reason
              where user_id = @u and revoked_at is null and (@x::uuid is null or family_id <> (select family_id from sessions where id = @x::uuid))",
            new { u = userId, x = exceptSession, r = reason }, tx, ct);

    private static string? Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? null : (s.Length > n ? s[..n] : s);
}
