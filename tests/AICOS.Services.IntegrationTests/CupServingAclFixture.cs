using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

/// <summary>Only fresh guarded containers; never provisions an operating role or returns secrets in evidence.</summary>
internal sealed class CupServingAclFixture
{
    public string Ordering { get; private init; } = "";
    public string Kitchen { get; private init; } = "";

    public static async Task<CupServingAclFixture> CreateAsync(string ordering, string kitchen)
    {
        await OperationalTestTarget.VerifyAsync(ordering); await OperationalTestTarget.VerifyAsync(kitchen);
        await using (var admin = new NpgsqlConnection(ordering))
        {
            await admin.OpenAsync();
            await using var existing = new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname IN ('ordering_svc','kitchen_svc')", admin);
            ((long)(await existing.ExecuteScalarAsync())!).Should().Be(0, "ACL fixture requires fresh service roles in its disposable cluster");
            await using var create = new NpgsqlCommand("CREATE ROLE ordering_svc NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS NOREPLICATION; CREATE ROLE kitchen_svc NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS NOREPLICATION", admin);
            await create.ExecuteNonQueryAsync();
        }
        var orderPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var kitchenPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await ConfigureAsync(ordering, "ordering", "ordering_svc", orderPassword);
        await ConfigureAsync(kitchen, "kitchen", "kitchen_svc", kitchenPassword);
        return new CupServingAclFixture
        {
            Ordering = RuntimeConnection(ordering, "ordering_svc", orderPassword, "ordering"),
            Kitchen = RuntimeConnection(kitchen, "kitchen_svc", kitchenPassword, "kitchen"),
        };
    }

    private static string RuntimeConnection(string admin, string role, string password, string schema) =>
        new NpgsqlConnectionStringBuilder(admin)
        {
            Username = role,
            Password = password,
            SearchPath = schema,
            ApplicationName = "aicos-cup-acl-test",
            MaxPoolSize = 8,
            IncludeErrorDetail = false
        }.ConnectionString;

    private static async Task ConfigureAsync(string connection, string schema, string role, string password)
    {
        await OperationalTestTarget.VerifyAsync(connection);
        var target = new NpgsqlConnectionStringBuilder(connection);
        await using (var admin = new NpgsqlConnection(connection))
        {
            await admin.OpenAsync();
            // Only these new test databases: no PUBLIC or ownership changes on any operating DB.
            var database = '"' + target.Database!.Replace("\"", "\"\"") + '"';
            await using var isolate = new NpgsqlCommand($"REVOKE ALL ON DATABASE {database} FROM PUBLIC", admin);
            await isolate.ExecuteNonQueryAsync();
        }
        await ApplyProposalAsync(connection, schema, role, "cup-serving-service-prerequisites.sql");
        await ApplyProposalAsync(connection, schema, role, "cup-serving-runtime-acl.sql");
        await using var secure = new NpgsqlConnection(connection); await secure.OpenAsync();
        // Suppress statement/parameter/context disclosure for this credential-only admin session.
        await using (var protect = new NpgsqlCommand("SET log_statement='none'; SET log_min_error_statement='panic'; SET log_parameter_max_length_on_error=0; SET log_error_verbosity='terse'", secure))
            await protect.ExecuteNonQueryAsync();
        try
        {
            await using var tx = await secure.BeginTransactionAsync();
            await using (var secret = new NpgsqlCommand("SELECT set_config('aicos_test.role_password', @password, true)", secure, tx))
            { secret.Parameters.AddWithValue("password", password); await secret.ExecuteNonQueryAsync(); }
            // Whitelisted constant role/schema names supplied only by CreateAsync.
            await using var configure = new NpgsqlCommand($"DO $body$ BEGIN EXECUTE format('ALTER ROLE {role} PASSWORD %L', current_setting('aicos_test.role_password')); END $body$; ALTER ROLE {role} LOGIN; ALTER ROLE {role} IN DATABASE \"{target.Database}\" SET search_path={schema}", secure, tx);
            await configure.ExecuteNonQueryAsync(); await tx.CommitAsync();
        }
        catch { throw new InvalidOperationException("Disposable runtime credential configuration failed; sensitive details suppressed."); }
    }

