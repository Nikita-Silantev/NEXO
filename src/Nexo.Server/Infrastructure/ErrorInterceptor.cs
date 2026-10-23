using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Nexo.Protocol.V1;

namespace Nexo.Server.Infrastructure;

/// <summary>
/// Единая обработка ошибок: NexoException → код gRPC + ErrorDetail в трейлере grpc-status-details-bin.
/// Прочие исключения → Internal без подробностей (в журнал пишется только тип исключения и метод; НФТ-БЕЗ-05).
/// </summary>
public sealed class ErrorInterceptor(ILogger<ErrorInterceptor> log) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try { return await continuation(request, context); }
        catch (Exception e) when (Translate(e, context) is { } rpc) { throw rpc; }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try { await continuation(request, responseStream, context); }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { /* клиент отключился */ }
        catch (Exception e) when (Translate(e, context) is { } rpc) { throw rpc; }
    }

    private RpcException? Translate(Exception e, ServerCallContext context)
    {
        if (e is RpcException) return null;   // уже готовая ошибка
        if (e is NexoException ne)
        {
            var detail = new ErrorDetail { Reason = ne.Reason, RetryAfterSeconds = ne.RetryAfterSeconds };
            if (ne.Meta != null) foreach (var (k, v) in ne.Meta) detail.Metadata[k] = v;
            var trailers = new Metadata { { "grpc-status-details-bin", detail.ToByteArray() } };
            return new RpcException(new Status(ne.Code, ne.Reason.ToString()), trailers);
        }
        if (e is OperationCanceledException) return new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
        // Только тип и метод: сообщение исключения может содержать значения параметров.
        log.LogError("Необработанная ошибка в {Method}: {Type}", context.Method, e.GetType().Name);
        return new RpcException(new Status(StatusCode.Internal, "internal error"));
    }
}
