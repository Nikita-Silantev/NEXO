using System.Collections.Concurrent;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Grpc.Core;

namespace Nexo.Server.Auth;

/// <summary>
/// Выдача и проверка вычислительных задач. Задача одноразовая и живёт 2 минуты.
/// Хранится в памяти процесса (не критично при перезапуске: клиент запросит новую).
/// </summary>
public sealed class PowService(IRateLimiter limiter)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private sealed record Entry(byte[] Challenge, int Bits, GetPowChallengeRequest.Types.Purpose Purpose, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Entry> _store = new();

    public GetPowChallengeResponse Issue(GetPowChallengeRequest.Types.Purpose purpose, string ip)
    {
        if (purpose == GetPowChallengeRequest.Types.Purpose.Unspecified)
            throw NexoException.Invalid(ErrorReason.PowInvalid, "purpose");
        if (!limiter.TryAcquire("pow:" + ip, 120, TimeSpan.FromHours(1), out var ra)) throw NexoException.RateLimited(ra);
        var recent = limiter.Count("pow:" + ip, TimeSpan.FromHours(1));
        var bits = Pow.DifficultyFor(recent);
        var id = Guid.NewGuid().ToString("N");
        var challenge = Pow.NewChallenge();
        var exp = DateTimeOffset.UtcNow + Lifetime;
        _store[id] = new Entry(challenge, bits, purpose, exp);
        return new GetPowChallengeResponse
        {
            ChallengeId = id, Challenge = Google.Protobuf.ByteString.CopyFrom(challenge), DifficultyBits = (uint)bits,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(exp),
        };
    }

    /// <summary>Проверяет и погашает решение. Бросает POW_INVALID при любой неудаче.</summary>
    public void Consume(PowSolution? solution, GetPowChallengeRequest.Types.Purpose purpose)
    {
        if (solution is null || string.IsNullOrEmpty(solution.ChallengeId))
            throw new NexoException(ErrorReason.PowRequired, StatusCode.FailedPrecondition);
        if (!_store.TryRemove(solution.ChallengeId, out var e) || e.Expires < DateTimeOffset.UtcNow || e.Purpose != purpose
            || !Pow.Verify(e.Challenge, solution.Nonce.Span, e.Bits))
            throw new NexoException(ErrorReason.PowInvalid, StatusCode.FailedPrecondition);
    }

    public void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in _store) if (v.Expires < now) _store.TryRemove(k, out _);
    }
}
