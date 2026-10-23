using System.Security.Cryptography;

namespace Nexo.Core;

/// <summary>
/// Вычислительная задача (hashcash): найти nonce, при котором SHA-256(challenge || nonce)
/// имеет не менее difficultyBits ведущих нулевых бит. Документ 81 02, раздел «Вычислительная задача».
/// </summary>
public static class Pow
{
    public const int ChallengeLength = 16;
    public const int MinBits = 18;
    public const int MaxBits = 26;

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeLength);

    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        int bits = 0;
        foreach (var b in hash)
        {
            if (b == 0) { bits += 8; continue; }
            bits += System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
            break;
        }
        return bits;
    }

    public static bool Verify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> nonce, int difficultyBits)
    {
        if (challenge.Length != ChallengeLength || nonce.Length is 0 or > 32) return false;
        Span<byte> buf = stackalloc byte[ChallengeLength + 32];
        challenge.CopyTo(buf);
        nonce.CopyTo(buf[ChallengeLength..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf[..(ChallengeLength + nonce.Length)], hash);
        return LeadingZeroBits(hash) >= difficultyBits;
    }

    /// <summary>Решение на стороне клиента. Перебирает 8-байтовый счётчик.</summary>
    public static byte[] Solve(ReadOnlySpan<byte> challenge, int difficultyBits, CancellationToken ct = default)
    {
        var nonce = new byte[8];
        var buf = new byte[ChallengeLength + 8];
        challenge.CopyTo(buf);
        Span<byte> hash = stackalloc byte[32];
        ulong counter = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(ChallengeLength), counter);
            SHA256.HashData(buf, hash);
            if (LeadingZeroBits(hash) >= difficultyBits)
            {
                Array.Copy(buf, ChallengeLength, nonce, 0, 8);
                return nonce;
            }
            counter++;
        }
    }

    /// <summary>Адаптивная сложность: растёт с числом недавних запросов с одного адреса.</summary>
    public static int DifficultyFor(int recentAttempts) =>
        Math.Clamp(MinBits + recentAttempts / 3, MinBits, MaxBits);
}
