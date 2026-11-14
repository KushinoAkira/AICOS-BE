using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Domain.Entities;
using AICOS.KitchenOperations.Api.Domain.Enums;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AICOS.KitchenOperations.Api.Features.Barista;

public sealed record AcceptTicketRequest(string ExpectedStatus, int ExpectedRemakeCount);
public sealed record LabelSelection(Guid ItemId, int Copies);
public sealed record CreateBarPrintRequest(IReadOnlyList<LabelSelection>? Items = null, Guid? ReprintOf = null, bool ConfirmUnknownRisk = false);
public sealed class BaristaOperations(KitchenOperationsDbContext db, ICurrentUser user, TimeProvider clock, IConfiguration config, IBarLocationDirectory locations, IBarOrderDirectory orders)
{
    public static bool Enabled(IConfiguration c) => c.GetValue<bool>("BaristaWorkflow:Enabled");
    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private (Guid Tenant, Guid Actor) Scope(string key)
    {
        KitchenEndpoints.RequireBar(user);
        if (user.UserId is not { } actor)
            throw new UnauthorizedException("Staff authentication required.");
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 80)
            throw new ValidationFailedException("Idempotency-Key", "Supply 8-80 characters.");
        return (TenantScope.Require(user), actor);
    }
    public async Task<KitchenPrintJob> AcceptAsync(Guid id, AcceptTicketRequest request, string key, CancellationToken ct)
    {
        var (tenant, actor) = Scope(key);
        var hint = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        var area = await locations.AreaAsync(hint.BranchId, hint.TableNumber, ct);
        var orderedAt = await orders.CreatedAtAsync(hint.OrderId, hint.BranchId, ct);
        var hash = Hash(JsonSerializer.Serialize(new
        {
            id,
            request
        }));
        return await KitchenTicketWrites.RunAsync(db, tenant, hint.OrderId, async () =>
        {
            if (db.Database.IsRelational())
            {
                var lk = $"aicos:bar-accept:{tenant}:{actor}:{Hash(key)}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lk},0))", ct);
            }
            var reused = await db.BaristaAcceptances.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.AcceptedBy == actor && a.KeyHash == Hash(key), ct);
            if (reused is not null && (reused.TicketId != id || reused.RequestHash != hash))
                throw new ConflictException("Acceptance request key differs.", "barista.key_conflict");
            var ticket = await TicketAccess.LoadAsync(db, user, id, ct);
            var prior = await db.BaristaAcceptances.SingleOrDefaultAsync(a => a.TicketId == id && a.Cycle == request.ExpectedRemakeCount, ct);
            if (prior is not null)
            {
                if (prior.AcceptedBy != actor || prior.RequestHash != hash)
                    throw new ConflictException("Another Barista accepted this cycle. Reload.", "barista.claimed");
                return await db.PrintJobs.SingleAsync(j => j.TicketId == id && j.Cycle == prior.Cycle && j.Kind == "SLIP" && j.ReprintOf == null, ct);
            }
            ticket.RequireRecoveryClear();
            if (!ticket.IsEligibleForBar || !((ticket.Status == KitchenTicketStatus.Pending && request.ExpectedStatus == "Pending") || (ticket.RemakeCount > 0 && ticket.Status == KitchenTicketStatus.Brewing && request.ExpectedStatus == "Brewing")) || ticket.RemakeCount != request.ExpectedRemakeCount)
                throw new ConflictException("Ticket state changed or is not eligible.", "barista.stale");
            var now = clock.GetUtcNow().UtcDateTime;
            var acceptance = new BaristaAcceptance { TicketId = id, Cycle = ticket.RemakeCount, TenantId = tenant, BranchId = ticket.BranchId, AcceptedBy = actor, KeyHash = Hash(key), AcceptedName = user.Name ?? "Ch\u01b0a x\u00e1c minh t\u00ean", AcceptedAtUtc = now, RequestHash = hash };
            db.BaristaAcceptances.Add(acceptance);
            if (ticket.FulfillmentMode == FulfillmentModes.WholeOrder && ticket.Status == KitchenTicketStatus.Pending)
                ticket.StartBrewing(now);
            var job = CreateJob(ticket, acceptance, "SLIP", "auto:" + id + ":" + ticket.RemakeCount, null, null, now, ticket.TableNumber == hint.TableNumber ? area : null, orderedAt);
            await db.SaveChangesAsync(ct);
            return job;
        }, ct);
    }
    public async Task<KitchenPrintJob> PrintAsync(Guid id, string kind, CreateBarPrintRequest request, string key, CancellationToken ct)
    {
        var (tenant, actor) = Scope(key);
        var hint = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        var area = await locations.AreaAsync(hint.BranchId, hint.TableNumber, ct);
        var orderedAt = await orders.CreatedAtAsync(hint.OrderId, hint.BranchId, ct);
        var hash = Hash(JsonSerializer.Serialize(new
        {
            id,
            kind,
            request
        }));
        var keyHash = Hash(key);
        return await KitchenTicketWrites.RunAsync(db, tenant, hint.OrderId, async () =>
        {
            if (db.Database.IsRelational())
            {
                var lk = $"aicos:bar-print:{tenant}:{actor}:{keyHash}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lk},0))", ct);
            }
            var previous = await db.PrintJobs.SingleOrDefaultAsync(j => j.TenantId == tenant && j.RequestedBy == actor && j.KeyHash == keyHash, ct);
            if (previous is not null)
            {
                if (previous.TicketId != id || previous.RequestHash != hash)
                    throw new ConflictException("Request key differs.", "barista.key_conflict");
                return previous;
            }
            var ticket = await TicketAccess.LoadAsync(db, user, id, ct);
            ticket.RequireRecoveryClear();
            if (!ticket.IsEligibleForBar || ticket.Status == KitchenTicketStatus.Cancelled)
                throw new ConflictException("Ticket cannot print.", "barista.stale");
            var acceptance = await db.BaristaAcceptances.SingleOrDefaultAsync(a => a.TicketId == id && a.Cycle == ticket.RemakeCount, ct) ?? throw new ConflictException("Accept this preparation cycle first.", "barista.accept_required");
            KitchenPrintJob? original = null;
            if (request.ReprintOf is { } originalId)
            {
                original = await db.PrintJobs.SingleOrDefaultAsync(j => j.Id == originalId && j.TenantId == tenant && j.TicketId == id, ct) ?? throw new NotFoundException("Print job", originalId);
                if (original.Status is "QUEUED" or "PRINTING")
                    throw new ConflictException("Wait for the existing job.", "print.pending");
                if (original.Status == "UNKNOWN" && !request.ConfirmUnknownRisk)
                    throw new ConflictException("Explicitly acknowledge possible duplicate physical output.", "print.unknown");
                if (original.Cycle != ticket.RemakeCount)
                    throw new ConflictException("Print job belongs to an earlier preparation cycle.", "print.stale_cycle");
                if (original.Kind != kind)
                    throw new ValidationFailedException("kind", "Reprint kind differs.");
            }
            else if (kind == "SLIP")
                throw new ValidationFailedException("ReprintOf", "Slips are automatic; select the original job for reprint.");
            var job = CreateJob(ticket, acceptance, kind, keyHash, hash, request, clock.GetUtcNow().UtcDateTime, ticket.TableNumber == hint.TableNumber ? area : null, orderedAt);
            await db.SaveChangesAsync(ct);
            return job;
        }, ct);
    }
    private KitchenPrintJob CreateJob(KitchenTicket ticket, BaristaAcceptance acceptance, string kind, string key, string? requestHash, CreateBarPrintRequest? request, DateTime now, string? area, DateTime? orderedAt)
    {
        IReadOnlyList<BarPrintLine> lines = ticket.Items.Select(i => new BarPrintLine(i.Id, i.ProductName, i.Note, i.Quantity)).ToArray();
        if (kind == "LABELS")
        {
            if (request?.Items is not { Count: > 0 and <= 50 } selected || selected.Select(x => x.ItemId).Distinct().Count() != selected.Count || selected.Any(x => x.Copies is < 1 or > 99) || selected.Sum(x => x.Copies) > 200)
                throw new ValidationFailedException("items", "Select 1-50 distinct items, 1-99 copies each, at most 200 labels.");
            if (selected.Any(x => ticket.Items.All(i => i.Id != x.ItemId)))
                throw new ValidationFailedException("items", "Item does not belong to this ticket.");
            lines = selected.Select(x => { var i = ticket.Items.Single(i => i.Id == x.ItemId); return new BarPrintLine(i.Id, i.ProductName, i.Note, x.Copies); }).ToArray();
        }
        var width = kind == "SLIP" ? config.GetValue("BaristaWorkflow:SlipWidthMm", 80) : config.GetValue("BaristaWorkflow:LabelWidthMm", 40);
        var height = config.GetValue("BaristaWorkflow:LabelHeightMm", 30);
        if ((kind == "SLIP" && width is not (58 or 80)) || (kind == "LABELS" && (width is < 20 or > 100 || height is < 20 or > 150)))
            throw new InvalidOperationException("Invalid configured printer dimensions.");
        var document = new BarPrintDocument("AICOS Coffee", kind, ticket.OrderCode, ticket.TableNumber is { } table ? "B\u00e0n " + table + (area is null ? "" : " - " + area) : "Mang v\u1ec1", orderedAt, acceptance.AcceptedAtUtc, acceptance.AcceptedName, width, height, lines);
        var payload = JsonSerializer.Serialize(document);
        var job = new KitchenPrintJob { Id = Guid.NewGuid(), TenantId = ticket.TenantId, BranchId = ticket.BranchId, TicketId = ticket.Id, Cycle = ticket.RemakeCount, RequestedBy = user.UserId!.Value, Kind = kind, KeyHash = key.StartsWith("auto:") ? Hash(key) : key, RequestHash = requestHash ?? acceptance.RequestHash, Payload = payload, PayloadHash = Hash(payload), TableSnapshot = ticket.TableNumber, ReprintOf = request?.ReprintOf, CreatedAtUtc = now };
        db.PrintJobs.Add(job);
        return job;
    }
    public static async Task RequireAcceptedAsync(KitchenOperationsDbContext db, KitchenTicket ticket, IConfiguration? config, CancellationToken ct)
    {
        if (config is null || !Enabled(config))
            return;
        if (!await db.BaristaAcceptances.AnyAsync(a => a.TicketId == ticket.Id && a.Cycle == ticket.RemakeCount, ct))
            throw new ConflictException("Accept this preparation cycle first.", "barista.accept_required");
    }
    public static async Task CompleteAsync(KitchenOperationsDbContext db, KitchenTicket ticket, Guid actor, DateTime now, IConfiguration? config, CancellationToken ct)
    {
        if (config is null || !Enabled(config) || ticket.Status != KitchenTicketStatus.ReadyForPickup)
            return;
        var record = await db.BaristaAcceptances.SingleAsync(a => a.TicketId == ticket.Id && a.Cycle == ticket.RemakeCount, ct);
        if (record.CompletedAtUtc is null)
        {
            record.CompletedAtUtc = now;
            record.CompletedBy = actor;
        }
    }
}
