using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Nexo.Server.Infrastructure;

namespace Nexo.Server.Security;

/// <summary>Проверяет, что сессия и устройство не отозваны. Результат кэшируется на 10 секунд.</summary>
public sealed class SessionGuard(NpgsqlDataSource ds, IMemoryCache cache)
{
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct)
    {
        if (cache.TryGetValue(sessionId, out bool v)) return v;
        await using var con = await ds.OpenAsync(ct);
        var ok = await con.ScalarAsync<bool>(
            @"select exists(select 1 from sessions s join devices d on d.id = s.device_id
                            join users u on u.id = s.user_id
                            where s.id = @sid and s.revoked_at is null and d.revoked_at is null and u.state = 'ACTIVE')",
            new { sid = sessionId }, ct: ct);
        cache.Set(sessionId, ok, TimeSpan.FromSeconds(10));
        return ok;
    }

    public void Forget(Guid sessionId) => cache.Remove(sessionId);
}
