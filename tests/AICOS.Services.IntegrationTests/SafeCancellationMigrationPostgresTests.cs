using AICOS.KitchenOperations.Api.Persistence;
using AICOS.Ordering.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AICOS.Services.IntegrationTests;

[Collection("Session bill PostgreSQL")]
public sealed class SafeCancellationMigrationPostgresTests
{
    [BillPostgresFact]
    public async Task Exact_proposal_sql_preflight_repeat_acl_and_empty_rollback_on_scratch_service_databases()
    {
        var connection = Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CONNECTION")!;
        await SessionBillHarness.GuardAsync(connection);
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        foreach (var service in new[] { "ordering", "kitchen" })
        {
            var database = "aicos_" + service;
            // The connection/cluster guard above proves these names are inside this run's disposable cluster.
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
            var baseline = service == "ordering" ? "20261118143317_OrderingSessionManualDiscount" : "20261114164849_TableTransferRoutingVersion";
            await using DbContext context = service == "ordering"
                ? new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseNpgsql(target, p => p.MigrationsHistoryTable("__ef_migrations_history", service)).UseSnakeCaseNamingConvention().Options)
                : new KitchenOperationsDbContext(new DbContextOptionsBuilder<KitchenOperationsDbContext>().UseNpgsql(target, p => p.MigrationsHistoryTable("__ef_migrations_history", service)).UseSnakeCaseNamingConvention().Options);
            await context.GetService<IMigrator>().MigrateAsync(baseline);
            await using var db = new NpgsqlConnection(target); await db.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('aicos.expected_cluster', @cluster, false)", db))
            { set.Parameters.AddWithValue("cluster", Environment.GetEnvironmentVariable("AICOS_BILL_TEST_CLUSTER")!); await set.ExecuteScalarAsync(); }
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "docs", "proposals", "order-cancellation"))) root = root.Parent;
            root.Should().NotBeNull(); var proposal = Path.Combine(root!.FullName, "docs", "proposals", "order-cancellation");
            async Task Sql(string name) { await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(proposal, name)), db); await cmd.ExecuteNonQueryAsync(); }
            await Sql("00_preflight.READ_ONLY.sql");
            var expand = service == "ordering" ? "01_ordering_expand.PROPOSAL.sql" : "02_kitchen_expand.PROPOSAL.sql";
            await Sql(expand); await Sql(expand); await Sql("00_preflight.READ_ONLY.sql");
            var role = service + "_svc";
            await using (var createRole = new NpgsqlCommand($"CREATE ROLE {role} NOLOGIN", db)) await createRole.ExecuteNonQueryAsync();
            await Sql(service == "ordering" ? "03_ordering_grants.PROPOSAL.sql" : "04_kitchen_grants.PROPOSAL.sql");
            var table = service == "ordering" ? "order_cancellation_operations" : "order_cancellation_receipts";
            await using (var shape = new NpgsqlCommand($"SELECT count(*) FROM information_schema.columns WHERE table_schema='{service}' AND table_name='{table}'", db))
                Convert.ToInt32(await shape.ExecuteScalarAsync()).Should().Be(service == "ordering" ? 22 : 10);
            await using (var acl = new NpgsqlCommand($"""
                SELECT has_table_privilege('{role}','{service}.{table}','SELECT'),
                  has_table_privilege('{role}','{service}.{table}','INSERT'),
                  has_column_privilege('{role}','{service}.{table}','state','UPDATE'),
                  has_column_privilege('{role}','{service}.{table}','request_hash','UPDATE'),
                  has_table_privilege('{role}','{service}.{table}','DELETE'),
                  has_table_privilege('{role}','{service}.{table}','TRUNCATE'),
                  (SELECT rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolcanlogin FROM pg_roles WHERE rolname='{role}');
                """, db))
            {
                await using var reader = await acl.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue();
                for (var i = 0; i < 7; i++) reader.GetBoolean(i).Should().Be(i < 3);
            }
            await context.GetService<IMigrator>().MigrateAsync(baseline);
            await using var absent = new NpgsqlCommand($"SELECT to_regclass('{service}.{table}') IS NULL", db);
            (await absent.ExecuteScalarAsync()).Should().Be(true);
            await context.Database.MigrateAsync();
            await Sql("00_preflight.READ_ONLY.sql");
        }
    }
}
