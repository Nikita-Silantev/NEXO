using Nexo.Protocol.V1;
using Nexo.Server.Auth;
using Nexo.Server.Infrastructure;
using Nexo.Server.Sync;
using Npgsql;

namespace Nexo.Server.Jobs;

/// <summary>Фоновые задачи: автоудаление сообщений по таймеру, очистка журнала (30 суток), просроченных запросов входа и сессий.</summary>
public sealed class MaintenanceService(NpgsqlDataSource ds, UpdateJournal journal, PowService pow, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var lastHeavy = DateTime.MinValue;
        while (await SafeWait(timer, stop))
        {
            try
            {
                await ExpireMessagesAsync(stop);
                pow.PurgeExpired();
                if (DateTime.UtcNow - lastHeavy > TimeSpan.FromHours(1)) { await PurgeAsync(stop); lastHeavy = DateTime.UtcNow; }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { log.LogError(e, "Сбой фоновой задачи"); }
        }
    }

    private static async Task<bool> SafeWait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    /// <summary>Удаляет сообщения, у которых истёк срок после прочтения (ФТ-СООБ-06).</summary>
    public async Task ExpireMessagesAsync(CancellationToken ct)
    {
        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var rows = await con.ListAsync<(Guid Id, Guid ChatId, long Seq)>(
            @"update messages set body = null, entities = null, deleted_at = now()
              where id in (select id from messages where expires_at < now() and deleted_at is null order by expires_at limit 500 for update skip locked)
              returning id, chat_id, seq", null, tx, ct);
        if (rows.Count == 0) { await tx.RollbackAsync(ct); return; }
        var pending = new List<(Guid, Update)>();
        foreach (var g in rows.GroupBy(r => r.ChatId))
        {
            var upd = new MessageDeletedUpdate { ChatId = g.Key.ToString(), Expired = true };
            foreach (var r in g) { upd.MessageIds.Add(r.Id.ToString()); upd.Seqs.Add(r.Seq); }
            var members = await con.ListAsync<Guid>("select user_id from chat_members where chat_id = @c and left_at is null order by user_id", new { c = g.Key }, tx, ct);
            foreach (var u in members) pending.Add(await journal.AppendAsync(con, tx, u, up => up.MessageDeleted = upd, ct));
        }
        await tx.CommitAsync(ct);
        journal.Publish(pending);
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        await using var con = await ds.OpenAsync(ct);
        var j = await con.ExecAsync("delete from user_updates where created_at < now() - interval '30 days'", ct: ct);
        var a = await con.ExecAsync("update login_approvals set status = 'EXPIRED'::approval_status, resolved_at = now() where status = 'PENDING' and expires_at < now()", ct: ct);
        var s = await con.ExecAsync("delete from sessions where expires_at < now() - interval '7 days' or revoked_at < now() - interval '30 days'", ct: ct);
        if (j + a + s > 0) log.LogInformation("Очистка: журнал {J}, запросы входа {A}, сессии {S}", j, a, s);
    }
}
