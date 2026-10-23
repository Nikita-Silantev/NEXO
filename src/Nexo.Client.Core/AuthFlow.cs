using Google.Protobuf;
using Grpc.Core;
using Nexo.Core;
using Nexo.Protocol.V1;

namespace Nexo.Client.Core;

/// <summary>Результат регистрации: ключ восстановления показывается пользователю один раз.</summary>
public sealed record RegisterResult(SessionData Session, string RecoveryKeyText, byte[] MasterKeyForVault);

public abstract record LoginOutcome;
public sealed record LoginSucceeded(SessionData Session, IReadOnlyList<LegalDocKind> DocumentsToAccept) : LoginOutcome;
public sealed record LoginNeedsSecondFactor(string ChallengeId, bool Totp, bool BackupCode, DeviceKey Key) : LoginOutcome;
public sealed record LoginNeedsApproval(string ApprovalId, string Code, DateTimeOffset ExpiresAt, DeviceKey Key, string Username) : LoginOutcome;

/// <summary>Сценарии без токена: регистрация, вход, ожидание подтверждения, восстановление.</summary>
public sealed class AuthFlow(NexoApi api)
{
    public static DeviceInfo DescribeDevice(string name, byte[] pubkey) => new()
    {
        Name = name,
        Platform = OperatingSystem.IsWindows() ? DevicePlatform.Windows : OperatingSystem.IsMacOS() ? DevicePlatform.Macos : DevicePlatform.Linux,
        AppVersion = typeof(AuthFlow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        OsVersion = Environment.OSVersion.VersionString.Length > 60 ? Environment.OSVersion.VersionString[..60] : Environment.OSVersion.VersionString,
        DevicePubkey = ByteString.CopyFrom(pubkey),
    };

    private async Task<PowSolution> SolvePowAsync(GetPowChallengeRequest.Types.Purpose purpose, CancellationToken ct)
    {
        var ch = await api.Auth.GetPowChallengeAsync(new GetPowChallengeRequest { Purpose = purpose }, cancellationToken: ct);
        var nonce = await Task.Run(() => Pow.Solve(ch.Challenge.Span, (int)ch.DifficultyBits, ct), ct);
        return new PowSolution { ChallengeId = ch.ChallengeId, Nonce = ByteString.CopyFrom(nonce) };
    }

    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct = default)
    {
        var r = await api.Auth.CheckUsernameAsync(new CheckUsernameRequest { Username = UsernameRules.Normalize(username) }, cancellationToken: ct);
        return r.Available;
    }

