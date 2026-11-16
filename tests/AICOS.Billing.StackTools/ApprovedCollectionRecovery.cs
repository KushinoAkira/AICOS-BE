using System.Data;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Billing;
using AICOS.Payments.Api.Domain;
using AICOS.Payments.Api.Features.CashierShifts;
using AICOS.Payments.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AICOS.Billing.StackTools;

// Administrative one-shot only; never registered by a service or invoked at startup.
internal static class ApprovedCollectionRecovery
{
    internal const string MainCluster = "7703616372368646182";
    internal sealed record Plan(string Database, string Cluster, CollectionReceipt[] Receipts);

    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is not ["recover-approved-collections", var file]) return false;
        var phase = "approval";
        var captureCommitted = false;
        try
        {
            if (Environment.GetEnvironmentVariable("AICOS_RECOVERY_EXECUTION_APPROVED") != "true")
                throw new InvalidOperationException("Explicit financial write approval required.");
            phase = "plan";
            var bytes = await File.ReadAllBytesAsync(file);
            if (bytes.Length > 65536 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(Required("AICOS_RECOVERY_PLAN_SHA256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Approved plan fingerprint mismatch.");
            var plan = JsonSerializer.Deserialize<Plan>(bytes) ?? throw new InvalidOperationException("Missing plan.");
            Validate(plan);
            var connection = Required("AICOS_RECOVERY_CONNECTION");
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (parsed.Database != plan.Database || parsed.Username != "payment_svc")
                throw new InvalidOperationException("Wrong database or role.");
            var url = new Uri(Required("AICOS_REPORTING_BASE_URL"), UriKind.Absolute);
            var host = plan.Cluster == MainCluster ? "reporting" : "recovery-reporting";
            if (url.Scheme != "http" || url.Host != host || url.Port != 8080 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0)
                throw new InvalidOperationException("Wrong Reporting target.");
            var key = Required("AICOS_COLLECTIONS_MACHINE_KEY");
            if (key.Length < 32) throw new InvalidOperationException("Missing Collections key.");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierShifts:Enabled"] = "true",
                ["CashierShifts:RequireOpenForCash"] = "false"
            }).Build();
            await using var db = new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseNpgsql(connection, o => o.CommandTimeout(15)).UseSnakeCaseNamingConvention().Options, config);
            phase = "target";
            await db.Database.OpenConnectionAsync();
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT system_identifier::text || ':' || current_database() || ':' || current_user FROM pg_control_system()";
                if ((string?)await command.ExecuteScalarAsync() != $"{plan.Cluster}:{plan.Database}:payment_svc")
                    throw new InvalidOperationException("Wrong PostgreSQL cluster.");
            }
            phase = "source-and-capture";
            var ids = plan.Receipts.Select(r => r.EventId).ToArray();
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
            {
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='15s';");
                // Same order locks as payment success, plus row locks to re-read the approved proof.
                foreach (var r in plan.Receipts.OrderBy(r => r.OrderId))
                {
                    var orderKey = $"aicos:payment-order:{r.TenantId}:{r.OrderId}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({orderKey},0))");
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM payment.payments WHERE id={r.EventId} FOR UPDATE");
                }
                var missing = await db.Payments.AsNoTracking().Where(p =>
                    (p.Status == PaymentStatus.Succeeded || p.Status == PaymentStatus.PartiallyRefunded || p.Status == PaymentStatus.Refunded)
                    && p.PaidAtUtc != null && !db.Collections.Any(c => c.Id == p.Id)).Select(p => p.Id).ToListAsync();
                if (missing.Except(ids).Any() || await db.Collections.AnyAsync(c => !ids.Contains(c.Id)) || await db.CashierShifts.AnyAsync())
                    throw new InvalidOperationException("Collection scope drift.");
                var sources = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
                if (sources.Count != 2) throw new InvalidOperationException("Approved source missing.");
                foreach (var expected in plan.Receipts)
                {
                    var p = sources.Single(p => p.Id == expected.EventId);
                    var actual = new CollectionReceipt(p.Id, p.TenantId, p.BranchId, null, p.OrderId, p.CashierUserId, null, p.Method, p.Amount, p.PaidAtUtc ?? default, p.TransactionRef, true);
                    if (p.Status != PaymentStatus.Succeeded || p.RefundedAmount != 0 || actual != expected ||
                        await db.PaymentRefunds.AnyAsync(f => f.PaymentId == p.Id && f.Status == RefundStatus.PendingApproval) ||
                        await db.SessionBillOrders.AnyAsync(b => b.OrderId == p.OrderId))
                        throw new InvalidOperationException("Approved source proof changed.");
                    var old = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == p.Id);
                    if (old is not null && (JsonSerializer.Deserialize<CollectionReceipt>(old.Payload) != expected ||
                        old.TenantId != expected.TenantId || old.BranchId != expected.BranchId || old.CashierId != expected.CashierId ||
                        old.ShiftId != null || old.Method != expected.Method || old.Amount != expected.Amount || old.PaidAtUtc != expected.PaidAtUtc))
                        throw new InvalidOperationException("Existing receipt identity changed.");
                }
                foreach (var receipt in plan.Receipts) await CashierLedger.CaptureAsync(db, receipt, default);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                captureCommitted = true;
            }
            if (Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_CRASH_AFTER_CAPTURE") == "true") Environment.Exit(23);
            phase = "delivery";
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            var pending = await db.Collections.Where(c => ids.Contains(c.Id) && c.DeliveredAtUtc == null).OrderBy(c => c.PaidAtUtc).ToListAsync();
            foreach (var row in pending)
            {
                // Deliberately scoped: the normal dispatcher would scan unrelated missing sources.
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/internal/collections"))
                { Content = JsonContent.Create(JsonSerializer.Deserialize<CollectionReceipt>(row.Payload)) };
                request.Headers.Add(CollectionMachineAuth.Header, key);
                using var response = await http.SendAsync(request);
                if (response.StatusCode != System.Net.HttpStatusCode.OK || (await response.Content.ReadFromJsonAsync<Ack>())?.EventId != row.Id)
                    throw new InvalidOperationException("No committed receipt acknowledgement.");
                row.DeliveredAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            Console.WriteLine("PASS approved batch: 2 Cash receipts / 258000; committed stable ACKs, no broad scan or worker.");
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"STOP approved recovery at {phase}; capture committed={captureCommitted}. No raw secret/error dump; retain durable rows and inspect before retry.");
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing runtime input.");
    private static void Validate(Plan p)
    {
        var main = p.Database == "aicos_payment" && p.Cluster == MainCluster;
        var test = p.Database == "aicos_recovery_payment_test" && p.Cluster != MainCluster;
        if ((!main && !test) || !ulong.TryParse(p.Cluster, out _) ||
            (main && Environment.GetEnvironmentVariable("AICOS_RECOVERY_TEST_CRASH_AFTER_CAPTURE") is not null) ||
            p.Receipts is not { Length: 2 } || p.Receipts.Select(r => r.EventId).Distinct().Count() != 2 ||
            p.Receipts.Select(r => r.OrderId).Distinct().Count() != 2 ||
            p.Receipts.Select(r => (r.TenantId, r.BranchId)).Distinct().Count() != 1 || p.Receipts.Sum(r => r.Amount) != 258000 ||
            p.Receipts.Any(r => r.EventId == Guid.Empty || r.TenantId == Guid.Empty || r.BranchId == Guid.Empty ||
                r.OrderId is null || r.OrderId == Guid.Empty || r.BillId is not null || r.CashierId is null || r.CashierId == Guid.Empty ||
                r.ShiftId is not null || !r.Recovered || r.Method != "Cash" || r.Amount <= 0 || r.Amount != decimal.Truncate(r.Amount) ||
                r.PaidAtUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(r.Reference) || r.Reference.Length > 64))
            throw new InvalidOperationException("Outside approved legacy Cash policy.");
    }
    private sealed record Ack(Guid EventId);
}
