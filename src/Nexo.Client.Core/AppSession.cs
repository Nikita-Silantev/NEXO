using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Nexo.Client.Core;

/// <summary>Нечувствительные настройки приложения (до разблокировки): адрес сервера, автоблокировка.</summary>
public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "https://localhost";
    public int AutoLockMinutes { get; set; } = 5;
    public bool LockOnMinimize { get; set; } = true;
    public string DeviceName { get; set; } = Environment.MachineName;

    public static string Directory => Environment.GetEnvironmentVariable("NEXO_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXO");
    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Открытая (разблокированная) сессия: локальная БД, API, синхронизация, действия пользователя.</summary>
public sealed class AppSession : IDisposable
{
    public LocalDb Db { get; }
    public NexoApi Api { get; }
    public SyncEngine Sync { get; private set; } = null!;
    public Messenger Messenger { get; private set; } = null!;
    public SessionData Data { get; }
    public byte[] Ldk { get; }

    public event Action<string>? Ended;

    private AppSession(LocalDb db, NexoApi api, SessionData data, byte[] ldk) { Db = db; Api = api; Data = data; Ldk = ldk; }

    private const string SessionKey = "session";

    public static void SaveSession(LocalDb db, SessionData s) => db.SetBytes(SessionKey, JsonSerializer.SerializeToUtf8Bytes(s));

    public static SessionData? LoadSession(LocalDb db) =>
        db.GetBytes(SessionKey) is { } b ? JsonSerializer.Deserialize<SessionData>(b) : null;

    public static async Task<AppSession> OpenAsync(Uri server, LocalDb db, SessionData data, byte[] ldk, CancellationToken ct = default)
    {
        var api = new NexoApi(server, s => SaveSession(db, s));
        api.SetSession(data);
        var s = new AppSession(db, api, data, ldk);
        if (string.IsNullOrEmpty(data.UserId))
        {
            var me = await api.CallAsync(h => api.Users.GetMeAsync(new Empty(), h, cancellationToken: ct).ResponseAsync, ct);
            data.UserId = me.Profile.UserId;
        }
        SaveSession(db, data);
        s.Sync = new SyncEngine(api, db, data.UserId);
        s.Messenger = new Messenger(api, db, s.Sync, data.UserId);
        api.SessionEnded += reason => s.Ended?.Invoke(reason);
        return s;
    }

    public void StartSync() => Sync.Start();

    public void Dispose() { Sync?.Dispose(); Api.Dispose(); Db.Dispose(); }
}
