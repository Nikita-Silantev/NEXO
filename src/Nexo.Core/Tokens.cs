using System.Security.Cryptography;
using System.Text;

namespace Nexo.Core;

/// <summary>Генерация и хэширование случайных токенов. Сервер хранит только хэши.</summary>
public static class Tokens
{
    /// <summary>256 бит случайных данных в base64url без заполнения.</summary>
    public static string NewOpaque()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Шестизначный цифровой код сверки (для подтверждения входа с нового устройства).</summary>
    public static string NewNumericCode(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        return RandomNumberGenerator.GetInt32(max).ToString(new string('0', digits));
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
