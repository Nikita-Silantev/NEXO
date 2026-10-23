using Grpc.Core;
using Nexo.Protocol.V1;

namespace Nexo.Server.Infrastructure;

/// <summary>Ошибка прикладного уровня: превращается в RpcException с ErrorDetail (ИНТ-02).</summary>
public sealed class NexoException : Exception
{
    public ErrorReason Reason { get; }
    public StatusCode Code { get; }
    public int RetryAfterSeconds { get; }
    public IReadOnlyDictionary<string, string>? Meta { get; }

    public NexoException(ErrorReason reason, StatusCode code, string? message = null, int retryAfterSeconds = 0,
        IReadOnlyDictionary<string, string>? meta = null)
        : base(message ?? reason.ToString())
    {
        Reason = reason; Code = code; RetryAfterSeconds = retryAfterSeconds; Meta = meta;
    }

    public static NexoException Unauthenticated(ErrorReason r) => new(r, StatusCode.Unauthenticated);
    public static NexoException Denied(ErrorReason r = ErrorReason.PermissionDenied) => new(r, StatusCode.PermissionDenied);
    public static NexoException NotFound() => new(ErrorReason.NotFound, StatusCode.NotFound);
    public static NexoException Invalid(ErrorReason r, string? msg = null) => new(r, StatusCode.InvalidArgument, msg);
    public static NexoException RateLimited(TimeSpan retry) =>
        new(ErrorReason.RateLimited, StatusCode.ResourceExhausted, null, (int)Math.Ceiling(retry.TotalSeconds));
}
