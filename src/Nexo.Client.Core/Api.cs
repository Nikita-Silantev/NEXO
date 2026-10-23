using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Nexo.Core;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

/// <summary>Разбор ошибок API: причина из ErrorDetail (grpc-status-details-bin).</summary>
public static class ApiError
{
    public static ErrorDetail? Detail(RpcException e)
    {
        var bytes = e.Trailers.GetValueBytes("grpc-status-details-bin");
        if (bytes is null) return null;
        try { return ErrorDetail.Parser.ParseFrom(bytes); } catch { return null; }
    }

    public static ErrorReason Reason(RpcException e) => Detail(e)?.Reason ?? ErrorReason.Unspecified;

    public static bool IsNetwork(RpcException e) =>
        e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled or StatusCode.Internal && Reason(e) == ErrorReason.Unspecified;

    /// <summary>Понятный пользователю текст.</summary>
    public static string Message(RpcException e)
    {
        var d = Detail(e);
        return (d?.Reason ?? ErrorReason.Unspecified) switch
        {
            ErrorReason.InvalidCredentials => "Неверный ник или пароль.",
            ErrorReason.AccountLocked => $"Вход временно заблокирован. Повторите через {Math.Max(1, d!.RetryAfterSeconds / 60)} мин.",
            ErrorReason.AccountSuspended => "Учётная запись приостановлена.",
            ErrorReason.UsernameTaken => "Этот ник уже занят.",
            ErrorReason.UsernameInvalid => "Ник не соответствует правилам (3–32 символа: a–z, 0–9, _; начинается с буквы).",
            ErrorReason.RateLimited => $"Слишком много запросов. Повторите через {Math.Max(1, d!.RetryAfterSeconds)} с.",
            ErrorReason.DocumentsNotAccepted => "Нужно принять юридические документы.",
            ErrorReason.NotFound => "Объект не найден.",
            ErrorReason.PermissionDenied => "Недостаточно прав.",
            ErrorReason.BlockedByPeer => "Собеседник ограничил отправку сообщений вам.",
            ErrorReason.PrivacyRestricted => "Это запрещено настройками приватности собеседника.",
            ErrorReason.MessageTooLong => "Сообщение слишком длинное (до 4096 символов).",
            ErrorReason.EditWindowExpired => "Срок редактирования (48 часов) истёк.",
            ErrorReason.ClientTooOld => "Версия приложения устарела, обновите его.",
            ErrorReason.PowInvalid or ErrorReason.PowRequired => "Не удалось пройти проверку защиты от автоматических запросов. Повторите попытку.",
            _ => e.StatusCode switch
            {
                StatusCode.Unavailable => "Нет связи с сервером.",
                StatusCode.DeadlineExceeded => "Сервер не отвечает.",
                _ => "Ошибка: " + e.Status.Detail,
            },
        };
    }
}

/// <summary>Состояние сессии устройства. Хранится в локальной БД (зашифрована LDK).</summary>
public sealed class SessionData
{
    public string UserId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Username { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset AccessExpires { get; set; }
    public string RefreshToken { get; set; } = "";
    public byte[] DevicePrivateKey { get; set; } = [];
}

/// <summary>
/// Канал gRPC и клиенты сервисов. Выдаёт токен доступа, при необходимости заранее или после ошибки TOKEN_EXPIRED
/// обновляет пару токенов (с подписью ключом устройства); обновление выполняется одним потоком.
/// </summary>
public sealed class NexoApi : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SessionData? _session;
    private readonly Action<SessionData> _persist;

    public event Action<string>? SessionEnded;   // причина: сессия отозвана на сервере

    public AuthService.AuthServiceClient Auth { get; }
    public DeviceService.DeviceServiceClient Devices { get; }
    public LegalService.LegalServiceClient Legal { get; }
    public UserService.UserServiceClient Users { get; }
    public ChatService.ChatServiceClient Chats { get; }
    public MessageService.MessageServiceClient Messages { get; }
    public SyncService.SyncServiceClient Sync { get; }

