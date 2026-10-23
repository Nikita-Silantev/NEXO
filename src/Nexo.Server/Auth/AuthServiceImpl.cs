using System.Data.Common;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Nexo.Server.Security;
using Nexo.Server.Sync;
using Npgsql;
using NSec.Cryptography;

namespace Nexo.Server.Auth;

/// <summary>Сервис аутентификации (ФТ-АУТ-*, ФТ-УСТ-*, ФТ-ПРА-*). TOTP – этап Э2 (методы возвращают Unimplemented).</summary>
public sealed class AuthServiceImpl(
    NpgsqlDataSource ds, PasswordVerifier verifier, PowService pow, SessionService sessions, UpdateJournal journal,
    UpdateHub hub, IRateLimiter limiter, Secrets secrets, SessionGuard guard) : AuthService.AuthServiceBase
{
    private sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = "";
        public string State { get; set; } = "";
        public byte[] KdfSalt { get; set; } = [];
        public string KdfParams { get; set; } = "";
        public string AuthVerifier { get; set; } = "";
        public string RecoveryVerifier { get; set; } = "";
        public bool TotpEnabled { get; set; }
    }

    private const string UserSelect =
        "select id, username::text as username, state::text as state, kdf_salt, kdf_params::text as kdf_params, auth_verifier, recovery_verifier, totp_enabled from users";

    // ---------------- Вычислительная задача, ник, параметры KDF ----------------

    public override Task<GetPowChallengeResponse> GetPowChallenge(GetPowChallengeRequest request, ServerCallContext context)
        => Task.FromResult(pow.Issue(request.Purpose, context.ClientIp()));

    public override async Task<CheckUsernameResponse> CheckUsername(CheckUsernameRequest request, ServerCallContext context)
    {
        if (!limiter.TryAcquire("checkname:" + context.ClientIp(), 30, TimeSpan.FromMinutes(1), out var ra))
            throw NexoException.RateLimited(ra);
        var name = UsernameRules.Normalize(request.Username);
        if (!UsernameRules.IsValid(name))
            return new CheckUsernameResponse { Available = false, Reason = ErrorReason.UsernameInvalid };
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var taken = await con.ScalarAsync<bool>("select exists(select 1 from users where username = @n::citext)", new { n = name }, ct: context.CancellationToken);
        return taken
            ? new CheckUsernameResponse { Available = false, Reason = ErrorReason.UsernameTaken }
            : new CheckUsernameResponse { Available = true };
    }

    public override async Task<GetKdfParamsResponse> GetKdfParams(GetKdfParamsRequest request, ServerCallContext context)
    {
        if (!limiter.TryAcquire("kdf:" + context.ClientIp(), 60, TimeSpan.FromMinutes(1), out var ra))
            throw NexoException.RateLimited(ra);
        var name = UsernameRules.Normalize(request.Username);
        UserRow? u = null;
        if (UsernameRules.IsValid(name))
        {
            await using var con = await ds.OpenAsync(context.CancellationToken);
            u = await con.OneAsync<UserRow>(UserSelect + " where username = @n::citext and state <> 'DELETED'", new { n = name }, ct: context.CancellationToken);
        }
        if (u is null)   // ложные, но стабильные значения (ФТ-АУТ-08)
            return new GetKdfParamsResponse { KdfSalt = ByteString.CopyFrom(FakeKdf.Salt(secrets.FakeKdfSecret, name)), Params = DefaultKdf() };
        return new GetKdfParamsResponse { KdfSalt = ByteString.CopyFrom(u.KdfSalt), Params = KdfJson.Parse(u.KdfParams) };
    }

    private static KdfParams DefaultKdf() => new() { Algorithm = "argon2id", MemoryKib = 65536, Iterations = 3, Parallelism = 1, Version = 1 };

    // ---------------- Регистрация ----------------

    public override async Task<RegisterResponse> Register(RegisterRequest r, ServerCallContext context)
    {
        var ct = context.CancellationToken; var ip = context.ClientIp();
        if (!limiter.TryAcquire("register:" + ip, 10, TimeSpan.FromHours(1), out var ra)) throw NexoException.RateLimited(ra);

        var name = UsernameRules.Normalize(r.Username);
        if (!UsernameRules.IsValid(name)) throw NexoException.Invalid(ErrorReason.UsernameInvalid);
        if (!UsernameRules.IsValidDisplayName(r.DisplayName)) throw NexoException.Invalid(ErrorReason.UsernameInvalid, "display_name");
        if (r.KdfSalt.Length != 16 || r.AuthKey.Length != 32 || r.RecoveryVerifierKey.Length != 32)
            throw NexoException.Invalid(ErrorReason.InvalidCredentials, "key sizes");
        KdfJson.Validate(r.KdfParams);
        SessionService.ValidateDevice(r.Device);
        CheckAcceptedDocuments(r.AcceptedDocuments);
        pow.Consume(r.Pow, GetPowChallengeRequest.Types.Purpose.Register);

        // Тяжёлые вычисления вне транзакции.
        var authVerifier = verifier.Hash(r.AuthKey.ToByteArray());
        var recoveryVerifier = verifier.Hash(r.RecoveryVerifierKey.ToByteArray());
        var userId = Guid.CreateVersion7();
        var ua = context.UserAgent();

        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        try
        {
            await con.ExecAsync(
                @"insert into users(id, username, display_name, locale, kdf_salt, kdf_params, auth_verifier, recovery_verifier)
                  values (@id, @n::citext, @dn, @loc, @salt, @kdf::jsonb, @av, @rv)",
                new
                {
                    id = userId, n = name, dn = r.DisplayName.Trim(), loc = r.Locale is "en" ? "en" : "ru",
                    salt = r.KdfSalt.ToByteArray(), kdf = KdfJson.Serialize(r.KdfParams), av = authVerifier, rv = recoveryVerifier,
                }, tx, ct);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            throw NexoException.Invalid(ErrorReason.UsernameTaken);
        }
        await con.ExecAsync("insert into privacy_settings(user_id) values (@u)", new { u = userId }, tx, ct);
        await con.ExecAsync("insert into user_update_counters(user_id, last_seq) values (@u, 0)", new { u = userId }, tx, ct);
        var (deviceId, _) = await sessions.CreateDeviceAsync(con, tx, userId, r.Device, ip, notify: false, ct);
        foreach (var d in r.AcceptedDocuments)
            await con.ExecAsync(
                "insert into consent_records(id, user_id, document_kind, version, ip, user_agent, device_id) values (@id, @u, @k::legal_doc_kind, @v, @ip::inet, @ua, @d)",
                new { id = Guid.CreateVersion7(), u = userId, k = LegalCatalog.DbKind(d.Kind), v = d.Version, ip = string.IsNullOrEmpty(ip) ? null : ip, ua, d = deviceId }, tx, ct);
        var tokens = await sessions.IssueAsync(con, tx, userId, deviceId, null, ct);
        await AuthEvents.AddAsync(con, userId, deviceId, "LOGIN_OK", ip, ua, tx, ct);
        await tx.CommitAsync(ct);
        return new RegisterResponse { UserId = userId.ToString(), Tokens = tokens };
    }

    private static void CheckAcceptedDocuments(IEnumerable<AcceptedDocument> accepted)
    {
        var list = accepted.ToList();
        foreach (var d in LegalCatalog.Current.Where(x => x.Mandatory))
            if (!list.Any(a => a.Kind == d.Kind && a.Version == d.Version))
                throw NexoException.Invalid(ErrorReason.DocumentsNotAccepted);
    }

    // ---------------- Вход ----------------

    public override async Task<LoginResponse> Login(LoginRequest r, ServerCallContext context)
    {
        var ct = context.CancellationToken; var ip = context.ClientIp(); var ua = context.UserAgent();
        var name = UsernameRules.Normalize(r.Username);
        if (r.AuthKey.Length != 32) throw NexoException.Invalid(ErrorReason.InvalidCredentials);
        SessionService.ValidateDevice(r.Device);

        if (!limiter.TryAcquire("login:ip:" + ip, LoginPolicy.MaxAttemptsPerIp, LoginPolicy.IpWindow, out var ra))
            throw NexoException.RateLimited(ra);
        var failKey = "login:fail:" + name;
        var fails = limiter.Count(failKey, LoginPolicy.UserWindow);
        if (fails >= LoginPolicy.MaxFailuresPerUser)
            throw new NexoException(ErrorReason.AccountLocked, StatusCode.ResourceExhausted, null, (int)LoginPolicy.UserWindow.TotalSeconds);
        if (fails >= LoginPolicy.FailuresBeforePow)
            pow.Consume(r.Pow, GetPowChallengeRequest.Types.Purpose.Login);   // POW_REQUIRED, если решения нет

        await using var con = await ds.OpenAsync(ct);
        var user = UsernameRules.IsValid(name)
            ? await con.OneAsync<UserRow>(UserSelect + " where username = @n::citext and state <> 'DELETED'", new { n = name }, ct: ct) : null;

        bool ok;
        if (user is null) { verifier.VerifyDummy(r.AuthKey.ToByteArray()); ok = false; }
        else ok = verifier.Verify(r.AuthKey.ToByteArray(), user.AuthVerifier);
        if (!ok)
        {
            limiter.TryAcquire(failKey, int.MaxValue, LoginPolicy.UserWindow, out _);   // фиксируем неудачу
            if (user is not null) await AuthEvents.AddAsync(con, user.Id, null, "LOGIN_FAILED", ip, ua, ct: ct);
            throw NexoException.Unauthenticated(ErrorReason.InvalidCredentials);
        }
        limiter.Reset(failKey);
        if (user!.State != "ACTIVE") throw NexoException.Denied(ErrorReason.AccountSuspended);
        if (user.TotpEnabled) throw new NexoException(ErrorReason.SecondFactorRequired, StatusCode.Unimplemented, "TOTP – этап Э2");

        var pubkey = r.Device.DevicePubkey.ToByteArray();
        var docs = await PendingDocumentsAsync(con, user.Id, ct);

        // Известное устройство – вход сразу.
        var existing = await con.OneAsync<Guid?>(
            "select id from devices where user_id = @u and device_pubkey = @p and revoked_at is null", new { u = user.Id, p = pubkey }, ct: ct);
        if (existing is { } deviceId)
        {
            await using var tx = await con.BeginTransactionAsync(ct);
            await con.ExecAsync("update devices set last_seen_at = now(), last_ip = @ip::inet, app_version = @av where id = @d",
                new { d = deviceId, ip = string.IsNullOrEmpty(ip) ? null : ip, av = r.Device.AppVersion }, tx, ct);
            var tokens = await sessions.IssueAsync(con, tx, user.Id, deviceId, null, ct);
            await AuthEvents.AddAsync(con, user.Id, deviceId, "LOGIN_OK", ip, ua, tx, ct);
            await tx.CommitAsync(ct);
            return Result(tokens, docs);
        }

        var others = await con.ScalarAsync<int>(@"select count(*) from devices d where d.user_id = @u and d.revoked_at is null
              and exists (select 1 from sessions s where s.device_id = d.id and s.revoked_at is null and s.expires_at > now())", new { u = user.Id }, ct: ct);
        if (others == 0)   // единственное устройство: достаточно пароля (документ 81 02)
        {
            await using var tx = await con.BeginTransactionAsync(ct);
            var (dev, pending) = await sessions.CreateDeviceAsync(con, tx, user.Id, r.Device, ip, notify: false, ct);
            var tokens = await sessions.IssueAsync(con, tx, user.Id, dev, null, ct);
            await AuthEvents.AddAsync(con, user.Id, dev, "LOGIN_OK", ip, ua, tx, ct);
            await tx.CommitAsync(ct);
            journal.Publish(pending);
            return Result(tokens, docs);
        }

        // Новое устройство при наличии других: нужно подтверждение (ФТ-УСТ-02).
        var approvalId = Guid.CreateVersion7();
        var code = Tokens.NewNumericCode();
        var expires = DateTime.UtcNow.AddMinutes(5);
        await con.ExecAsync(
            @"insert into login_approvals(id, user_id, requester_pubkey, requester_name, requester_platform, requester_ip, code, expires_at)
              values (@id, @u, @pk, @n, @pl::device_platform, @ip::inet, @c, @e)",
            new
            {
                id = approvalId, u = user.Id, pk = pubkey, n = r.Device.Name.Trim(), pl = SessionService.PlatformText(r.Device.Platform),
                ip = string.IsNullOrEmpty(ip) ? null : ip, c = code, e = expires,
            }, ct: ct);
        await AuthEvents.AddAsync(con, user.Id, null, "LOGIN_APPROVAL_REQUESTED", ip, ua, ct: ct);
        var approval = DeviceServiceImpl.BuildApproval(approvalId, r.Device.Name.Trim(), r.Device.Platform, ip, code, ApprovalStatus.Pending, DateTime.UtcNow, expires, pubkey);
        journal.PublishEphemeral(user.Id, u => u.LoginApproval = new LoginApprovalUpdate { Approval = approval });
        return new LoginResponse
        {
            Approval = new ApprovalRequired { ApprovalId = approvalId.ToString(), Code = code, ExpiresAt = Timestamp.FromDateTime(expires) },
            DocumentsToAccept = { docs },
        };
    }

    private static LoginResponse Result(TokenPair t, List<LegalDocKind> docs) => new() { Tokens = t, DocumentsToAccept = { docs } };

    internal static async Task<List<LegalDocKind>> PendingDocumentsAsync(DbConnection con, Guid userId, CancellationToken ct)
    {
        var have = await con.ListAsync<(string K, string V)>(
            "select document_kind::text as K, version as V from consent_records where user_id = @u", new { u = userId }, ct: ct);
        return LegalCatalog.Current.Where(d => d.Mandatory && !have.Any(h => h.K == d.DbKind && h.V == d.Version)).Select(d => d.Kind).ToList();
    }

    public override async Task WatchLoginApproval(WatchLoginApprovalRequest request, IServerStreamWriter<LoginApprovalEvent> stream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ApprovalId, out var id)) throw NexoException.NotFound();
        if (!limiter.TryAcquire("watch:" + context.ClientIp(), 20, TimeSpan.FromMinutes(1), out var ra)) throw NexoException.RateLimited(ra);
        while (!ct.IsCancellationRequested)
        {
            await using var con = await ds.OpenAsync(ct);
            var a = await con.OneAsync<ApprovalRow>(
                "select id, user_id, requester_pubkey, status::text as status, expires_at from login_approvals where id = @id", new { id }, ct: ct);
            if (a is null || !a.RequesterPubkey.AsSpan().SequenceEqual(request.DevicePubkey.Span)) throw NexoException.NotFound();

            if (a.Status == "PENDING" && a.ExpiresAt <= DateTime.UtcNow)
            {
                await con.ExecAsync("update login_approvals set status = 'EXPIRED', resolved_at = now() where id = @id and status = 'PENDING'", new { id }, ct: ct);
                a.Status = "EXPIRED";
            }
            switch (a.Status)
            {
                case "DENIED": await stream.WriteAsync(new LoginApprovalEvent { Status = ApprovalStatus.Denied }, ct); return;
                case "EXPIRED": await stream.WriteAsync(new LoginApprovalEvent { Status = ApprovalStatus.Expired }, ct); return;
                case "APPROVED":
                    await stream.WriteAsync(await IssueForApprovedAsync(con, a, context, ct), ct);
                    return;
            }
            await Task.Delay(1000, ct);
        }
    }

    private sealed class ApprovalRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public byte[] RequesterPubkey { get; set; } = [];
        public string Status { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }

    private async Task<LoginApprovalEvent> IssueForApprovedAsync(DbConnection con, ApprovalRow a, ServerCallContext context, CancellationToken ct)
    {
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecAsync("select 1 from login_approvals where id = @id for update", new { id = a.Id }, tx, ct);
        var dev = await con.OneAsync<Guid?>("select id from devices where user_id = @u and device_pubkey = @p and revoked_at is null",
            new { u = a.UserId, p = a.RequesterPubkey }, tx, ct);
        if (dev is null) { await tx.RollbackAsync(ct); return new LoginApprovalEvent { Status = ApprovalStatus.Denied }; }
        var hasSession = await con.ScalarAsync<bool>("select exists(select 1 from sessions where device_id = @d)", new { d = dev.Value }, tx, ct);
        TokenPair? tokens = hasSession ? null : await sessions.IssueAsync(con, tx, a.UserId, dev.Value, null, ct);
        await tx.CommitAsync(ct);
        // Если сессия уже выдана ранее, клиент повторяет Login: устройство известно, вход пройдёт сразу.
        return new LoginApprovalEvent { Status = ApprovalStatus.Approved, Tokens = tokens };
    }

    // ---------------- Обновление токена, выход ----------------

    public override async Task<TokenPair> RefreshToken(RefreshTokenRequest r, ServerCallContext context)
    {
        var ct = context.CancellationToken; var ip = context.ClientIp();
        if (!limiter.TryAcquire("refresh:" + ip, 60, TimeSpan.FromMinutes(1), out var ra)) throw NexoException.RateLimited(ra);
        if (!Guid.TryParse(r.DeviceId, out var deviceId) || string.IsNullOrEmpty(r.RefreshToken) || r.Signature.Length != 64 || r.Timestamp is null)
            throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);
        var ts = r.Timestamp.ToDateTimeOffset();
        if (Math.Abs((DateTimeOffset.UtcNow - ts).TotalSeconds) > 60) throw NexoException.Unauthenticated(ErrorReason.TokenExpired);

        await using var con = await ds.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);
        var s = await con.OneAsync<SessionRow>(
            @"select id, user_id, device_id, family_id, expires_at, rotated_at, revoked_at
              from sessions where refresh_hash = @h for update", new { h = Tokens.Hash(r.RefreshToken) }, tx, ct);
        if (s is null || s.DeviceId != deviceId) throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);

        if (s.RotatedAt is not null)   // повторное предъявление уже использованного токена – вероятная кража
        {
            await sessions.RevokeFamilyAsync(con, tx, s.Id, "REUSE_DETECTED", ct);
            await AuthEvents.AddAsync(con, s.UserId, s.DeviceId, "LOGIN_FAILED", ip, context.UserAgent(), tx, ct);
            await tx.CommitAsync(ct);
            throw NexoException.Unauthenticated(ErrorReason.RefreshReuseDetected);
        }
        if (s.RevokedAt is not null || s.ExpiresAt <= DateTime.UtcNow) throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);

        var dev = await con.OneAsync<byte[]>("select device_pubkey from devices where id = @d and revoked_at is null", new { d = deviceId }, tx, ct);
        if (dev is null) throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);
        var message = DeviceSignatures.RefreshMessage(r.RefreshToken, r.DeviceId, ts.ToUnixTimeSeconds());
        if (!Ed25519Ok(dev, message, r.Signature.Span)) throw NexoException.Unauthenticated(ErrorReason.TokenRevoked);

        await con.ExecAsync("update sessions set rotated_at = now() where id = @id", new { id = s.Id }, tx, ct);
        var pair = await sessions.IssueAsync(con, tx, s.UserId, deviceId, s.FamilyId, ct);
        await con.ExecAsync("update devices set last_seen_at = now(), last_ip = @ip::inet where id = @d",
            new { d = deviceId, ip = string.IsNullOrEmpty(ip) ? null : ip }, tx, ct);
        await tx.CommitAsync(ct);
        return pair;
    }

    private sealed class SessionRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid DeviceId { get; set; }
        public Guid FamilyId { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RotatedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
    }

    internal static bool Ed25519Ok(byte[] publicKey, byte[] message, ReadOnlySpan<byte> signature)
    {
        try
        {
            var pk = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(pk, message, signature);
        }
        catch { return false; }
    }

    public override async Task<Empty> Logout(Empty request, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        await using var con = await ds.OpenAsync(ct);
        await sessions.RevokeFamilyAsync(con, null, a.SessionId, "LOGOUT", ct);
        // Выход стирает локальные данные и ключ устройства, поэтому устройство больше не может подтверждать входы: отзываем его.
        await sessions.RevokeDeviceSessionsAsync(con, null, a.DeviceId, "LOGOUT", ct);
        await con.ExecAsync("update devices set revoked_at = now() where id = @d and user_id = @u and revoked_at is null", new { d = a.DeviceId, u = a.UserId }, ct: ct);
        await AuthEvents.AddAsync(con, a.UserId, a.DeviceId, "LOGOUT", context.ClientIp(), context.UserAgent(), ct: ct);
        guard.Forget(a.SessionId);
        return new Empty();
    }

    // ---------------- Пароль и восстановление ----------------

    public override async Task<TokenPair> ChangePassword(ChangePasswordRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        if (!limiter.TryAcquire("chpass:" + a.UserId, 10, TimeSpan.FromHours(1), out var ra)) throw NexoException.RateLimited(ra);
        if (r.NewKdfSalt.Length != 16 || r.NewAuthKey.Length != 32 || r.CurrentAuthKey.Length != 32) throw NexoException.Invalid(ErrorReason.InvalidCredentials);
        KdfJson.Validate(r.NewKdfParams);

        await using var con = await ds.OpenAsync(ct);
        var current = await con.ScalarAsync<string>("select auth_verifier from users where id = @u", new { u = a.UserId }, ct: ct);
        if (current is null || !verifier.Verify(r.CurrentAuthKey.ToByteArray(), current))
            throw NexoException.Unauthenticated(ErrorReason.InvalidCredentials);
        var newVerifier = verifier.Hash(r.NewAuthKey.ToByteArray());

        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecAsync("update users set kdf_salt = @s, kdf_params = @p::jsonb, auth_verifier = @v, updated_at = now() where id = @u",
            new { u = a.UserId, s = r.NewKdfSalt.ToByteArray(), p = KdfJson.Serialize(r.NewKdfParams), v = newVerifier }, tx, ct);
        if (r.RevokeOtherSessions) await sessions.RevokeUserSessionsAsync(con, tx, a.UserId, a.SessionId, "PASSWORD_CHANGED", ct);
        var family = await con.ScalarAsync<Guid>("select family_id from sessions where id = @s", new { s = a.SessionId }, tx, ct);
        await con.ExecAsync("update sessions set rotated_at = now() where id = @s", new { s = a.SessionId }, tx, ct);
        var pair = await sessions.IssueAsync(con, tx, a.UserId, a.DeviceId, family, ct);
        await AuthEvents.AddAsync(con, a.UserId, a.DeviceId, "PASSWORD_CHANGED", context.ClientIp(), context.UserAgent(), tx, ct);
        await tx.CommitAsync(ct);
        return pair;
    }

    public override async Task<RecoverAccountResponse> RecoverAccount(RecoverAccountRequest r, ServerCallContext context)
    {
        var ct = context.CancellationToken; var ip = context.ClientIp(); var ua = context.UserAgent();
        if (!limiter.TryAcquire("recover:" + ip, 5, TimeSpan.FromHours(1), out var ra)) throw NexoException.RateLimited(ra);
        var name = UsernameRules.Normalize(r.Username);
        if (r.RecoveryVerifierKey.Length != 32 || r.NewAuthKey.Length != 32 || r.NewKdfSalt.Length != 16 || r.NewRecoveryVerifierKey.Length != 32)
            throw NexoException.Invalid(ErrorReason.InvalidCredentials);
        KdfJson.Validate(r.NewKdfParams);
        SessionService.ValidateDevice(r.Device);
        pow.Consume(r.Pow, GetPowChallengeRequest.Types.Purpose.Recover);

        await using var con = await ds.OpenAsync(ct);
        var user = UsernameRules.IsValid(name)
            ? await con.OneAsync<UserRow>(UserSelect + " where username = @n::citext and state = 'ACTIVE'", new { n = name }, ct: ct) : null;
        bool ok;
        if (user is null) { verifier.VerifyDummy(r.RecoveryVerifierKey.ToByteArray()); ok = false; }
        else ok = verifier.Verify(r.RecoveryVerifierKey.ToByteArray(), user.RecoveryVerifier);
        if (!ok) throw NexoException.Unauthenticated(ErrorReason.InvalidCredentials);
        if (user!.TotpEnabled)   // второй фактор – этап Э2
            throw new NexoException(ErrorReason.SecondFactorRequired, StatusCode.Unimplemented, "TOTP – этап Э2");

        var newAuth = verifier.Hash(r.NewAuthKey.ToByteArray());
        var newRecovery = verifier.Hash(r.NewRecoveryVerifierKey.ToByteArray());
        await using var tx = await con.BeginTransactionAsync(ct);
        await con.ExecAsync(
            @"update users set kdf_salt = @s, kdf_params = @p::jsonb, auth_verifier = @v, recovery_verifier = @rv,
                               recovery_set_at = now(), updated_at = now() where id = @u",
            new { u = user.Id, s = r.NewKdfSalt.ToByteArray(), p = KdfJson.Serialize(r.NewKdfParams), v = newAuth, rv = newRecovery }, tx, ct);
        await sessions.RevokeUserSessionsAsync(con, tx, user.Id, null, "PASSWORD_CHANGED", ct);
        // Прежние устройства отзываются: восстановление означает потерю доверия к ним.
        await con.ExecAsync("update devices set revoked_at = now() where user_id = @u and revoked_at is null", new { u = user.Id }, tx, ct);
        var (deviceId, _) = await sessions.CreateDeviceAsync(con, tx, user.Id, r.Device, ip, notify: false, ct);
        var pair = await sessions.IssueAsync(con, tx, user.Id, deviceId, null, ct);
        await AuthEvents.AddAsync(con, user.Id, deviceId, "RECOVERY_KEY_USED", ip, ua, tx, ct);
        await tx.CommitAsync(ct);
        return new RecoverAccountResponse { Tokens = pair };
    }

    public override async Task<Empty> RegenerateRecoveryKey(RegenerateRecoveryKeyRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        if (r.AuthKey.Length != 32 || r.NewRecoveryVerifierKey.Length != 32) throw NexoException.Invalid(ErrorReason.InvalidCredentials);
        await using var con = await ds.OpenAsync(ct);
        var current = await con.ScalarAsync<string>("select auth_verifier from users where id = @u", new { u = a.UserId }, ct: ct);
        if (current is null || !verifier.Verify(r.AuthKey.ToByteArray(), current)) throw NexoException.Unauthenticated(ErrorReason.InvalidCredentials);
        var rv = verifier.Hash(r.NewRecoveryVerifierKey.ToByteArray());
        await con.ExecAsync("update users set recovery_verifier = @v, recovery_set_at = now() where id = @u", new { u = a.UserId, v = rv }, ct: ct);
        await AuthEvents.AddAsync(con, a.UserId, a.DeviceId, "RECOVERY_KEY_REGENERATED", context.ClientIp(), context.UserAgent(), ct: ct);
        return new Empty();
    }
}

internal static class KdfJson
{
    public static string Serialize(KdfParams p) => JsonSerializer.Serialize(new
    {
        algorithm = "argon2id", memory_kib = p.MemoryKib, iterations = p.Iterations, parallelism = p.Parallelism, version = p.Version,
    });

    public static KdfParams Parse(string json)
    {
        var e = JsonDocument.Parse(json).RootElement;
        return new KdfParams
        {
            Algorithm = e.GetProperty("algorithm").GetString() ?? "argon2id",
            MemoryKib = e.GetProperty("memory_kib").GetUInt32(), Iterations = e.GetProperty("iterations").GetUInt32(),
            Parallelism = e.GetProperty("parallelism").GetUInt32(), Version = e.GetProperty("version").GetUInt32(),
        };
    }

    /// <summary>Допустимые границы параметров клиента (не слабее минимума OWASP, не настолько велики, чтобы вывести устройство из строя).</summary>
    public static void Validate(KdfParams? p)
    {
        if (p is null || p.Algorithm != "argon2id" || p.MemoryKib is < 19456 or > 1_048_576 || p.Iterations is < 2 or > 20 || p.Parallelism is < 1 or > 8)
            throw NexoException.Invalid(ErrorReason.InvalidCredentials, "kdf_params");
    }
}
