namespace Nexo.Core;

/// <summary>Политика пароля (проверяется только на клиенте: сервер пароля не видит).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>Возвращает текст замечания или null, если пароль подходит.</summary>
    public static string? Check(string password, string? username = null)
    {
        if (password.Length < MinLength) return $"Пароль должен быть не короче {MinLength} символов.";
        if (password.Distinct().Count() < 5) return "Слишком много повторяющихся символов.";
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "Пароль не должен содержать ник.";
        var common = new[] { "password", "qwerty", "123456", "йцукен", "11111111" };
        if (common.Any(c => password.Contains(c, StringComparison.OrdinalIgnoreCase))) return "Пароль слишком распространённый.";
        return null;
    }
}