    public async Task<IReadOnlyList<LegalDocument>> GetDocumentsAsync(CancellationToken ct = default) =>
        (await api.Legal.GetLegalDocumentsAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: ct)).Documents;

    /// <summary>Регистрация. Пароль и MasterKey не покидают устройство; на сервер уходят AuthKey и проверочное значение RK.</summary>
    public async Task<RegisterResult> RegisterAsync(string username, string displayName, string password, string deviceName,
        IEnumerable<LegalDocument> acceptedDocs, CancellationToken ct = default)
    {
        username = UsernameRules.Normalize(username);
        var salt = ClientKeys.NewKdfSalt();
        var kdf = Kdf.DefaultParams;
        var master = await Kdf.DeriveAsync(password, salt, kdf);
        var authKey = ClientKeys.AuthKey(master);
        var rk = RecoveryKey.Generate();
        var rkVerify = ClientKeys.RecoveryVerifier(rk);
        using var key = DeviceKey.Generate();
        var pow = await SolvePowAsync(GetPowChallengeRequest.Types.Purpose.Register, ct);

        var req = new RegisterRequest
        {
            Username = username, DisplayName = displayName.Trim(), KdfSalt = ByteString.CopyFrom(salt), KdfParams = kdf,
            AuthKey = ByteString.CopyFrom(authKey), RecoveryVerifierKey = ByteString.CopyFrom(rkVerify), Pow = pow,
            Device = DescribeDevice(deviceName, key.PublicKey), Locale = "ru",
        };
        foreach (var d in acceptedDocs) req.AcceptedDocuments.Add(new AcceptedDocument { Kind = d.Kind, Version = d.Version });
        var res = await api.Auth.RegisterAsync(req, cancellationToken: ct);
        var session = NexoApi.FromTokens(res.Tokens, username, key.ExportPrivate(), res.UserId);
        var text = RecoveryKey.Format(rk);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(rk);
        return new RegisterResult(session, text, master);
    }

    public async Task<LoginOutcome> LoginAsync(string username, string password, string deviceName, CancellationToken ct = default)
    {
        username = UsernameRules.Normalize(username);
        var p = await api.Auth.GetKdfParamsAsync(new GetKdfParamsRequest { Username = username }, cancellationToken: ct);
        var master = await Kdf.DeriveAsync(password, p.KdfSalt.ToByteArray(), p.Params);
        var authKey = ClientKeys.AuthKey(master);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(master);
        var key = DeviceKey.Generate();
        var req = new LoginRequest { Username = username, AuthKey = ByteString.CopyFrom(authKey), Device = DescribeDevice(deviceName, key.PublicKey) };
        LoginResponse res;
        try { res = await api.Auth.LoginAsync(req, cancellationToken: ct); }
        catch (RpcException e) when (ApiError.Reason(e) == ErrorReason.PowRequired)
        {
            req.Pow = await SolvePowAsync(GetPowChallengeRequest.Types.Purpose.Login, ct);
            res = await api.Auth.LoginAsync(req, cancellationToken: ct);
        }
        return Interpret(res, username, key);
    }

    private static LoginOutcome Interpret(LoginResponse res, string username, DeviceKey key)
    {
        switch (res.ResultCase)
        {
            case LoginResponse.ResultOneofCase.Tokens:
                var s = NexoApi.FromTokens(res.Tokens, username, key.ExportPrivate());
                key.Dispose();
                return new LoginSucceeded(s, res.DocumentsToAccept);
            case LoginResponse.ResultOneofCase.SecondFactor:
                return new LoginNeedsSecondFactor(res.SecondFactor.ChallengeId, res.SecondFactor.Totp, res.SecondFactor.BackupCode, key);
            case LoginResponse.ResultOneofCase.Approval:
                return new LoginNeedsApproval(res.Approval.ApprovalId, res.Approval.Code, res.Approval.ExpiresAt.ToDateTimeOffset(), key, username);
            default: throw new InvalidOperationException("Пустой ответ входа");
        }
    }

    public async Task<LoginOutcome> CompleteSecondFactorAsync(LoginNeedsSecondFactor pending, string username, string code, bool isBackup, CancellationToken ct = default)
    {
        var req = new CompleteSecondFactorRequest { ChallengeId = pending.ChallengeId };
        if (isBackup) req.BackupCode = code.Trim(); else req.TotpCode = code.Trim();
        var res = await api.Auth.CompleteSecondFactorAsync(req, cancellationToken: ct);
        return Interpret(res, username, pending.Key);
    }

    /// <summary>Ждёт решения по запросу подтверждения входа (код сверяется на двух устройствах).</summary>
    public async Task<LoginOutcome?> WaitForApprovalAsync(LoginNeedsApproval pending, CancellationToken ct)
    {
        using var call = api.Auth.WatchLoginApproval(new WatchLoginApprovalRequest
        { ApprovalId = pending.ApprovalId, DevicePubkey = ByteString.CopyFrom(pending.Key.PublicKey) }, cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var ev = call.ResponseStream.Current;
            switch (ev.Status)
            {
                case ApprovalStatus.Approved:
                    var s = NexoApi.FromTokens(ev.Tokens, pending.Username, pending.Key.ExportPrivate());
                    return new LoginSucceeded(s, []);
                case ApprovalStatus.Denied or ApprovalStatus.Expired: return null;
            }
        }
        return null;
    }

    /// <summary>Восстановление доступа по ключу восстановления (задаёт новый пароль и выпускает новый ключ восстановления).</summary>
    public async Task<(SessionData? Session, string? NewRecoveryKey, DateTimeOffset? DelayedUntil, byte[]? Master)> RecoverAsync(
        string username, string recoveryKeyText, string newPassword, string deviceName, string? secondFactorCode, bool backup, CancellationToken ct = default)
    {
        username = UsernameRules.Normalize(username);
        var rk = RecoveryKey.Parse(recoveryKeyText) ?? throw new FormatException("Ключ восстановления введён неверно.");
        var salt = ClientKeys.NewKdfSalt(); var kdf = Kdf.DefaultParams;
        var master = await Kdf.DeriveAsync(newPassword, salt, kdf);
        var newRk = RecoveryKey.Generate();
        using var key = DeviceKey.Generate();
        var req = new RecoverAccountRequest
        {
            Username = username, RecoveryVerifierKey = ByteString.CopyFrom(ClientKeys.RecoveryVerifier(rk)),
            NewKdfSalt = ByteString.CopyFrom(salt), NewKdfParams = kdf, NewAuthKey = ByteString.CopyFrom(ClientKeys.AuthKey(master)),
            NewRecoveryVerifierKey = ByteString.CopyFrom(ClientKeys.RecoveryVerifier(newRk)),
            Pow = await SolvePowAsync(GetPowChallengeRequest.Types.Purpose.Recover, ct),
            Device = DescribeDevice(deviceName, key.PublicKey),
        };
        if (!string.IsNullOrWhiteSpace(secondFactorCode)) { if (backup) req.BackupCode = secondFactorCode.Trim(); else req.TotpCode = secondFactorCode.Trim(); }
        var res = await api.Auth.RecoverAccountAsync(req, cancellationToken: ct);
        if (res.ResultCase == RecoverAccountResponse.ResultOneofCase.DelayedUntil)
            return (null, null, res.DelayedUntil.ToDateTimeOffset(), null);
        return (NexoApi.FromTokens(res.Tokens, username, key.ExportPrivate()), RecoveryKey.Format(newRk), null, master);
    }
}
