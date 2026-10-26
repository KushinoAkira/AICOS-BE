using AICOS.BuildingBlocks.Domain;

namespace AICOS.IdentityOrganization.Api.Domain;

public enum ContactMessageStatus
{
    New,
    Handled,
}

/// <summary>A message from the public contact form of the landing page. Kept in the database for the Super Admin to read and close.</summary>
public sealed class ContactMessage : AuditableEntity<Guid>
{
    public const int NameMaxLength = 120;
    public const int SubjectMaxLength = 150;
    public const int MessageMaxLength = 2000;

    private ContactMessage()
    {
    }

    public ContactMessage(string fullName, string email, string? phone, string? subject, string message)
    {
        Id = Guid.NewGuid();
        FullName = fullName.Trim();
        Email = email.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        Message = message.Trim();
        Status = ContactMessageStatus.New;
    }

    public string FullName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Subject { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public ContactMessageStatus Status { get; private set; }

    public DateTime? HandledAtUtc { get; private set; }

    public Guid? HandledBy { get; private set; }

    public void MarkHandled(Guid? by, DateTime now)
    {
        if (Status == ContactMessageStatus.Handled)
        {
            return;
        }

        Status = ContactMessageStatus.Handled;
        HandledAtUtc = now;
        HandledBy = by;
    }
}
