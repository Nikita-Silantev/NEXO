using System.Security.Cryptography;
using System.Text.Json;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

public enum VaultSecretKind { Password, Pin }

public sealed class VaultLockedOutException(int attemptsLeft) : Exception("Неверный код. Осталось попыток: " + attemptsLeft)
{
    public int AttemptsLeft { get; } = attemptsLeft;
}

public sealed class VaultWipedException() : Exception("Превышено число попыток: локальные данные удалены.");

/// <summary>
/// Хранилище ключа локальной БД (LDK, 256 бит). LDK оборачивается ключом Argon2id(секрет): секрет – пароль учётной записи
/// либо PIN. Число неверных попыток ограничено; после порога (по настройке) локальные данные стираются (НФТ-БЕЗ-05).
/// Платформенные хранилища (Keychain, DPAPI, Secret Service) и биометрия подключаются отдельной задачей поверх этого же LDK.
/// </summary>
public sealed class LocalVault(string directory)
{
    private sealed class Meta
    {
        public int Version { get; set; } = 1;
        public string Kind { get; set; } = "Password";
        public byte[] Salt { get; set; } = [];
        public uint MemoryKib { get; set; } = 65536;
        public uint Iterations { get; set; } = 3;
        public byte[] Wrapped { get; set; } = [];
        public int Failed { get; set; }
        public bool WipeAfter10 { get; set; }
    }

    public const int MaxAttempts = 10;
    private string MetaPath => Path.Combine(directory, "vault.json");
    public string DbPath => Path.Combine(directory, "nexo.db");

    public bool Exists => File.Exists(MetaPath);

    public VaultSecretKind Kind => Load().Kind == "Pin" ? VaultSecretKind.Pin : VaultSecretKind.Password;
    public bool WipeAfterFailures => Load().WipeAfter10;

    private Meta Load() => JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) ?? throw new InvalidDataException("vault.json");
    private void Save(Meta m)
    {
        Directory.CreateDirectory(directory);
        var tmp = MetaPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m));
        File.Move(tmp, MetaPath, true);
    }

    private static KdfParams P(Meta m) => new() { Algorithm = "argon2id", MemoryKib = m.MemoryKib, Iterations = m.Iterations, Parallelism = 1, Version = 1 };

    /// <summary>Создаёт хранилище: новый LDK, обёрнутый секретом.</summary>
    public async Task<byte[]> CreateAsync(string secret, VaultSecretKind kind)
    {
        var ldk = RandomNumberGenerator.GetBytes(32);
        await WrapAsync(ldk, secret, kind, wipe: false);
        return ldk;
    }

    private async Task WrapAsync(byte[] ldk, string secret, VaultSecretKind kind, bool wipe)
    {
        var m = new Meta { Kind = kind.ToString(), Salt = RandomNumberGenerator.GetBytes(16), WipeAfter10 = wipe };
        var key = await Kdf.DeriveAsync(secret, m.Salt, P(m));
        m.Wrapped = Aead.Seal(key, ldk, "nexo-ldk"u8.ToArray());
        CryptographicOperations.ZeroMemory(key);
        Save(m);
    }

    /// <summary>Разблокировка. Счётчик ошибок сохраняется ДО проверки, чтобы его нельзя было обойти аварийным завершением.</summary>
    public async Task<byte[]> UnlockAsync(string secret)
    {
        var m = Load();
        m.Failed++; Save(m);
        var key = await Kdf.DeriveAsync(secret, m.Salt, P(m));
        try
        {
            var ldk = Aead.Open(key, m.Wrapped, "nexo-ldk"u8.ToArray());
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

    /// <summary>Смена секрета (например, установка PIN). Требует уже разблокированного LDK.</summary>
    public Task ChangeSecretAsync(byte[] ldk, string newSecret, VaultSecretKind kind, bool wipeAfter10) =>
        WrapAsync(ldk, newSecret, kind, wipeAfter10);

    /// <summary>Удаляет ключ и локальную БД (выход из учётной записи, превышение попыток).</summary>
    public void Wipe()
    {
        foreach (var f in new[] { MetaPath, DbPath, DbPath + "-wal", DbPath + "-shm", DbPath + "-journal" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* файл может быть занят – повторится при следующем запуске */ }
        }
    }
}
