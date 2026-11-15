using System.Text.Json;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.KitchenOperations.Api.Features.Barista;
public sealed class BarPrintDispatcher(KitchenOperationsDbContext db, IBarPrinter printer, TimeProvider clock)
{
    public async Task DispatchAsync(Guid id, CancellationToken ct)
    {
        var hint = await db.PrintJobs.AsNoTracking().Where(j => j.Id == id).Join(db.Tickets, j => j.TicketId, t => t.Id, (j, t) => new { j.TenantId, t.OrderId }).SingleOrDefaultAsync(ct);
        if (hint is null)
            return;
        var owner = Guid.NewGuid();
        var now = clock.GetUtcNow().UtcDateTime;
        var job = await KitchenTicketWrites.RunAsync<KitchenPrintJob?>(db, hint.TenantId, hint.OrderId, async () =>
        {
            var row = await db.PrintJobs.SingleAsync(j => j.Id == id, ct);
            if (row.Status == "PRINTING" && row.LeaseUntilUtc <= now)
            {
                row.Status = "UNKNOWN";
                row.ErrorCode = "printer_worker_lost";
                row.LeaseOwner = null;
                row.LeaseUntilUtc = null;
                await db.SaveChangesAsync(ct);
                return null;
            }
            if (row.Status != "QUEUED")
                return null;
            var ticket = await db.Tickets.SingleAsync(t => t.Id == row.TicketId, ct);
            if (ticket.RemakeCount != row.Cycle || ticket.TableNumber != row.TableSnapshot || ticket.RecoveryHoldReason != null || ticket.Status == Domain.Enums.KitchenTicketStatus.Cancelled)
            {
                row.Status = "FAILED";
                row.ErrorCode = "printer_ticket_changed";
                await db.SaveChangesAsync(ct);
                return null;
            }
            var canonical = JsonSerializer.Serialize(BarPrintDocument.Read(row.Payload));
            if (BaristaOperations.Hash(canonical) != row.PayloadHash)
            {
                row.Status = "FAILED";
                row.ErrorCode = "printer_payload_invalid";
                await db.SaveChangesAsync(ct);
                return null;
            }
            row.Status = "PRINTING";
            row.StartedAtUtc ??= now;
            row.LeaseOwner = owner;
            row.LeaseUntilUtc = now.AddSeconds(30);
            row.Fence++;
            await db.SaveChangesAsync(ct);
            return row;
        }, ct);
        if (job is null)
            return;
        PrinterResult receipt;
        try
        {
            receipt = await printer.PrintAsync(job, ct);
        }
        catch (Exception) { receipt = new("UNKNOWN", "printer_adapter_failed"); }
        await KitchenTicketWrites.RunAsync(db, hint.TenantId, hint.OrderId, async () =>
        {
            var row = await db.PrintJobs.SingleAsync(j => j.Id == id, ct);
            if (row.Status != "PRINTING" || row.LeaseOwner != owner || row.Fence != job.Fence)
                return false;
            var table = await db.Tickets.Where(t => t.Id == row.TicketId).Select(t => t.TableNumber).SingleAsync(ct);
            row.Status = table != row.TableSnapshot ? "UNKNOWN" : receipt.Status is "PRINTED" or "FAILED" ? receipt.Status : "UNKNOWN";
            row.ErrorCode = table != row.TableSnapshot ? "printer_location_changed" : receipt.ErrorCode;
            row.TestOutput = receipt.IsTest;
            row.PrintedAtUtc = row.Status == "PRINTED" ? clock.GetUtcNow().UtcDateTime : null;
            row.LeaseOwner = null;
            row.LeaseUntilUtc = null;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }
}
public sealed class BarPrintWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<BarPrintWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (BaristaOperations.Enabled(config) && config.GetValue("BaristaWorkflow:WorkerEnabled", true))
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
                    var now = DateTime.UtcNow;
                    var jobs = await db.PrintJobs.AsNoTracking().Where(j => j.Status == "QUEUED" || (j.Status == "PRINTING" && j.LeaseUntilUtc <= now)).OrderBy(j => j.CreatedAtUtc).Select(j => j.Id).Take(10).ToListAsync(ct);
                    foreach (var id in jobs)
                        await scope.ServiceProvider.GetRequiredService<BarPrintDispatcher>().DispatchAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Bar printer worker will resume scan ({Type}).", ex.GetType().Name); }
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
