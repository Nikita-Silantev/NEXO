using System.Data.Common;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;

namespace Nexo.Server.Sync;

/// <summary>
/// Журнал долговечных обновлений пользователя (user_updates). Номера update_seq выдаются строго по возрастанию
/// в пределах пользователя через строку-счётчик user_update_counters (блокировка строки сериализует записи).
/// Журнал хранится не менее 30 суток (ФТ-СИН-04).
/// </summary>
public sealed class UpdateJournal(UpdateHub hub)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Добавляет обновление в журнал внутри транзакции. Рассылку выполнить после фиксации: <see cref="Publish"/>.</summary>
    public async Task<(Guid UserId, Update Update)> AppendAsync(
        DbConnection con, DbTransaction tx, Guid userId, Action<Update> fill, CancellationToken ct)
    {
        var u = new Update { CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) };
        fill(u);
        var kind = KindOf(u);
        var seq = await con.ScalarAsync<long>(
            "update user_update_counters set last_seq = last_seq + 1 where user_id = @u returning last_seq",
            new { u = userId }, tx, ct);
        if (seq == 0)   // строки счётчика ещё нет (старые пользователи)
        {
            await con.ExecAsync("insert into user_update_counters(user_id, last_seq) values (@u, 0) on conflict do nothing", new { u = userId }, tx, ct);
            seq = await con.ScalarAsync<long>(
                "update user_update_counters set last_seq = last_seq + 1 where user_id = @u returning last_seq",
                new { u = userId }, tx, ct);
        }
        u.UpdateSeq = seq;
        await con.ExecAsync(
            "insert into user_updates(user_id, update_seq, kind, payload) values (@u, @s, @k::update_kind, @p)",
            new { u = userId, s = seq, k = kind, p = u.ToByteArray() }, tx, ct);
        return (userId, u);
    }

    public void Publish(IEnumerable<(Guid UserId, Update Update)> items) => hub.PublishAll(items);

    /// <summary>Эфемерное обновление (update_seq = 0): не хранится.</summary>
    public void PublishEphemeral(Guid userId, Action<Update> fill)
    {
        var u = new Update { UpdateSeq = 0, CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow) };
        fill(u);
        hub.Publish(userId, u);
    }

    private static string KindOf(Update u) => u.PayloadCase switch
    {
        Update.PayloadOneofCase.NewMessage => "MESSAGE_NEW",
        Update.PayloadOneofCase.MessageEdited => "MESSAGE_EDITED",
        Update.PayloadOneofCase.MessageDeleted => "MESSAGE_DELETED",
        Update.PayloadOneofCase.ReadState => "READ_STATE",
        Update.PayloadOneofCase.Chat => "CHAT_UPDATED",
        Update.PayloadOneofCase.Member => "MEMBER_UPDATED",
        Update.PayloadOneofCase.Folder => "FOLDER_UPDATED",
        Update.PayloadOneofCase.Draft => "DRAFT_UPDATED",
        Update.PayloadOneofCase.Settings => "SETTINGS_UPDATED",
        Update.PayloadOneofCase.Device => "DEVICE_UPDATED",
        Update.PayloadOneofCase.FileReady => "FILE_READY",
        Update.PayloadOneofCase.Contact => "CONTACT_UPDATED",
        Update.PayloadOneofCase.Reaction => "REACTION_UPDATED",
        _ => throw new InvalidOperationException("Обновление не является долговечным: " + u.PayloadCase),
    };
}
