using System.Data.Common;
using Nexo.Server.Infrastructure;

namespace Nexo.Server.Auth;

public static class AuthEvents
{
    /// <summary>Записывает событие безопасности (auth_events). Значения kind – имена перечисления auth_event_kind.</summary>
    public static Task AddAsync(DbConnection con, Guid userId, Guid? deviceId, string kind, string? ip, string? ua,
        DbTransaction? tx = null, CancellationToken ct = default)
        => con.ExecAsync(
            "insert into auth_events(user_id, device_id, kind, ip, user_agent) values (@u, @d, @k::auth_event_kind, @ip::inet, @ua)",
            new { u = userId, d = deviceId, k = kind, ip = string.IsNullOrEmpty(ip) ? null : ip, ua }, tx, ct);
}
