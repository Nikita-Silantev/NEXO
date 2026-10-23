namespace Nexo.Core;

/// <summary>Лимиты попыток входа (ФТ-АУТ-09, документ протокола – таблица лимитов).</summary>
public static class LoginPolicy
{
    public const int MaxFailuresPerUser = 5;            // за окно – блокировка входа по нику
    public static readonly TimeSpan UserWindow = TimeSpan.FromMinutes(15);
    public const int MaxAttemptsPerIp = 30;
    public static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(15);
    public const int FailuresBeforePow = 2;             // после стольких неудач требуется PoW

    public static TimeSpan Backoff(int failures) =>
        failures <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, 1 << Math.Min(failures, 8)));
}
