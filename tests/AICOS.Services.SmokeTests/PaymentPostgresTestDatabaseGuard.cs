using Npgsql;

namespace AICOS.Services.SmokeTests;

internal static class PaymentPostgresTestDatabaseGuard
{
    public static void RequireDedicatedDatabase(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (!string.Equals(database, "aicos_payment_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Payment mutation tests require database 'aicos_payment_test'; got '{database}'.");
        }
    }
}
