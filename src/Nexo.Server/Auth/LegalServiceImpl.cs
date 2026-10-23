using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;
using Npgsql;

namespace Nexo.Server.Auth;

/// <summary>Юридические документы и фиксация принятия (ФТ-ПРА-01, ФТ-ПРА-02).</summary>
public sealed class LegalServiceImpl(NpgsqlDataSource ds) : LegalService.LegalServiceBase
{
    public override Task<GetLegalDocumentsResponse> GetLegalDocuments(Empty request, ServerCallContext context)
    {
        var res = new GetLegalDocumentsResponse();
        foreach (var d in LegalCatalog.Current) res.Documents.Add(LegalCatalog.ToProto(d));
        return Task.FromResult(res);
    }

    public override async Task<Empty> AcceptDocuments(AcceptDocumentsRequest r, ServerCallContext context)
    {
        var a = context.Auth(); var ct = context.CancellationToken;
        var ip = context.ClientIp(); var ua = context.UserAgent();
        await using var con = await ds.OpenAsync(ct);
        foreach (var d in r.Documents)
        {
            var cur = LegalCatalog.Current.FirstOrDefault(x => x.Kind == d.Kind && x.Version == d.Version)
                      ?? throw NexoException.Invalid(ErrorReason.DocumentsNotAccepted, "version");
            await con.ExecAsync(
                "insert into consent_records(id, user_id, document_kind, version, ip, user_agent, device_id) values (@id, @u, @k::legal_doc_kind, @v, @ip::inet, @ua, @d)",
                new { id = Guid.CreateVersion7(), u = a.UserId, k = cur.DbKind, v = cur.Version, ip = string.IsNullOrEmpty(ip) ? null : ip, ua, d = a.DeviceId }, ct: ct);
        }
        return new Empty();
    }

    public override async Task<GetConsentStatusResponse> GetConsentStatus(Empty request, ServerCallContext context)
    {
        var a = context.Auth();
        await using var con = await ds.OpenAsync(context.CancellationToken);
        var pending = await AuthServiceImpl.PendingDocumentsAsync(con, a.UserId, context.CancellationToken);
        var res = new GetConsentStatusResponse();
        foreach (var d in LegalCatalog.Current.Where(x => pending.Contains(x.Kind))) res.Pending.Add(LegalCatalog.ToProto(d));
        return res;
    }
}
