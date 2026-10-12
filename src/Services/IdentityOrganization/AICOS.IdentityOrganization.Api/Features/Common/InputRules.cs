using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Validation;

namespace AICOS.IdentityOrganization.Api.Features.Common;

/// <summary>Identity-specific input rules on top of <see cref="CommonRules"/> (username and password policy).</summary>
internal static partial class InputRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 100;

    /// <summary>Optional: 3-50 chars of letters, digits, '.', '_' ; must start with a letter; cannot contain '@'.</summary>
    public static void Username(ValidationErrors errors, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !UsernameRegex().IsMatch(value.Trim()))
        {
            errors.Add(field, "Username must be 3-50 characters, start with a letter and contain only letters, digits, '.' or '_'.");
        }
    }

    /// <summary>8-100 characters with at least one letter and one digit; confirmation must match.</summary>
    public static void NewPassword(ValidationErrors errors, string field, string? password, string confirmField, string? confirmation)
    {
        if (string.IsNullOrEmpty(password))
        {
            errors.Add(field, "Password is required.");
        }
        else
        {
            if (password.Length is < PasswordMinLength or > PasswordMaxLength)
            {
                errors.Add(field, $"Password must be {PasswordMinLength}-{PasswordMaxLength} characters.");
            }

            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            {
                errors.Add(field, "Password must contain at least one letter and one digit.");
            }
        }

        if (password != confirmation)
        {
            errors.Add(confirmField, "Password confirmation does not match.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._]{2,49}$")]
    private static partial Regex UsernameRegex();
}
