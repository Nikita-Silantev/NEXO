using System.Net;

namespace Nexo.Core;

/// <summary>Маскирование IP для показа пользователю: последний октет IPv4 / последние 80 бит IPv6 скрыты.</summary>
public static class IpMask
{
    public static string Mask(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return "";
        var b = a.GetAddressBytes();
        if (b.Length == 4) return $"{b[0]}.{b[1]}.{b[2]}.***";
        var hex = string.Join(":", Enumerable.Range(0, 3).Select(i => ((b[2 * i] << 8) | b[2 * i + 1]).ToString("x")));
        return hex + ":****";
    }
}
