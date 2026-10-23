using Grpc.Core;
using Grpc.Core.Interceptors;
using Nexo.Protocol.V1;
using Nexo.Server.Security;

namespace Nexo.Server.Infrastructure;

/// <summary>
/// Проверка токена доступа для всех методов, кроме перечисленных как анонимные.
/// Ошибки: TOKEN_EXPIRED (клиент обновляет токен), TOKEN_REVOKED (клиент выходит из учётной записи).
/// </summary>
public sealed class AuthInterceptor(AccessTokenService tokens, SessionGuard guard) : Interceptor
{
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        "/nexo.v1.AuthService/GetPowChallenge", "/nexo.v1.AuthService/CheckUsername", "/nexo.v1.AuthService/GetKdfParams",
        "/nexo.v1.AuthService/Register", "/nexo.v1.AuthService/Login", "/nexo.v1.AuthService/CompleteSecondFactor",
        "/nexo.v1.AuthService/WatchLoginApproval", "/nexo.v1.AuthService/RefreshToken", "/nexo.v1.AuthService/RecoverAccount",
        "/nexo.v1.LegalService/GetLegalDocuments",
    };

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        await continuation(request, responseStream, context);
    }

    private async Task AuthenticateAsync(ServerCallContext context)
    {
        if (Anonymous.Contains(context.Method)) return;
        var header = context.RequestHeaders.GetValue("authorization");
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw NexoException.Unauthenticated(ErrorReason.TokenExpired);
        switch (tokens.Validate(header[7..].Trim(), out var claims))
        {
            case AccessTokenService.Result.Expired: throw NexoException.Unauthenticated(ErrorReason.TokenExpired);
            case AccessTokenService.Result.Invalid: throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);
        }
        if (!await guard.IsActiveAsync(claims!.SessionId, context.CancellationToken))
            throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);
        context.SetAuth(new AuthContext(claims.UserId, claims.DeviceId, claims.SessionId));
    }
}
