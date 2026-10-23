using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Nexo.Core;
using Nexo.Protocol.V1;
using NSec.Cryptography;

namespace Nexo.Client.Core;

/// <summary>Argon2id на клиенте (MasterKey = Argon2id(пароль, соль, параметры)).</summary>
public static class Kdf
{
    public static KdfParams DefaultParams => new() { Algorithm = "argon2id", MemoryKib = 65536, Iterations = 3, Parallelism = 1, Version = 1 };

    public static Task<byte[]> DeriveAsync(string secret, byte[] salt, KdfParams p) => Task.Run(() =>
    {
        var pw = Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormKC));
        try
        {
            using var a = new Konscious.Security.Cryptography.Argon2id(pw)
            {
                Salt = salt, DegreeOfParallelism = (int)p.Parallelism, Iterations = (int)p.Iterations, MemorySize = (int)p.MemoryKib,
            };
            return a.GetBytes(32);
        }
        finally { CryptographicOperations.ZeroMemory(pw); }
    });
}

/// <summary>Ключ устройства Ed25519 (подписи обновления токенов и подтверждения входа).</summary>
public sealed class DeviceKey : IDisposable
{
    private static readonly SignatureAlgorithm Alg = SignatureAlgorithm.Ed25519;
    private readonly Key _key;

    private DeviceKey(Key k) => _key = k;

    public static DeviceKey Generate() => new(Key.Create(Alg, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));

    public static DeviceKey Import(byte[] pkcs8) =>
        new(Key.Import(Alg, pkcs8, KeyBlobFormat.PkixPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));

    public byte[] PublicKey => _key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    public byte[] ExportPrivate() => _key.Export(KeyBlobFormat.PkixPrivateKey);
    public byte[] Sign(byte[] message) => Alg.Sign(_key, message);
    public void Dispose() => _key.Dispose();
}

/// <summary>Шифрование небольших блоков AES-256-GCM: nonce(12) | tag(16) | шифртекст.</summary>
public static class Aead
{
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[]? aad = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length]; var tag = new byte[16];
        using var g = new AesGcm(key, 16);
        g.Encrypt(nonce, plaintext, ct, tag, aad);
        return [.. nonce, .. tag, .. ct];
    }

    public static byte[] Open(byte[] key, byte[] sealedData, byte[]? aad = null)
    {
        if (sealedData.Length < 28) throw new CryptographicException("Короткий блок");
        var pt = new byte[sealedData.Length - 28];
        using var g = new AesGcm(key, 16);
        g.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), pt, aad);
        return pt;
    }
}
