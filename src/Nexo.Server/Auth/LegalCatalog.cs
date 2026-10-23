using Google.Protobuf.WellKnownTypes;
using Nexo.Protocol.V1;

namespace Nexo.Server.Auth;

/// <summary>Действующие редакции юридических документов (тексты – в Документы/10…13 и на сайте).</summary>
public static class LegalCatalog
{
    public sealed record Doc(LegalDocKind Kind, string DbKind, string Version, string Title, bool Mandatory);

    public static readonly IReadOnlyList<Doc> Current =
    [
        new(LegalDocKind.Terms, "TERMS", "1.0", "Пользовательское соглашение", true),
        new(LegalDocKind.Privacy, "PRIVACY", "1.0", "Политика конфиденциальности", true),
        new(LegalDocKind.PdConsent, "PD_CONSENT", "1.0", "Согласие на обработку персональных данных", true),
        new(LegalDocKind.CommunityRules, "COMMUNITY_RULES", "1.0", "Правила сообщества", false),
    ];

    public static string DbKind(LegalDocKind k) =>
        Current.FirstOrDefault(d => d.Kind == k)?.DbKind ?? throw new ArgumentException("kind");

    public static LegalDocument ToProto(Doc d) => new()
    {
        Kind = d.Kind, Version = d.Version, Title = d.Title, Mandatory = d.Mandatory,
        Url = "/legal/" + d.DbKind.ToLowerInvariant(),
        PublishedAt = Timestamp.FromDateTime(new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc)),
    };
}
