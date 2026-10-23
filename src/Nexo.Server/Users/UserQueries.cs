using System.Data.Common;
using Google.Protobuf.WellKnownTypes;
using Nexo.Protocol.V1;
using Nexo.Server.Infrastructure;

namespace Nexo.Server.Users;

/// <summary>Сборка профилей с учётом настроек приватности (ФТ-ЧАТ-07, ФТ-ПРИ-*).</summary>
public sealed class UserQueries
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Bio { get; set; }
        public Guid? AvatarFileId { get; set; }
        public string Role { get; set; } = "USER";
        public string BioVis { get; set; } = "EVERYONE";
        public string AvatarVis { get; set; } = "EVERYONE";
        public string? ContactStatus { get; set; }
        public Guid? ContactRequester { get; set; }
        public bool BlockedByMe { get; set; }
    }

    public static ContactStatus ParseContact(string? s) => s switch
    {
        "PENDING" => ContactStatus.Pending, "ACCEPTED" => ContactStatus.Accepted, "DECLINED" => ContactStatus.Declined,
        _ => ContactStatus.Unspecified,
    };

    private static bool Visible(string vis, bool self, bool accepted) =>
        self || vis == "EVERYONE" || (vis == "CONTACTS" && accepted);

    public async Task<Dictionary<Guid, UserProfile>> ProfilesAsync(DbConnection con, Guid viewer, IReadOnlyCollection<Guid> ids,
        CancellationToken ct, DbTransaction? tx = null)
    {
        var result = new Dictionary<Guid, UserProfile>();
        if (ids.Count == 0) return result;
        var rows = await con.ListAsync<Row>(
            @"select u.id, u.username::text as username, u.display_name, u.bio, u.avatar_file_id, u.role::text as role,
                     p.bio_vis::text as bio_vis, p.avatar_vis::text as avatar_vis,
                     cl.status::text as contact_status, cl.requester_id as contact_requester,
                     exists(select 1 from user_blocks b where b.blocker_id = @v and b.blocked_id = u.id) as blocked_by_me
              from users u
              join privacy_settings p on p.user_id = u.id
              left join contact_links cl on (cl.requester_id = @v and cl.addressee_id = u.id) or (cl.requester_id = u.id and cl.addressee_id = @v)
              where u.id = any(@ids)",
            new { v = viewer, ids = ids.ToArray() }, tx, ct);
        foreach (var r in rows)
        {
            var self = r.Id == viewer;
            var accepted = r.ContactStatus == "ACCEPTED";
            var p = new UserProfile
            {
                UserId = r.Id.ToString(), Username = r.Username, DisplayName = r.DisplayName,
                ContactStatus = ParseContact(r.ContactStatus), IsBlockedByMe = r.BlockedByMe, IsSelf = self,
                Role = r.Role switch { "ADMIN" => UserRole.Admin, "MODERATOR" => UserRole.Moderator, _ => UserRole.User },
            };
            if (Visible(r.BioVis, self, accepted)) p.Bio = r.Bio ?? "";
            if (Visible(r.AvatarVis, self, accepted) && r.AvatarFileId is { } f) p.AvatarFileId = f.ToString();
            result[r.Id] = p;
        }
        return result;
    }

    public async Task<UserProfile?> ProfileAsync(DbConnection con, Guid viewer, Guid id, CancellationToken ct)
        => (await ProfilesAsync(con, viewer, [id], ct)).GetValueOrDefault(id);

    public static Timestamp Ts(DateTime d) => Timestamp.FromDateTime(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
