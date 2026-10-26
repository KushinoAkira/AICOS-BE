namespace AICOS.Services.SmokeTests;

public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public PostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("AICOS_TEST_POSTGRES")))
        {
            Skip = "Set AICOS_TEST_POSTGRES to run this test against PostgreSQL.";
        }
    }
}
