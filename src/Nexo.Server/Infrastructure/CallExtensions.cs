using Grpc.Core;
using Nexo.Protocol.V1;

namespace Nexo.Server.Infrastructure;

public sealed record AuthContext(Guid UserId, Guid DeviceId, Guid SessionId);

public static class CallExtensions
{
    private const string Key = "nexo.auth";

    public static void SetAuth(this ServerCallContext c, AuthContext a) => c.UserState[Key] = a;

    /// <summary>Вызывающий пользователь. Для методов без токена бросает Unauthenticated.</summary>
    public static AuthContext Auth(this ServerCallContext c) =>
        c.UserState.TryGetValue(Key, out var v) && v is AuthContext a
            ? a : throw NexoException.Unauthenticated(ErrorReason.TokenExpired);

    public static string ClientIp(this ServerCallContext c) =>
        c.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "";

    public static string UserAgent(this ServerCallContext c)
    {
        var ua = c.GetHttpContext().Request.Headers.UserAgent.ToString();
        return ua.Length > 300 ? ua[..300] : ua;
    }

    public static Guid ParseId(string? s) =>
        Guid.TryParse(s, out var g) ? g : throw NexoException.Invalid(ErrorReason.NotFound, "bad id");
}
