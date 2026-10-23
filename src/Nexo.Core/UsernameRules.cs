using System.Text.RegularExpressions;

namespace Nexo.Core;

/// <summary>Правила ника: 3–32 символа a–z, 0–9, _; первый символ – буква (ФТ-АУТ-01, ограничение CHECK в таблице users). Ник хранится в нижнем регистре.</summary>
public static partial class UsernameRules
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,31}$")]
    private static partial Regex Pattern();

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "root", "support", "system", "nexo", "moderator", "mod",
        "security", "abuse", "help", "official", "staff", "service", "deleted", "null", "undefined",
    };

    public static string Normalize(string? username) => (username ?? "").Trim().ToLowerInvariant();

    public static bool IsValid(string normalized) => Pattern().IsMatch(normalized) && !Reserved.Contains(normalized);

    public static bool IsValidDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length is < 1 or > 64) return false;
        foreach (var c in name) if (char.IsControl(c)) return false;
        return true;
    }
}
