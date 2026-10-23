using System.Runtime.CompilerServices;

namespace AICOS.Services.IntegrationTests;

/// <summary>
/// The committed settings carry no secrets, so the tests supply their own throwaway values (never a deployment secret).
/// Environment variables are read by every host the tests start.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Configure()
    {
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "unit-test-only-signing-key-0123456789abcdef");
        Environment.SetEnvironmentVariable("Database__Password", "test-only-not-a-real-password");
        Environment.SetEnvironmentVariable("Payments__SecretKey", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        Environment.SetEnvironmentVariable("Auth__SeedAdmin__Password", "Test-only-Passw0rd!");
    }
}
