using System.ComponentModel.DataAnnotations;

namespace AICOS.BuildingBlocks.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "aicos";

    [Required]
    public string Audience { get; set; } = "aicos-clients";

    /// <summary>HMAC-SHA256 key shared by all services (>= 32 chars). Supply via env var <c>Jwt__SigningKey</c> outside Development.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int ExpiryMinutes { get; set; } = 60;
}
