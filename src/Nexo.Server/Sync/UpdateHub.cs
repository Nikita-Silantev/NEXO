using System.Collections.Concurrent;
using System.Threading.Channels;
using Nexo.Protocol.V1;

namespace Nexo.Server.Sync;

/// <summary>
/// Рассылка обновлений подключённым устройствам внутри процесса. Хранения не даёт: долговечные обновления
/// лежат в журнале user_updates, эфемерные (update_seq = 0) существуют только пока подписчик подключён.
/// При масштабировании на несколько экземпляров заменяется реализацией на Redis Pub/Sub с тем же интерфейсом.
/// </summary>
public sealed class UpdateHub
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<Update>>> _subs = new();

    public sealed class Subscription(UpdateHub hub, Guid userId, Guid id, Channel<Update> channel) : IDisposable
    {
        public ChannelReader<Update> Reader => channel.Reader;
        public void Dispose() => hub.Remove(userId, id);
    }

    public Subscription Subscribe(Guid userId)
    {
        var id = Guid.NewGuid();
        // При переполнении теряются старые события; пробел в update_seq клиентский поток закрывает чтением журнала.
        var ch = Channel.CreateBounded<Update>(new BoundedChannelOptions(2000)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _subs.GetOrAdd(userId, _ => new()).TryAdd(id, ch);
        return new Subscription(this, userId, id, ch);
    }

    internal void Remove(Guid userId, Guid id)
    {
        if (_subs.TryGetValue(userId, out var d)) { d.TryRemove(id, out _); }
    }

    public void Publish(Guid userId, Update update)
    {
        if (!_subs.TryGetValue(userId, out var d)) return;
        foreach (var ch in d.Values) ch.Writer.TryWrite(update);
    }

    public void PublishAll(IEnumerable<(Guid UserId, Update Update)> items)
    {
        foreach (var (u, up) in items) Publish(u, up);
    }
}
