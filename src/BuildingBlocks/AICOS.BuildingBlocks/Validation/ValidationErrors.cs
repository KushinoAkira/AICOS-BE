using System.Text.RegularExpressions;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.BuildingBlocks.Validation;

/// <summary>Collects field errors and throws one <see cref="ValidationFailedException"/> (400) listing all of them.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public bool HasAny => _errors.Count > 0;

    public bool Has(string field) => _errors.ContainsKey(field);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (HasAny)
        {
            throw new ValidationFailedException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}

/// <summary>Field rules used by every service, so e.g. "valid phone" means the same thing everywhere.</summary>
public static partial class CommonRules
{
    public static void Required(ValidationErrors errors, string field, string? value, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(field, $"{label} is required.");
        }
        else
        {
            MaxLength(errors, field, value, label, maxLength);
        }
    }

    public static void MaxLength(ValidationErrors errors, string field, string? value, string label, int maxLength)
    {
        if (value?.Trim().Length > maxLength)
        {
            errors.Add(field, $"{label} must be at most {maxLength} characters.");
        }
    }

    public static void RequiredId(ValidationErrors errors, string field, Guid? value, string label)
    {
        if (value is null || value == Guid.Empty)
        {
            errors.Add(field, $"{label} is required.");
        }
    }

    /// <summary>Optional: 8-15 digits, may start with '+'. No spaces or dashes.</summary>
    public static void Phone(ValidationErrors errors, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !PhoneRegex().IsMatch(value.Trim()))
        {
            errors.Add(field, "Phone must contain 8-15 digits and may start with '+'.");
        }
    }

    public static void OptionalEmail(ValidationErrors errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        MaxLength(errors, field, value, "Email", 255);
        if (!EmailRegex().IsMatch(value.Trim()))
        {
            errors.Add(field, "Email is not a valid email address.");
        }
    }

    public static void Email(ValidationErrors errors, string field, string? value)
    {
        Required(errors, field, value, "Email", 255);
        if (!errors.Has(field) && !EmailRegex().IsMatch(value!.Trim()))
        {
            errors.Add(field, "Email is not a valid email address.");
        }
    }

    public static void OptionalUrl(ValidationErrors errors, string field, string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        MaxLength(errors, field, value, label, 1000);
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            errors.Add(field, $"{label} must be an absolute http(s) URL.");
        }
    }

    public static void Positive(ValidationErrors errors, string field, decimal? value, string label, decimal max = 1_000_000_000m)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
        }
        else if (value <= 0 || value > max)
        {
            errors.Add(field, $"{label} must be greater than 0 and at most {max:0.##}.");
        }
    }

    public static void NonNegative(ValidationErrors errors, string field, decimal? value, string label, decimal max = 1_000_000_000m)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
        }
        else if (value < 0 || value > max)
        {
            errors.Add(field, $"{label} must be between 0 and {max:0.##}.");
        }
    }

    public static void Range(ValidationErrors errors, string field, int? value, string label, int min, int max)
    {
        if (value is null)
        {
            errors.Add(field, $"{label} is required.");
        }
        else if (value < min || value > max)
        {
            errors.Add(field, $"{label} must be between {min} and {max}.");
        }
    }

    /// <summary>Enum given as text in JSON: required and must be a defined value.</summary>
    public static void RequiredEnum<TEnum>(ValidationErrors errors, string field, TEnum? value, string label)
        where TEnum : struct, Enum
    {
        if (value is null || !Enum.IsDefined(value.Value))
        {
            errors.Add(field, $"{label} is required ({string.Join(", ", Enum.GetNames<TEnum>())}).");
        }
    }

    [GeneratedRegex(@"^\+?[0-9]{8,15}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
