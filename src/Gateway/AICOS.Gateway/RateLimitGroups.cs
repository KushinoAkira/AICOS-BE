namespace AICOS.Gateway;

/// <summary>Which rate-limit budget a request counts against.</summary>
public static class RateLimitGroups
{
    private static readonly string[] AuthPaths =
    [
        "/api/v1/identity/auth/login",
        "/api/v1/identity/auth/register",
        "/api/v1/identity/auth/forgot-password",
        "/api/v1/identity/auth/reset-password",
        "/api/v1/identity/auth/register-brand",   // open to anyone, creates a tenant and an account
        "/api/v1/identity/public/contact",        // open to anyone, writes to the database
    ];

    private const string ChatPrefix = "/api/v1/ai/chat";

    /// <summary>Table QR ordering needs no account, so it gets its own, tighter budget per client address.</summary>
    private const string QrPrefix = "/api/v1/orders/qr";

    public static (string Group, int Limit) Classify(string method, PathString path, int normalLimit, int authLimit, int chatLimit, int qrLimit = 120)
    {
        if (HttpMethods.IsPost(method))
        {
            if (AuthPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                return ("auth", authLimit);
            }

            if (path.StartsWithSegments(ChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ("chat", chatLimit);
            }
        }

        if (path.StartsWithSegments(QrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("qr", qrLimit);
        }

        return ("all", normalLimit);
    }
}
