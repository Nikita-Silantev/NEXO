using System.Security.Cryptography;
using System.Text.Json;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

public sealed class VaultLockedOutException(int attemptsLeft) : Exception("Неверный код. Осталось попыток: " + attemptsLeft)
{
    public int AttemptsLeft { get; } = attemptsLeft;
}

public sealed class VaultWipedException() : Exception("Превышено число попыток: локальные данные удалены.");

/// <summary>
/// Хранилище ключа локальной БД (LDK, 256 бит). LDK независимо оборачивается двумя ключами Argon2id:
/// паролем учётной записи (всегда) и PIN-кодом (по желанию пользователя). Число неверных попыток ограничено;
/// после 10 ошибок (если включено) локальные данные стираются (НФТ-БЕЗ-05).
/// Платформенные хранилища (Keychain, DPAPI, Secret Service) и биометрия добавляются отдельной задачей третьим способом
/// оборачивания того же LDK; биометрия лишь разрешает платформе выдать ключ, криптографию она не заменяет.
/// </summary>
public sealed class LocalVault(string directory)
{
    private sealed class Meta
    {
        public int Version { get; set; } = 2;
        public byte[] PasswordSalt { get; set; } = [];
        public byte[] PasswordWrapped { get; set; } = [];
        public byte[]? PinSalt { get; set; }
        public byte[]? PinWrapped { get; set; }
        public int Failed { get; set; }
        public bool WipeAfter10 { get; set; }
    }

    public const int MaxAttempts = 10;
    private static readonly byte[] Aad = "nexo-ldk"u8.ToArray();
    private string MetaPath => Path.Combine(directory, "vault.json");
    public string DbPath => Path.Combine(directory, "nexo.db");

    public bool Exists => File.Exists(MetaPath);
    public bool HasPin => Exists && Load().PinWrapped is not null;
    public bool WipeAfterFailures => Exists && Load().WipeAfter10;

    private Meta Load() => JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) ?? throw new InvalidDataException("vault.json");
    private void Save(Meta m)
    {
        Directory.CreateDirectory(directory);
        var tmp = MetaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m));
        File.Move(tmp, MetaPath, true);
    }

    private static readonly KdfParams Params = new() { Algorithm = "argon2id", MemoryKib = 65536, Iterations = 3, Parallelism = 1, Version = 1 };

    private static async Task<(byte[] Salt, byte[] Wrapped)> WrapAsync(byte[] ldk, string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = await Kdf.DeriveAsync(secret, salt, Params);
        try { return (salt, Aead.Seal(key, ldk, Aad)); } finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Создаёт хранилище: новый LDK, обёрнутый паролем учётной записи.</summary>
    public async Task<byte[]> CreateAsync(string password)
    {
        var ldk = RandomNumberGenerator.GetBytes(32);
        var (salt, wrapped) = await WrapAsync(ldk, password);
        Save(new Meta { PasswordSalt = salt, PasswordWrapped = wrapped });
        return ldk;
    }

    /// <summary>Разблокировка PIN-кодом или паролем. Счётчик ошибок сохраняется ДО проверки (нельзя обойти аварийным завершением).</summary>
    public async Task<byte[]> UnlockAsync(string secret, bool usePin)
    {
        var m = Load();
        var salt = usePin ? m.PinSalt : m.PasswordSalt;
        var wrapped = usePin ? m.PinWrapped : m.PasswordWrapped;
        if (salt is null || wrapped is null) throw new InvalidOperationException("PIN не установлен");
        m.Failed++; Save(m);
        var key = await Kdf.DeriveAsync(secret, salt, Params);
        try
        {
            var ldk = Aead.Open(key, wrapped, Aad);
            m.Failed = 0; Save(m);
            return ldk;
        }
        catch (CryptographicException)
        {
            if (m.WipeAfter10 && m.Failed >= MaxAttempts) { Wipe(); throw new VaultWipedException(); }
            throw new VaultLockedOutException(Math.Max(0, MaxAttempts - m.Failed));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task SetPinAsync(byte[] ldk, string pin)
    {
        var m = Load();
        (m.PinSalt, m.PinWrapped) = await WrapAsync(ldk, pin);
        Save(m);
    }

    public void RemovePin() { var m = Load(); m.PinSalt = null; m.PinWrapped = null; Save(m); }
    public void SetWipeAfterFailures(bool on) { var m = Load(); m.WipeAfter10 = on; Save(m); }

    /// <summary>Удаляет ключ и локальную БД (выход из учётной записи, превышение попыток).</summary>
    public void Wipe()
    {
        foreach (var f in new[] { MetaPath, DbPath, DbPath + "-wal", DbPath + "-shm", DbPath + "-journal" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* файл может быть занят – повторится при следующем запуске */ }
        }
    }
}
