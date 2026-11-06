using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

namespace AICOS.Services.IntegrationTests;

public sealed class OperationalInfrastructureFactAttribute : FactAttribute
{
    public OperationalInfrastructureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AICOS_OPERATIONS_TEST_CLUSTER")))
            Skip = "Run scripts/Test-StaffOperationsInfrastructure.ps1 -Run for the isolated PostgreSQL/Redis cluster.";
    }
}

/// <summary>Fail closed before schema creation, fixtures, or restarting the disposable Redis container.</summary>
public static class OperationalTestTarget
{
    public static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing isolated operations test configuration: " + name);

    public static bool IsApprovedConnection(string connection)
    {
        var target = new NpgsqlConnectionStringBuilder(connection);
        return target.Host == "127.0.0.1" && target.Port is > 1024 and not (5432 or 5433 or 5434)
            && target.Port.ToString() == Environment.GetEnvironmentVariable("AICOS_OPERATIONS_POSTGRES_PORT")
            && target.Username == "aicos_operations_test" && !target.IncludeErrorDetail
            && (target.Database is "aicos_operations_ordering_test" or "aicos_operations_kitchen_test")
            && (connection == Environment.GetEnvironmentVariable("AICOS_OPERATIONS_ORDERING_CONNECTION")
                || connection == Environment.GetEnvironmentVariable("AICOS_OPERATIONS_KITCHEN_CONNECTION"));
    }

    public static void ValidateRedis(string connection)
    {
        var options = ConfigurationOptions.Parse(connection);
        var port = int.Parse(Required("AICOS_OPERATIONS_REDIS_PORT"));
        var expected = "127.0.0.1:" + port;
        if (connection != Required("AICOS_OPERATIONS_REDIS") || options.EndPoints.Count != 1
            || options.EndPoints[0].ToString() != expected || port <= 1024 || port is 6379 or 6380)
            throw new InvalidOperationException("Redis test endpoint is not isolated.");
    }

    public static async Task VerifyAsync(string connection)
    {
        if (!IsApprovedConnection(connection)) throw new InvalidOperationException("Refusing non-test PostgreSQL target.");
        await VerifyContainerAsync("POSTGRES", 5432);
        await using var probe = new NpgsqlConnection(connection);
        await probe.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", probe);
        var actual = (string)(await command.ExecuteScalarAsync())!;
        if (actual != Required("AICOS_OPERATIONS_TEST_CLUSTER"))
            throw new InvalidOperationException("PostgreSQL test cluster identity mismatch.");
    }

    public static async Task RestartRedisAsync()
    {
        await VerifyContainerAsync("REDIS", 6379);
        await DockerAsync("restart", "--time", "2", Required("AICOS_OPERATIONS_REDIS_ID"));
        await VerifyContainerAsync("REDIS", 6379);
    }

    public static async Task DuringRedisOutageAsync(Func<Task> operation)
    {
        await VerifyContainerAsync("REDIS", 6379);
        var id = Required("AICOS_OPERATIONS_REDIS_ID");
        await DockerAsync("stop", "--time", "2", id);
        try { await operation(); }
        finally { await DockerAsync("start", id); await VerifyContainerAsync("REDIS", 6379); }
    }

    public static async Task VerifyRedisAsync()
    {
        ValidateRedis(Required("AICOS_OPERATIONS_REDIS"));
        await VerifyContainerAsync("REDIS", 6379);
    }

    private static async Task VerifyContainerAsync(string kind, int internalPort)
    {
        var id = Required("AICOS_OPERATIONS_" + kind + "_ID");
        var json = await DockerAsync("inspect", id);
        using var document = JsonDocument.Parse(json);
        var container = document.RootElement[0];
        if (container.GetProperty("Id").GetString() != id
            || container.GetProperty("Config").GetProperty("Labels").GetProperty("aicos.purpose").GetString() != "staff-operations-test"
            || (container.GetProperty("Mounts").ValueKind == JsonValueKind.Array
                && container.GetProperty("Mounts").EnumerateArray().Any(m => m.GetProperty("Type").GetString() != "tmpfs")))
            throw new InvalidOperationException("Disposable container guard failed.");
        var dataPath = kind == "POSTGRES" ? "/var/lib/postgresql/data" : "/data";
        if (!container.GetProperty("HostConfig").GetProperty("Tmpfs").TryGetProperty(dataPath, out _))
            throw new InvalidOperationException("Test data path is not backed by tmpfs.");
        var networks = container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().ToArray();
        if (networks.Length != 1 || networks[0].Name != Required("AICOS_OPERATIONS_TEST_NETWORK"))
            throw new InvalidOperationException("Test network identity mismatch.");
        var bindings = container.GetProperty("NetworkSettings").GetProperty("Ports").GetProperty(internalPort + "/tcp");
        if (bindings.GetArrayLength() != 1 || bindings[0].GetProperty("HostIp").GetString() != "127.0.0.1"
            || bindings[0].GetProperty("HostPort").GetString() != Required("AICOS_OPERATIONS_" + kind + "_PORT"))
            throw new InvalidOperationException("Unexpected test port publication.");
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot run test infrastructure guard.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        await error; // Do not echo raw Docker inspection/environment information.
        if (process.ExitCode != 0) throw new InvalidOperationException("Disposable Docker operation failed.");
        return await output;
    }
}
