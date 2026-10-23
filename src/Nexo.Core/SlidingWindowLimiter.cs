using System.Collections.Concurrent;

namespace Nexo.Core;

/// <summary>
/// Ограничитель частоты со скользящим окном. Хранится в памяти процесса; при масштабировании
/// на несколько экземпляров заменяется реализацией на Redis с тем же интерфейсом.
/// </summary>
public interface IRateLimiter
{
    /// <summary>Регистрирует попытку. Возвращает false, если лимит превышен, и задержку до повтора.</summary>
    bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter);
    int Count(string key, TimeSpan window);
    void Reset(string key);
}

public sealed class SlidingWindowLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    private readonly Func<DateTimeOffset> _now;

    public SlidingWindowLimiter(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            var now = _now();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= limit)
            {
                retryAfter = window - (now - q.Peek());
                return false;
            }
            q.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public int Count(string key, TimeSpan window)
    {
        if (!_hits.TryGetValue(key, out var q)) return 0;
        lock (q)
        {
            var now = _now();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            return q.Count;
        }
    }

    public void Reset(string key) => _hits.TryRemove(key, out _);
}
