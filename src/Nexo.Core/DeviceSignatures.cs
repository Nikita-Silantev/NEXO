using System.Buffers.Binary;
using System.Text;

namespace Nexo.Core;

/// <summary>
/// Канонические сообщения для подписей ключом устройства (Ed25519). Клиент и сервер собирают их одним кодом.
/// Все строки – UTF-8; число – 8 байт big-endian; поля склеиваются без разделителей (длины фиксированы по формату).
/// </summary>
public static class DeviceSignatures
{
    /// <summary>refresh_token || device_id || unix_seconds (запрос обновления токена, ФТ-АУТ-06).</summary>
    public static byte[] RefreshMessage(string refreshToken, string deviceId, long unixSeconds)
    {
        var a = Encoding.UTF8.GetBytes(refreshToken);
        var b = Encoding.UTF8.GetBytes(deviceId);
        var r = new byte[a.Length + b.Length + 8];
        a.CopyTo(r, 0); b.CopyTo(r, a.Length);
        BinaryPrimitives.WriteInt64BigEndian(r.AsSpan(a.Length + b.Length), unixSeconds);
        return r;
    }

    /// <summary>approval_id || requester_pubkey || code (подтверждение входа с нового устройства, ФТ-УСТ-02).</summary>
    public static byte[] ApprovalMessage(string approvalId, ReadOnlySpan<byte> requesterPubkey, string code)
    {
        var a = Encoding.UTF8.GetBytes(approvalId);
        var c = Encoding.UTF8.GetBytes(code);
        var r = new byte[a.Length + requesterPubkey.Length + c.Length];
        a.CopyTo(r, 0); requesterPubkey.CopyTo(r.AsSpan(a.Length)); c.CopyTo(r, a.Length + requesterPubkey.Length);
        return r;
    }
}
