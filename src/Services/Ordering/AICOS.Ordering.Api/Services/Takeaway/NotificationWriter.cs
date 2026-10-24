using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using System.Text.Json;

namespace AICOS.Ordering.Api.Services.Takeaway;

public static class NotificationWriter
{
    public static void Add(
        OrderingDbContext db,
        Guid userId,
        string type,
        string title,
        string body,
        object? data = null)
    {
        db.Notifications.Add(new NotificationEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            Body = body,
            DataJson = data is null ? null : JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow
        });
    }
}