    public NexoApi(Uri server, Action<SessionData> persist)
    {
        _persist = persist;
        // Для http:// (локальная разработка без Caddy) нужен HTTP/2 без TLS – включается самим Grpc.Net.Client.
        _channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 8 * 1024 * 1024,
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            },
        });
        Auth = new(_channel); Devices = new(_channel); Legal = new(_channel); Users = new(_channel);
        Chats = new(_channel); Messages = new(_channel); Sync = new(_channel);
    }

    public SessionData? Session => _session;
    public void SetSession(SessionData? s) => _session = s;

    public static SessionData FromTokens(TokenPair t, string username, byte[] devicePrivateKey, string? userId = null) => new()
    {
        UserId = userId ?? "", DeviceId = t.DeviceId, Username = username, AccessToken = t.AccessToken,
        AccessExpires = t.AccessExpiresAt.ToDateTimeOffset(), RefreshToken = t.RefreshToken, DevicePrivateKey = devicePrivateKey,
    };

    /// <summary>Заголовки с актуальным токеном; при необходимости обновляет его заранее.</summary>
    public async Task<Metadata> HeadersAsync(CancellationToken ct = default)
    {
        var s = _session ?? throw new InvalidOperationException("Нет сессии");
        if (s.AccessExpires - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(30)) await RefreshAsync(s.AccessToken, ct);
        return new Metadata { { "authorization", "Bearer " + _session!.AccessToken } };
    }

    /// <summary>Выполняет вызов с токеном; после TOKEN_EXPIRED обновляет токен и повторяет один раз.</summary>
    public async Task<T> CallAsync<T>(Func<Metadata, Task<T>> call, CancellationToken ct = default)
    {
        try { return await call(await HeadersAsync(ct)); }
        catch (RpcException e) when (ApiError.Reason(e) == ErrorReason.TokenExpired)
        {
            await RefreshAsync(_session!.AccessToken, ct);
            return await call(await HeadersAsync(ct));
        }
        catch (RpcException e) when (ApiError.Reason(e) is ErrorReason.TokenRevoked or ErrorReason.RefreshReuseDetected)
        {
            SessionEnded?.Invoke("Сессия завершена на сервере.");
            throw;
        }
    }

    /// <summary>Единый обновитель: если токен уже заменён другим потоком, повторно не обновляет.</summary>
    private async Task RefreshAsync(string staleAccessToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var s = _session!;
            if (s.AccessToken != staleAccessToken) return;
            using var key = DeviceKey.Import(s.DevicePrivateKey);
            var now = DateTimeOffset.UtcNow;
            var sig = key.Sign(DeviceSignatures.RefreshMessage(s.RefreshToken, s.DeviceId, now.ToUnixTimeSeconds()));
            try
            {
                var t = await Auth.RefreshTokenAsync(new RefreshTokenRequest
                {
                    RefreshToken = s.RefreshToken, DeviceId = s.DeviceId, Timestamp = Timestamp.FromDateTimeOffset(now),
                    Signature = Google.Protobuf.ByteString.CopyFrom(sig),
                }, cancellationToken: ct);
                s.AccessToken = t.AccessToken; s.AccessExpires = t.AccessExpiresAt.ToDateTimeOffset(); s.RefreshToken = t.RefreshToken;
                _persist(s);
            }
            catch (RpcException e) when (ApiError.Reason(e) is ErrorReason.TokenRevoked or ErrorReason.RefreshReuseDetected or ErrorReason.TokenExpired
                                         or ErrorReason.InvalidCredentials)
            {
                SessionEnded?.Invoke("Сессия завершена: войдите снова.");
                throw;
            }
        }
        finally { _refreshGate.Release(); }
    }

    public void Dispose() => _channel.Dispose();
}
