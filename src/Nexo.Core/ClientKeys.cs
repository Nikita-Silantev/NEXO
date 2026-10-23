using System.Security.Cryptography;
using System.Text;

namespace Nexo.Core;

/// <summary>Выработка ключей на клиенте (раздел «Криптография»: разделение ключей HKDF-SHA-256, метки фиксированы).</summary>
public static class ClientKeys
{
    public const string AuthLabel = "nexo-auth";
    public const string RecoveryVerifyLabel = "nexo-recovery-verify";

    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, string info, int length = 32, ReadOnlySpan<byte> salt = default)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm.ToArray(), length, salt.ToArray(), Encoding.UTF8.GetBytes(info));

    /// <summary>AuthKey = HKDF(MasterKey, "nexo-auth"). Именно он (а не пароль) уходит на сервер.</summary>
    public static byte[] AuthKey(ReadOnlySpan<byte> masterKey) => Hkdf(masterKey, AuthLabel);

    /// <summary>Проверочное значение ключа восстановления = HKDF(RK, "nexo-recovery-verify").</summary>
    public static byte[] RecoveryVerifier(ReadOnlySpan<byte> recoveryKey) => Hkdf(recoveryKey, RecoveryVerifyLabel);

    /// <summary>Соль для KDF: 16 случайных байт.</summary>
    public static byte[] NewKdfSalt() => RandomNumberGenerator.GetBytes(16);
}

/// <summary>Ключ восстановления: 256 бит; для человека записывается в base32 группами по 4 символа.</summary>
public static class RecoveryKey
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Generate() => RandomNumberGenerator.GetBytes(32);

    public static string Format(ReadOnlySpan<byte> key)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { bits -= 5; sb.Append(Alphabet[(buffer >> bits) & 31]); }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        var raw = sb.ToString();
        var parts = new List<string>();
        for (var i = 0; i < raw.Length; i += 4) parts.Add(raw.Substring(i, Math.Min(4, raw.Length - i)));
        return string.Join('-', parts);
    }

    /// <summary>Разбор введённого пользователем ключа (регистр, дефисы и пробелы не важны). Null – формат неверен.</summary>
    public static byte[]? Parse(string text)
    {
        var s = new string(text.Where(c => c is not ('-' or ' ' or '\n' or '\r' or '\t')).ToArray()).ToUpperInvariant();
        if (s.Length != 52) return null;
        var result = new List<byte>(32);
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bits -= 8; result.Add((byte)((buffer >> bits) & 0xFF)); buffer &= (1 << bits) - 1; }
        }
        return result.Count == 32 ? result.ToArray() : null;
    }
}
