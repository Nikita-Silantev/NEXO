namespace Nexo.Server.Infrastructure;

public sealed class ServerOptions
{
    public string ConnectionString { get; set; } = "";
    public int GrpcPort { get; set; } = 5080;
    public int HttpPort { get; set; } = 5081;
    public string DataDirectory { get; set; } = "data";
    /// <summary>Ключ шифрования секретов (AES-256, base64). В Production обязателен.</summary>
    public string KekBase64 { get; set; } = "";
    /// <summary>Секрет для ложных параметров KDF (base64). В Production обязателен.</summary>
    public string FakeKdfSecretBase64 { get; set; } = "";
    /// <summary>Файл закрытого ключа подписи токенов (Ed25519, PKCS#8, base64). Создаётся при первом запуске в Development.</summary>
    public string SigningKeyFile { get; set; } = "";
}
