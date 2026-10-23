using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using Nexo.Core;

namespace Nexo.Server.Security;

/// <summary>
/// Хранение верификаторов: Argon2id(AuthKey) с индивидуальной солью, формат PHC.
/// Параметры не ниже минимума OWASP (память 19 МиБ, 2 прохода, параллелизм 1).
/// AuthKey уже получен клиентом из пароля через Argon2id, поэтому это второй слой защиты.
/// </summary>
public sealed class PasswordVerifier
{
    private const int MemoryKib = 19456, Iterations = 2, Parallelism = 1, HashLength = 32;
    // Фиктивная запись для выравнивания времени при несуществующем нике.
    private readonly string _dummy;

    public PasswordVerifier() => _dummy = Hash(RandomNumberGenerator.GetBytes(32));

    public string Hash(byte[] key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(key, salt, MemoryKib, Iterations, Parallelism);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(byte[] key, string phc)
    {
        try
        {
            var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
            // argon2id, v=19, m=..,t=..,p=.., salt, hash
            if (parts.Length != 5 || parts[0] != "argon2id") return false;
            var prm = parts[2].Split(',').Select(x => int.Parse(x[2..])).ToArray();
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Compute(key, salt, prm[0], prm[1], prm[2]);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    /// <summary>Выполняет проверку впустую, чтобы время ответа не выдавало отсутствие ника.</summary>
    public void VerifyDummy(byte[] key) => Verify(key, _dummy);

    private static byte[] Compute(byte[] key, byte[] salt, int memKib, int iter, int par)
    {
        using var a = new Argon2id(key) { Salt = salt, MemorySize = memKib, Iterations = iter, DegreeOfParallelism = par };
        return a.GetBytes(HashLength);
    }
}
