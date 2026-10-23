using System.Security.Cryptography;
using System.Text;

namespace Nexo.Core;

/// <summary>
/// Ложные параметры KDF для несуществующих ников (ФТ-АУТ-08): детерминированны (HMAC от ника
/// серверного секрета), поэтому одинаковы между запросами и неотличимы от настоящих.
/// </summary>
public static class FakeKdf
{
    public static byte[] Salt(byte[] serverSecret, string normalizedUsername)
    {
        var h = HMACSHA256.HashData(serverSecret, Encoding.UTF8.GetBytes("nexo-fake-kdf-salt:" + normalizedUsername));
        return h.AsSpan(0, 16).ToArray();
    }
}
