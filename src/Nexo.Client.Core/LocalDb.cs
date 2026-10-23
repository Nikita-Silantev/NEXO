using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

/// <summary>Локальная БД клиента на SQLCipher (ключ LDK). Один процесс – одно соединение, доступ сериализован блокировкой.</summary>
public sealed class LocalDb : IDisposable
{
    private readonly SqliteConnection _con;
    private readonly object _gate = new();
    private static bool _init;

    public LocalDb(string path, byte[] ldk)
    {
        if (!_init) { SQLitePCL.Batteries_V2.Init(); _init = true; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _con = new SqliteConnection($"Data Source={path}");
        _con.Open();
        Exec($"PRAGMA key = \"x'{Convert.ToHexString(ldk)}'\";");
        Exec("PRAGMA journal_mode = WAL;");
        try { Scalar("select count(*) from sqlite_master"); }
        catch (SqliteException) { _con.Dispose(); throw new InvalidDataException("Не удалось открыть локальную БД: неверный ключ."); }
        Migrate();
    }

    private void Migrate()
    {
        Exec(@"create table if not exists kv(k text primary key, v blob not null);
               create table if not exists chats(chat_id text primary key, data blob not null, sort_key integer not null, peer_read_seq integer not null default 0);
               create index if not exists ix_chats_sort on chats(sort_key desc);
               create table if not exists messages(chat_id text not null, seq integer not null, message_id text not null, data blob not null, primary key(chat_id, seq));
               create index if not exists ix_messages_id on messages(message_id);
               create table if not exists outbox(client_msg_id text primary key, chat_id text not null, body text not null, reply_to_id text not null default '',
                    created_at integer not null, state integer not null default 0, error text, ord integer not null);
               create index if not exists ix_outbox_ord on outbox(ord);");
    }

    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = _con.CreateCommand(); c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] ps)
    {
        using var c = _con.CreateCommand(); c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = c.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var c = _con.CreateCommand(); c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = c.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    // ---- kv ----
    public void SetBytes(string key, byte[] value) { lock (_gate) Exec("insert into kv(k,v) values($k,$v) on conflict(k) do update set v=$v", ("$k", key), ("$v", value)); }
    public byte[]? GetBytes(string key) { lock (_gate) return Scalar("select v from kv where k=$k", ("$k", key)) as byte[]; }
    public void Delete(string key) { lock (_gate) Exec("delete from kv where k=$k", ("$k", key)); }
    public long GetLong(string key, long def = 0) { var b = GetBytes(key); return b is { Length: 8 } ? BitConverter.ToInt64(b) : def; }
    public void SetLong(string key, long v) => SetBytes(key, BitConverter.GetBytes(v));

    // ---- чаты ----
    private static long SortKey(Chat c)
    {
        var ts = c.LastMessage?.CreatedAt ?? c.CreatedAt;
        var t = ts is null ? 0 : ts.ToDateTime().Ticks / TimeSpan.TicksPerMillisecond;
        return c.Me?.PinnedPosition > 0 ? long.MaxValue - c.Me.PinnedPosition : t;
    }

    public void UpsertChat(Chat c)
    {
        lock (_gate)
            Exec("insert into chats(chat_id,data,sort_key) values($i,$d,$s) on conflict(chat_id) do update set data=$d, sort_key=$s",
                ("$i", c.ChatId), ("$d", c.ToByteArray()), ("$s", SortKey(c)));
    }

    public void RemoveChat(string chatId)
    {
        lock (_gate)
        {
            Exec("delete from chats where chat_id=$i", ("$i", chatId));
            Exec("delete from messages where chat_id=$i", ("$i", chatId));
        }
    }

    public Chat? GetChat(string chatId)
    {
        lock (_gate) return Scalar("select data from chats where chat_id=$i", ("$i", chatId)) is byte[] b ? Chat.Parser.ParseFrom(b) : null;
    }

    public List<Chat> ListChats(bool archived)
    {
        lock (_gate)
            return Query("select data from chats order by sort_key desc", r => Chat.Parser.ParseFrom((byte[])r[0]))
                .Where(c => (c.Me?.Archived ?? false) == archived).ToList();
    }

    public long GetPeerRead(string chatId) { lock (_gate) return Convert.ToInt64(Scalar("select peer_read_seq from chats where chat_id=$i", ("$i", chatId)) ?? 0L); }
    public void SetPeerRead(string chatId, long seq) { lock (_gate) Exec("update chats set peer_read_seq = max(peer_read_seq,$s) where chat_id=$i", ("$i", chatId), ("$s", seq)); }

    // ---- сообщения ----
    public void UpsertMessage(Message m)
    {
        lock (_gate)
            Exec("insert into messages(chat_id,seq,message_id,data) values($c,$s,$m,$d) on conflict(chat_id,seq) do update set message_id=$m, data=$d",
                ("$c", m.ChatId), ("$s", m.Seq), ("$m", m.MessageId), ("$d", m.ToByteArray()));
    }

    public Message? GetMessageById(string messageId)
    {
        lock (_gate) return Scalar("select data from messages where message_id=$m", ("$m", messageId)) is byte[] b ? Message.Parser.ParseFrom(b) : null;
    }

    public void RemoveMessage(string messageId) { lock (_gate) Exec("delete from messages where message_id=$m", ("$m", messageId)); }

    /// <summary>Последние сообщения чата (по возрастанию seq); beforeSeq = 0 – с конца.</summary>
    public List<Message> GetMessages(string chatId, long beforeSeq, int limit)
    {
        lock (_gate)
        {
            var list = Query("select data from messages where chat_id=$c and ($b = 0 or seq < $b) order by seq desc limit $l",
                r => Message.Parser.ParseFrom((byte[])r[0]), ("$c", chatId), ("$b", beforeSeq), ("$l", limit));
            list.Reverse();
            return list;
        }
    }

    public long? MinSeq(string chatId)
    {
        lock (_gate) { var v = Scalar("select min(seq) from messages where chat_id=$c", ("$c", chatId)); return v is null ? null : Convert.ToInt64(v); }
    }

    // ---- очередь отправки ----
    public sealed record OutboxItem(string ClientMsgId, string ChatId, string Body, string ReplyToId, DateTime CreatedAt, int State, string? Error);

    public void Enqueue(OutboxItem i)
    {
        lock (_gate)
            Exec("insert or ignore into outbox(client_msg_id,chat_id,body,reply_to_id,created_at,state,ord) values($c,$ch,$b,$r,$t,0,(select coalesce(max(ord),0)+1 from outbox))",
                ("$c", i.ClientMsgId), ("$ch", i.ChatId), ("$b", i.Body), ("$r", i.ReplyToId), ("$t", new DateTimeOffset(i.CreatedAt).ToUnixTimeMilliseconds()));
    }

    private static OutboxItem ReadOutbox(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime, r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6));

    private const string OutboxCols = "client_msg_id,chat_id,body,reply_to_id,created_at,state,error";

    public List<OutboxItem> Outbox(string? chatId = null)
    {
        lock (_gate)
            return Query($"select {OutboxCols} from outbox where ($c is null or chat_id=$c) order by ord", ReadOutbox, ("$c", chatId));
    }

    public void SetOutboxState(string clientMsgId, int state, string? error = null)
    {
        lock (_gate) Exec("update outbox set state=$s, error=$e where client_msg_id=$c", ("$s", state), ("$e", error), ("$c", clientMsgId));
    }

    public void RemoveOutbox(string clientMsgId) { lock (_gate) Exec("delete from outbox where client_msg_id=$c", ("$c", clientMsgId)); }

    /// <summary>Полная очистка данных синхронизации (повторная синхронизация); очередь отправки сохраняется.</summary>
    public void ClearSyncData()
    {
        lock (_gate) { Exec("delete from chats"); Exec("delete from messages"); Exec("delete from kv where k = 'last_update_seq'"); }
    }

    public void Dispose() { lock (_gate) _con.Dispose(); }
}
