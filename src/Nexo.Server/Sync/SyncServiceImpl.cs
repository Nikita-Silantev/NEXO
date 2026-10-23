using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Npgsql;

namespace Nexo.Server.Sync;

/// <summary>
/// Синхронизация (ФТ-СИН-01..05). Подписка: сперва подключаемся к хабу, затем читаем журнал после last_update_seq,
/// затем переходим к потоку; повторы по update_seq отбрасываются, пропуски дочитываются из журнала.
/// </summary>
public sealed class SyncServiceImpl(NpgsqlDataSource ds, UpdateHub hub) : SyncService.SyncServiceBase
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);
    private const int BatchSize = 500;

    private async Task<(long Current, long Oldest)> StateAsync(Guid userId, CancellationToken ct)
    {
        await using var con = await ds.OpenAsync(ct);
        var cur = await con.ScalarAsync<long>("select coalesce((select last_seq from user_update_counters where user_id = @u), 0)", new { u = userId }, ct: ct);
        var oldest = await con.ScalarAsync<long>("select coalesce(min(update_seq), 0) from user_updates where user_id = @u", new { u = userId }, ct: ct);
        return (cur, oldest);
    }

    private async Task<List<Update>> ReadAsync(Guid userId, long after, int limit, CancellationToken ct)
    {
        await using var con = await ds.OpenAsync(ct);
        var rows = await con.ListAsync<byte[]>(
            "select payload from user_updates where user_id = @u and update_seq > @a order by update_seq limit @l",
            new { u = userId, a = after, l = limit }, ct: ct);
        return rows.Select(Update.Parser.ParseFrom).ToList();
    }

    public override async Task<GetStateResponse> GetState(Empty request, ServerCallContext context)
    {
        var a = context.Auth();
        var (cur, oldest) = await StateAsync(a.UserId, context.CancellationToken);
        return new GetStateResponse { CurrentUpdateSeq = cur, OldestAvailableUpdateSeq = oldest, ServerTime = Timestamp.FromDateTime(DateTime.UtcNow) };
    }

    public override async Task<GetDifferenceResponse> GetDifference(GetDifferenceRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var limit = Math.Clamp(r.Limit == 0 ? BatchSize : r.Limit, 1, BatchSize);
        var (cur, oldest) = await StateAsync(a.UserId, ct);
        var res = new GetDifferenceResponse { ToUpdateSeq = r.FromUpdateSeq };
        if (r.FromUpdateSeq >= cur) return res;
        if (oldest > 0 && oldest > r.FromUpdateSeq + 1) { res.ResyncRequired = true; res.ToUpdateSeq = cur; return res; }
        var list = await ReadAsync(a.UserId, r.FromUpdateSeq, limit + 1, ct);
        res.HasMore = list.Count > limit;
        foreach (var u in list.Take(limit)) { res.Updates.Add(u); res.ToUpdateSeq = u.UpdateSeq; }
        return res;
    }

    public override async Task<Empty> AckUpdates(AckUpdatesRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        await con.ExecAsync("update devices set last_seen_at = now() where id = @d and user_id = @u",
            new { d = a.DeviceId, u = a.UserId }, ct: ct);
        return new Empty();
    }

    public override async Task Subscribe(SubscribeRequest r, IServerStreamWriter<SubscribeEvent> stream, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        using var sub = hub.Subscribe(a.UserId);   // подписка раньше чтения журнала: событие между ними не потеряется
        var last = Math.Max(r.LastUpdateSeq, 0);

        var (cur, oldest) = await StateAsync(a.UserId, ct);
        if (last > cur) last = cur;                                                // клиент «из будущего» (восстановление БД): начать с текущей позиции
        if (oldest > 0 && oldest > last + 1 && last < cur)
        {
            await stream.WriteAsync(new SubscribeEvent { ResyncRequired = new ResyncRequired { Reason = "journal_trimmed", OldestAvailableUpdateSeq = oldest } }, ct);
            last = cur;
        }

        last = await DrainJournalAsync(a.UserId, last, stream, ct);

        var beat = new PeriodicTimer(HeartbeatEvery);
        var beatTask = beat.WaitForNextTickAsync(ct).AsTask();
        var readTask = sub.Reader.WaitToReadAsync(ct).AsTask();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var done = await Task.WhenAny(beatTask, readTask);
                if (done == beatTask)
                {
                    if (!await beatTask) break;
                    await stream.WriteAsync(new SubscribeEvent { Heartbeat = new Heartbeat { ServerTime = Timestamp.FromDateTime(DateTime.UtcNow) } }, ct);
                    beatTask = beat.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }
                if (!await readTask) break;
                while (sub.Reader.TryRead(out var u))
                {
                    if (u.UpdateSeq == 0) { await stream.WriteAsync(new SubscribeEvent { Update = u }, ct); continue; }
                    if (u.UpdateSeq <= last) continue;                              // уже отдано из журнала
                    if (u.UpdateSeq != last + 1) { last = await DrainJournalAsync(a.UserId, last, stream, ct); if (u.UpdateSeq <= last) continue; }
                    await stream.WriteAsync(new SubscribeEvent { Update = u }, ct);
                    last = u.UpdateSeq;
                }
                readTask = sub.Reader.WaitToReadAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException) { /* клиент отключился */ }
        finally { beat.Dispose(); }
    }

    private async Task<long> DrainJournalAsync(Guid userId, long last, IServerStreamWriter<SubscribeEvent> stream, CancellationToken ct)
    {
        while (true)
        {
            var batch = await ReadAsync(userId, last, BatchSize, ct);
            foreach (var u in batch) { await stream.WriteAsync(new SubscribeEvent { Update = u }, ct); last = u.UpdateSeq; }
            if (batch.Count < BatchSize) return last;
        }
    }
}
