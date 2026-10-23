using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexo.Core;
using Nexo.Server.Infrastructure;
using NSec.Cryptography;

namespace Nexo.Server.Security;

public sealed record AccessClaims(Guid UserId, Guid DeviceId, Guid SessionId, DateTimeOffset ExpiresAt);

/// <summary>JWT с подписью EdDSA (Ed25519), срок 10 минут (ФТ-АУТ-06). Реализация по RFC 7519 / RFC 8037.</summary>
public sealed class AccessTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Issuer = "nexo", Audience = "nexo";
    private static readonly SignatureAlgorithm Alg = SignatureAlgorithm.Ed25519;

    private readonly Key _key;
    private readonly string _kid;

    public AccessTokenService(IOptions<ServerOptions> options, IHostEnvironment env, ILogger<AccessTokenService> log)
    {
        var o = options.Value;
        var file = string.IsNullOrWhiteSpace(o.SigningKeyFile) ? Path.Combine(o.DataDirectory, "signing-key.b64") : o.SigningKeyFile;
        if (File.Exists(file))
        {
            _key = Key.Import(Alg, Convert.FromBase64String(File.ReadAllText(file).Trim()), KeyBlobFormat.PkixPrivateKey,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        }
        else
        {
            _key = Key.Create(Alg, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, Convert.ToBase64String(_key.Export(KeyBlobFormat.PkixPrivateKey)));
            log.LogWarning("Создан ключ подписи токенов: {File}. В Production файл лежит в томе serverdata – включите его в резервное копирование.", file);
        }
        _kid = Base64Url.Encode(SHA256.HashData(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey))[..8]);
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid deviceId, Guid sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now + Lifetime;
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", typ = "JWT", kid = _kid }));
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = Issuer, aud = Audience, sub = userId, did = deviceId, sid = sessionId,
            iat = now.ToUnixTimeSeconds(), exp = exp.ToUnixTimeSeconds(),
        }));
        var input = header + "." + payload;
        var sig = Alg.Sign(_key, Encoding.ASCII.GetBytes(input));
        return (input + "." + Base64Url.Encode(sig), exp);
    }

    public enum Result { Ok, Expired, Invalid }

    public Result Validate(string token, out AccessClaims? claims)
    {
        claims = null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return Result.Invalid;
            var header = JsonDocument.Parse(Base64Url.Decode(parts[0])).RootElement;
            if (header.GetProperty("alg").GetString() != "EdDSA" || header.GetProperty("kid").GetString() != _kid) return Result.Invalid;
            var sig = Base64Url.Decode(parts[2]);
            if (!Alg.Verify(_key.PublicKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig)) return Result.Invalid;
            var p = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement;
            if (p.GetProperty("iss").GetString() != Issuer || p.GetProperty("aud").GetString() != Audience) return Result.Invalid;
            var exp = DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("exp").GetInt64());
            claims = new AccessClaims(p.GetProperty("sub").GetGuid(), p.GetProperty("did").GetGuid(), p.GetProperty("sid").GetGuid(), exp);
            return exp <= DateTimeOffset.UtcNow ? Result.Expired : Result.Ok;
        }
        catch { return Result.Invalid; }
    }
}
