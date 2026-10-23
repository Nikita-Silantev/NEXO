using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Nexo.Protocol.V1;

namespace Nexo.Server.Messages;

public sealed class MessageRow
{
    public Guid Id { get; set; }
    public Guid ChatId { get; set; }
    public long Seq { get; set; }
    public Guid SenderId { get; set; }
    public Guid? SenderDeviceId { get; set; }
    public Guid ClientMsgId { get; set; }
    public string Kind { get; set; } = "TEXT";
    public string? Body { get; set; }
    public string? Entities { get; set; }
    public Guid? ReplyToId { get; set; }
    public string? ForwardOrigin { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? TtlSeconds { get; set; }
    public DateTime? FirstReadAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? PinnedAt { get; set; }

    public const string Select =
        @"select id, chat_id, seq, sender_id, sender_device_id, client_msg_id, kind::text as kind, body, entities::text as entities,
                 reply_to_id, forward_origin::text as forward_origin, created_at, edited_at, deleted_at, ttl_seconds,
                 first_read_at, expires_at, pinned_at from messages";
}

/// <summary>Преобразование строк БД в сообщения протокола и обратно (формат сущностей и пересылки – JSON).</summary>
public static class MessageMapper
{
    private static Timestamp Ts(DateTime d) => Timestamp.FromDateTime(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    public static Message ToProto(MessageRow r)
    {
        var m = new Message
        {
            MessageId = r.Id.ToString(), ChatId = r.ChatId.ToString(), Seq = r.Seq, SenderId = r.SenderId.ToString(),
            SenderDeviceId = r.SenderDeviceId?.ToString() ?? "", ClientMsgId = r.ClientMsgId.ToString(),
            ReplyToId = r.ReplyToId?.ToString() ?? "", CreatedAt = Ts(r.CreatedAt),
            EditedAt = r.EditedAt is { } e ? Ts(e) : null, Deleted = r.DeletedAt is not null,
            TtlSeconds = r.TtlSeconds ?? 0, ExpiresAt = r.ExpiresAt is { } x ? Ts(x) : null,
            FirstReadAt = r.FirstReadAt is { } f ? Ts(f) : null, Pinned = r.PinnedAt is not null,
        };
        if (r.ForwardOrigin is not null) m.ForwardOrigin = ParseForward(r.ForwardOrigin);
        if (r.DeletedAt is null && r.Kind == "TEXT")
        {
            var t = new TextContent { Text = r.Body ?? "" };
            t.Entities.AddRange(ParseEntities(r.Entities));
            m.Content = new MessageContent { Text = t };
        }
        return m;
    }

    public static string? SerializeEntities(IEnumerable<TextEntity> entities)
    {
        var list = entities.Select(e => new { kind = (int)e.Kind, offset = e.Offset, length = e.Length, url = e.Url, user_id = e.UserId }).ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    public static IEnumerable<TextEntity> ParseEntities(string? json)
    {
        if (string.IsNullOrEmpty(json)) yield break;
        foreach (var el in JsonDocument.Parse(json).RootElement.EnumerateArray())
            yield return new TextEntity
            {
                Kind = (TextEntity.Types.Kind)el.GetProperty("kind").GetInt32(), Offset = el.GetProperty("offset").GetInt32(),
                Length = el.GetProperty("length").GetInt32(), Url = el.GetProperty("url").GetString() ?? "",
                UserId = el.GetProperty("user_id").GetString() ?? "",
            };
    }

    public static string SerializeForward(ForwardOrigin f) => JsonSerializer.Serialize(new
    {
        from_user_id = f.FromUserId, from_chat_id = f.FromChatId, from_message_id = f.FromMessageId,
        original_date = f.OriginalDate?.ToDateTime().ToString("O"), hidden_name = f.HiddenName,
    });

    private static ForwardOrigin ParseForward(string json)
    {
        var e = JsonDocument.Parse(json).RootElement;
        var f = new ForwardOrigin
        {
            FromUserId = e.GetProperty("from_user_id").GetString() ?? "", FromChatId = e.GetProperty("from_chat_id").GetString() ?? "",
            FromMessageId = e.GetProperty("from_message_id").GetString() ?? "", HiddenName = e.GetProperty("hidden_name").GetString() ?? "",
        };
        if (e.TryGetProperty("original_date", out var d) && d.ValueKind == JsonValueKind.String
            && DateTime.TryParse(d.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            f.OriginalDate = Timestamp.FromDateTime(dt.ToUniversalTime());
        return f;
    }
}
