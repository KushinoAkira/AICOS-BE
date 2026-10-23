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
    ];

    private const string ChatPrefix = "/api/v1/ai/chat";

    public static (string Group, int Limit) Classify(string method, PathString path, int normalLimit, int authLimit, int chatLimit)
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

        return ("all", normalLimit);
    }
}
