using System.Text.Json;
using AICOS.BuildingBlocks.Hosting;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Persistence;
using AICOS.ReportingAnalytics.Api.Features.Reports;
using AICOS.ReportingAnalytics.Api.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AICOS.Billing.StackTools;

// Synthetic fixtures/HTTP fault injection refuse all operating database names/clusters.
internal static class CollectionRecoveryProbe
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is ["recovery-schema", var directory])
        {
            Directory.CreateDirectory(directory);
            const string offline = "Host=127.0.0.1;Port=1;Database=offline;Username=offline;Timeout=1";
            using var payment = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(offline, p => p.MigrationsHistoryTable("__ef_migrations_history", "payment")).UseSnakeCaseNamingConvention().Options);
            using var reporting = new ReportingAnalyticsDbContext(new DbContextOptionsBuilder<ReportingAnalyticsDbContext>().UseNpgsql(offline, p => p.MigrationsHistoryTable("__ef_migrations_history", "reporting")).UseSnakeCaseNamingConvention().Options);
            File.WriteAllText(Path.Combine(directory, "payment.sql"), payment.GetService<IMigrator>().GenerateScript());
            File.WriteAllText(Path.Combine(directory, "reporting.sql"), reporting.GetService<IMigrator>().GenerateScript());
            Console.WriteLine("Full recovery test schemas exported offline; no connection opened."); return true;
        }
        if (args is not ["recovery-fixtures", _] && args is not ["recovery-reporting"]) return false;
        var fixture = args[0] == "recovery-fixtures";
        var connection = Environment.GetEnvironmentVariable(fixture ? "AICOS_RECOVERY_TEST_ADMIN_CONNECTION" : "ConnectionStrings__Default")
            ?? throw new InvalidOperationException("Missing test-only connection.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database != (fixture ? "aicos_recovery_payment_test" : "aicos_recovery_reporting_test"))
            throw new InvalidOperationException("Scratch database required.");
        await using (var probe = new NpgsqlConnection(connection))
        {
            await probe.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", probe);
            var cluster = (string)(await command.ExecuteScalarAsync())!;
            if (cluster == ApprovedCollectionRecovery.MainCluster || cluster != Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_CLUSTER"))
                throw new InvalidOperationException("Wrong scratch cluster.");
        }
        if (fixture)
        {
            await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseNpgsql(connection).UseSnakeCaseNamingConvention().Options);
            var tenant = Guid.NewGuid(); var branch = Guid.NewGuid(); var cashier = Guid.NewGuid();
            var payments = new[] { new Payment(tenant, branch, Guid.NewGuid(), 208000, "Cash", "Synthetic legacy one"), new Payment(tenant, branch, Guid.NewGuid(), 50000, "Cash", "Synthetic legacy two") };
            payments[0].RecordCash(cashier, new DateTime(2026, 11, 4, 16, 38, 21, DateTimeKind.Utc).AddTicks(2131530));
            payments[1].RecordCash(cashier, new DateTime(2026, 11, 6, 16, 25, 22, DateTimeKind.Utc).AddTicks(2452210));
            db.Payments.AddRange(payments);
            if (Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_EXTRA") == "true")
            {
                var extra = new Payment(tenant, branch, Guid.NewGuid(), 10000, "Cash", "Unapproved extra"); extra.RecordCash(cashier, DateTime.UtcNow); db.Payments.Add(extra);
            }
            await db.SaveChangesAsync();
            var receipts = payments.Select(p => new CollectionReceipt(p.Id, p.TenantId, p.BranchId, null, p.OrderId, p.CashierUserId, null, p.Method, p.Amount, p.PaidAtUtc!.Value, p.TransactionRef, true)).ToArray();
            var plan = new ApprovedCollectionRecovery.Plan(parsed.Database!, Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_CLUSTER")!, receipts);
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(plan));
            Console.WriteLine("Synthetic scratch fixture ready; no operating data copied."); return true;
        }
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders(); builder.Services.AddAicosCore(); builder.Services.AddAuthorization();
        builder.Services.AddAicosBranchDirectory(builder.Configuration);
        builder.Services.AddDbContext<ReportingAnalyticsDbContext>(o => o.UseNpgsql(connection).UseSnakeCaseNamingConvention());
        var app = builder.Build(); app.UseExceptionHandler(); app.UseAuthorization();
        var fault = Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_ACK"); var used = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Method != "POST" || fault is null || Interlocked.Exchange(ref used, 1) != 0) { await next(); return; }
            var original = context.Response.Body; await using var buffer = new MemoryStream(); context.Response.Body = buffer;
            await next(); context.Response.Body = original;
            if (fault == "lost") { context.Abort(); return; }
            if (fault == "wrong") { context.Response.ContentType = "application/json"; await context.Response.WriteAsJsonAsync(new { eventId = Guid.Empty }); return; }
            if (fault == "accepted") context.Response.StatusCode = 202;
            buffer.Position = 0; await buffer.CopyToAsync(original);
        });
        app.MapCollections(app.MapGroup("/api/v1/reporting"));
        app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous();
        await app.RunAsync("http://0.0.0.0:8080"); return true;
    }
}
