using Grpc.AspNetCore.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nexo.Core;
using Nexo.Server.Auth;
using Nexo.Server.Chats;
using Nexo.Server.Infrastructure;
using Nexo.Server.Jobs;
using Nexo.Server.Messages;
using Nexo.Server.Security;
using Nexo.Server.Sync;
using Nexo.Server.Users;
using Npgsql;

Db.ConfigureDapper();

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Nexo"));
var opt = builder.Configuration.GetSection("Nexo").Get<ServerOptions>() ?? new ServerOptions();

builder.WebHost.ConfigureKestrel(k =>
{
    // gRPC (HTTP/2 без TLS): TLS завершает обратный прокси Caddy. HTTP/1.1 – проверка здоровья и gRPC-Web для браузера.
    k.ListenAnyIP(opt.GrpcPort, o => o.Protocols = HttpProtocols.Http2);
    k.ListenAnyIP(opt.HttpPort, o => o.Protocols = HttpProtocols.Http1);
    k.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
});

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(opt.ConnectionString).Build());
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRateLimiter>(_ => new SlidingWindowLimiter());
builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<AccessTokenService>();
builder.Services.AddSingleton<SessionGuard>();
builder.Services.AddSingleton<PasswordVerifier>();
builder.Services.AddSingleton<PowService>();
builder.Services.AddSingleton<UpdateHub>();
builder.Services.AddSingleton<UpdateJournal>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<UserQueries>();
builder.Services.AddSingleton<ChatQueries>();
builder.Services.AddHostedService<MaintenanceService>();

builder.Services.AddGrpc(o =>
{
    o.MaxReceiveMessageSize = 4 * 1024 * 1024;
    o.Interceptors.Add<ErrorInterceptor>();   // внешний: превращает NexoException в ошибку gRPC
    o.Interceptors.Add<AuthInterceptor>();
});
builder.Services.AddSingleton<ErrorInterceptor>();
builder.Services.AddSingleton<AuthInterceptor>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();   // сервер доступен только из сети Docker за Caddy
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGet("/health", async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    try
    {
        await using var con = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("select 1", con);
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok" });
    }
    catch { return Results.StatusCode(503); }
});

app.MapGrpcService<AuthServiceImpl>();
app.MapGrpcService<DeviceServiceImpl>();
app.MapGrpcService<LegalServiceImpl>();
app.MapGrpcService<UserServiceImpl>();
app.MapGrpcService<ChatServiceImpl>();
app.MapGrpcService<MessageServiceImpl>();
app.MapGrpcService<SyncServiceImpl>();

app.Run();