    private static async Task ApplyProposalAsync(string connection, string schema, string role, string file)
    {
        await OperationalTestTarget.VerifyAsync(connection);
        var target = new NpgsqlConnectionStringBuilder(connection);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "docs/proposals"))) root = root.Parent ?? throw new InvalidOperationException("Proposal root missing.");
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "exec", "-i", OperationalTestTarget.Required("AICOS_OPERATIONS_POSTGRES_ID"), "psql", "-X", "-U", target.Username!, "-d", target.Database!,
            "-v", "expected_database=" + target.Database, "-v", "expected_schema=" + schema, "-v", "service_role=" + role,
            "-v", "expected_system_identifier=" + OperationalTestTarget.Required("AICOS_OPERATIONS_TEST_CLUSTER") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "docs/proposals", file))); process.StandardInput.Close();
        await process.WaitForExitAsync(); process.ExitCode.Should().Be(0, await output + await error);
    }

    public async Task WriteMatrixAsync()
    {
        var lines = new List<string> { "# Isolated Cup service-role permission evidence", "",
            "Generated only after role-login and HTTP/SQL assertions PASS. No credentials or operating-role inspection.",
            "Cluster: " + OperationalTestTarget.Required("AICOS_OPERATIONS_TEST_CLUSTER"), "" };
        foreach (var pair in new[] { (Connection: Kitchen, Schema: "kitchen"), (Connection: Ordering, Schema: "ordering") })
        {
            await using var connection = new NpgsqlConnection(pair.Connection); await connection.OpenAsync();
            await using var user = new NpgsqlCommand("SELECT current_user", connection);
            lines.Add("## " + (string)(await user.ExecuteScalarAsync())!); lines.Add("");
            lines.Add("| Table | SELECT | INSERT | UPDATE | DELETE | TRUNCATE |"); lines.Add("| --- | --- | --- | --- | --- | --- |");
            await using var matrix = new NpgsqlCommand("SELECT c.relname, has_table_privilege(current_user,c.oid,'SELECT'), has_table_privilege(current_user,c.oid,'INSERT'), has_table_privilege(current_user,c.oid,'UPDATE'), has_table_privilege(current_user,c.oid,'DELETE'), has_table_privilege(current_user,c.oid,'TRUNCATE') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relkind='r' ORDER BY c.relname", connection);
            matrix.Parameters.AddWithValue("schema", pair.Schema);
            await using var rows = await matrix.ExecuteReaderAsync();
            while (await rows.ReadAsync()) lines.Add("| " + rows.GetString(0) + " | " + string.Join(" | ", Enumerable.Range(1, 5).Select(i => rows.GetBoolean(i) ? "YES" : "NO")) + " |");
            lines.Add("");
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(root.FullName, "docs/proposals"))) root = root.Parent ?? throw new InvalidOperationException("Evidence root missing.");
        await File.WriteAllLinesAsync(Path.Combine(root.FullName, "docs/CUP_SERVING_ACL_PERMISSION_MATRIX.md"), lines);
    }

    public static async Task VerifyRoleAsync(string connection, string role, string schema, string ledger)
    {
        await using var runtime = new NpgsqlConnection(connection); await runtime.OpenAsync();
        await using (var identity = new NpgsqlCommand("SELECT current_user", runtime))
            ((string)(await identity.ExecuteScalarAsync())!).Should().Be(role);
        await using (var attributes = new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls AND NOT rolreplication AND rolcanlogin FROM pg_roles WHERE rolname = current_user", runtime))
            ((bool)(await attributes.ExecuteScalarAsync())!).Should().BeTrue();
        await using (var ownership = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = @schema AND tableowner = current_user", runtime))
        { ownership.Parameters.AddWithValue("schema", schema); ((long)(await ownership.ExecuteScalarAsync())!).Should().Be(0); }
        await using (var rights = new NpgsqlCommand("SELECT has_table_privilege(current_user,@table,'SELECT') AND has_table_privilege(current_user,@table,'INSERT') AND NOT has_table_privilege(current_user,@table,'UPDATE') AND NOT has_table_privilege(current_user,@table,'DELETE') AND NOT has_table_privilege(current_user,@table,'TRUNCATE') AND NOT has_schema_privilege(current_user,@schema,'CREATE')", runtime))
        { rights.Parameters.AddWithValue("table", schema + "." + ledger); rights.Parameters.AddWithValue("schema", schema); ((bool)(await rights.ExecuteScalarAsync())!).Should().BeTrue(); }
        foreach (var statement in new[] { $"UPDATE {schema}.{ledger} SET committed_at_utc = committed_at_utc WHERE false", $"DELETE FROM {schema}.{ledger} WHERE false" })
        {
            // Both ledgers have different timestamp names; a no-op UPDATE uses a shared column.
            var sql = statement.Replace("committed_at_utc = committed_at_utc", "tenant_id = tenant_id");
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            { await using var denied = new NpgsqlCommand(sql, runtime); await denied.ExecuteNonQueryAsync(); });
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }
}
