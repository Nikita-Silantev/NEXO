using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexo.Server.Infrastructure;

namespace Nexo.Server.Security;

/// <summary>
/// Серверные секреты. В Production берутся ТОЛЬКО из конфигурации/секретов Docker; при отсутствии – ошибка запуска.
/// В Development недостающие секреты создаются и сохраняются в каталоге данных (для локальных проверок).
/// </summary>
public sealed class Secrets
{
    public byte[] Kek { get; }
    public byte[] FakeKdfSecret { get; }

    public Secrets(IOptions<ServerOptions> options, IHostEnvironment env, ILogger<Secrets> log)
    {
        var o = options.Value;
        var dev = env.IsDevelopment();
        var devFile = Path.Combine(o.DataDirectory, "dev-secrets.json");
        Dictionary<string, string> store = new();
        if (dev && File.Exists(devFile))
            store = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(devFile)) ?? new();

        byte[] Resolve(string configured, string name)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return Convert.FromBase64String(configured);
            if (!dev) throw new InvalidOperationException($"Не задан секрет {name} (Nexo__{name}).");
            if (!store.TryGetValue(name, out var v))
            {
                v = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                store[name] = v;
                Directory.CreateDirectory(o.DataDirectory);
                File.WriteAllText(devFile, JsonSerializer.Serialize(store));
                log.LogWarning("Создан секрет разработки {Name} в {File}. Не использовать в Production.", name, devFile);
            }
            return Convert.FromBase64String(v);
        }

        Kek = Resolve(o.KekBase64, "KekBase64");
        FakeKdfSecret = Resolve(o.FakeKdfSecretBase64, "FakeKdfSecretBase64");
        if (Kek.Length != 32) throw new InvalidOperationException("KekBase64 должен содержать 32 байта.");
    }
}

/// <summary>Шифрование секретов в БД: AES-256-GCM, формат: версия(1) | nonce(12) | tag(16) | шифртекст (НФТ-БЕЗ-02).</summary>
public sealed class SecretBox(Secrets secrets)
{
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(secrets.Kek, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        var result = new byte[1 + 12 + 16 + cipher.Length];
        result[0] = 1;
        nonce.CopyTo(result, 1); tag.CopyTo(result, 13); cipher.CopyTo(result, 29);
        return result;
    }

    public byte[] Open(byte[] sealedData)
    {
        if (sealedData.Length < 29 || sealedData[0] != 1) throw new CryptographicException("bad format");
        var nonce = sealedData.AsSpan(1, 12);
        var tag = sealedData.AsSpan(13, 16);
        var cipher = sealedData.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(secrets.Kek, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
