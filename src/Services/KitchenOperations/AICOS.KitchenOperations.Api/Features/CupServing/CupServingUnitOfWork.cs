using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Serving;
using AICOS.KitchenOperations.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.KitchenOperations.Api.Features.CupServing;

public enum CupAction { Start, Ready, Claim, Pickup, Serve, Release, Renew }
public sealed record CupCommand(Guid CupId, string Key, CupAction Action, long ExpectedVersion,
    int ExpectedCycle, Guid? ClaimId = null, long? Fence = null, bool ConfirmedAtBar = false);
public sealed record CupEffect(Guid OperationId, bool Replayed, CupTask Cup, CupClaim? Claim,
    string OriginalResult, DateTime ServerTimeUtc, string? TicketHoldReason);

/// <summary>
/// Transactional core, deliberately NOT registered in HTTP/DI until admission v2 and lease policy are approved.
/// Lease duration is a trusted server policy argument, never a request field.
/// </summary>
public sealed class CupServingUnitOfWork(KitchenOperationsDbContext db, ICurrentUser user,
    TimeProvider clock, TimeSpan leaseDuration)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<CupEffect> ExecuteAsync(CupCommand command, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actor || actor == Guid.Empty)
            throw new UnauthorizedException("Staff authentication is required.");
        var tenant = TenantScope.Require(user);
        if (!Enum.IsDefined(command.Action) || command.ExpectedVersion < 0 || command.ExpectedCycle < 1
            || string.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 80)
            throw new ValidationFailedException("command", "Supply a valid operation key, action and expected state.");
        if (command.Action is CupAction.Start or CupAction.Ready) KitchenEndpoints.RequireBar(user);
        else if (!new[] { AicosRoles.Cashier, AicosRoles.BranchManager, AicosRoles.BrandAdmin }.Any(user.IsInRole))
            throw new ForbiddenException("Only counter staff may claim or serve cups.");
        if (command.Action is CupAction.Claim or CupAction.Renew && leaseDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("An approved server lease policy is required.");
        var hint = await db.CupTasks.AsNoTracking().SingleOrDefaultAsync(c => c.Id == command.CupId && c.TenantId == tenant, ct)
            ?? throw new NotFoundException("Cup", command.CupId);
        BranchAccess.EnsureCanAccess(user, hint.BranchId);
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Json)));
        return await KitchenTicketWrites.RunAsync(db, tenant, hint.OrderId, async () =>
        {
            if (db.Database.IsRelational())
            {
                var key = $"aicos:cup-operation:{tenant}:{actor}:{command.Key}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
            var ticket = await TicketLookup.ByOrderAsync(db, tenant, hint.OrderId, ct) ?? throw new NotFoundException("Ticket", hint.TicketId);
            BranchAccess.EnsureCanAccess(user, ticket.BranchId);
            var cup = await db.CupTasks.SingleAsync(c => c.Id == command.CupId && c.TenantId == tenant, ct);
            if (ticket.FulfillmentMode != FulfillmentModes.CupV1 || ticket.Id != cup.TicketId || ticket.BranchId != cup.BranchId)
                throw new ConflictException("Cup/ticket identity needs review.", "serving.identity_conflict");
            var now = db.Database.IsRelational()
                ? await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
                : clock.GetUtcNow().UtcDateTime;
            var previous = await db.CupOperations.AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == tenant
                && o.ActorId == actor && o.OperationKey == command.Key, ct);
            var claim = await db.CupClaims.SingleOrDefaultAsync(c => c.CupId == cup.Id
                && (c.State == CupClaimState.Reserved || c.State == CupClaimState.Carrying || c.State == CupClaimState.Overdue), ct);
            if (previous is not null)
            {
                if (previous.CupId != cup.Id || previous.RequestHash != requestHash)
                    throw new ConflictException("Operation key was used with another request.", "operation.key_reused");
                return new CupEffect(previous.Id, true, cup, claim, previous.ResultJson, now, ticket.RecoveryHoldReason);
            }
            ticket.RequireRecoveryClear();
            if (!ticket.IsEligibleForBar || ticket.Status is Domain.Enums.KitchenTicketStatus.Cancelled or Domain.Enums.KitchenTicketStatus.Completed)
                throw new ConflictException("Ticket is not available for cup operations.", "serving.ticket_unavailable");
            cup.Check(command.ExpectedVersion, command.ExpectedCycle);
            var before = JsonSerializer.Serialize(cup, Json); var oldVersion = cup.Version;
            if (command.Action is CupAction.Pickup or CupAction.Serve or CupAction.Release or CupAction.Renew)
            {
                if (claim is null || claim.Id != command.ClaimId || claim.Fence != command.Fence)
                    throw new ConflictException("Claim no longer matches.", "serving.stale");
            }
            switch (command.Action)
            {
                case CupAction.Start: cup.Prepare(CupPreparation.Brewing, actor, now); break;
                case CupAction.Ready: cup.Prepare(CupPreparation.Ready, actor, now); break;
                case CupAction.Claim:
                    if (claim is not null) throw new ConflictException("Cup already has a claim.", "serving.claimed");
                    claim = cup.Reserve(actor, now, now + leaseDuration); db.CupClaims.Add(claim); break;
                case CupAction.Pickup: cup.Pickup(claim!, actor, now); break;
                case CupAction.Serve: cup.Serve(claim!, actor, now); break;
                case CupAction.Release:
                    if (!command.ConfirmedAtBar) throw new ValidationFailedException("confirmedAtBar", "Confirm the cup is still at the bar.");
                    cup.Release(claim!, actor, now); break;
                case CupAction.Renew: cup.Renew(claim!, actor, now, now + leaseDuration); break;
            }
            var cups = await db.CupTasks.Where(c => c.TicketId == ticket.Id && c.TenantId == tenant && c.BranchId == ticket.BranchId).ToListAsync(ct);
            if (cups.Count != ticket.TotalCups || cups.Count is < 1 or > 4950)
                throw new ConflictException("Admission cup set is incomplete.", "serving.admission_not_ready");
            if (!cups.Select(c => c.OrderCupIndex).Order().SequenceEqual(Enumerable.Range(1, cups.Count))
                || ticket.Items.Any(i => i.OrderItemId is null || !cups.Where(c => c.TicketItemId == i.Id)
                    .Select(c => c.UnitIndex).Order().SequenceEqual(Enumerable.Range(1, i.Quantity)))
                || cups.Any(c => c.OrderId != ticket.OrderId || !ticket.Items.Any(i => i.Id == c.TicketItemId && i.OrderItemId == c.OrderItemId)
                    || c.Id != CupIdentity.Create(c.TenantId, c.OrderId, c.OrderItemId, c.UnitIndex)))
                throw new ConflictException("Cup correlation requires review.", "serving.identity_conflict");
            var manifest = await db.AdmissionReceipts.AsNoTracking().Where(r => r.TicketId == ticket.Id
                && r.TenantId == tenant && r.ConsumerKey == "kitchen-order-admission-v2" && r.ReceiptStatus == "APPLIED")
                .OrderBy(r => r.RecordedAtUtc).Select(r => r.CreationSha256).FirstOrDefaultAsync(ct);
            if (manifest is null || manifest.Length != 32)
                throw new ConflictException("V2 admission proof is required.", "serving.admission_not_ready");
            ticket.RecordCupProgress(cups, now);
            var version = ticket.AdvanceFulfillmentVersion();
            var snapshot = new FulfillmentSnapshot(Guid.NewGuid(), tenant, ticket.BranchId, cup.OrderId, ticket.Id,
                version, Convert.ToHexString(manifest), cups.Count,
                cups.Count(c => c.Service == CupService.Waiting), cups.Count(c => c.Service == CupService.Available),
                cups.Count(c => c.Service == CupService.Reserved), cups.Count(c => c.Service == CupService.Carrying),
                cups.Count(c => c.Service == CupService.Served), cups.Count(c => c.Service == CupService.Review),
                cups.All(c => c.Service == CupService.Served) ? cups.Max(c => c.ServedAtUtc) : null);
            var after = JsonSerializer.Serialize(cup, Json);
            var result = JsonSerializer.Serialize(new { Cup = cup, Claim = claim, ActorRole = user.Role }, Json);
            var operation = new CupOperation
            {
                TenantId = tenant,
                BranchId = cup.BranchId,
                CupId = cup.Id,
                ActorId = actor,
                OperationKey = command.Key,
                Command = command.Action.ToString(),
                RequestHash = requestHash,
                VersionBefore = oldVersion,
                VersionAfter = cup.Version,
                BeforeJson = before,
                AfterJson = after,
                ResultJson = result,
                CommittedAtUtc = now
            };
            db.CupOperations.Add(operation);
            db.FulfillmentOutbox.Add(new FulfillmentOutbox
            {
                EventId = snapshot.EventId,
                TenantId = tenant,
                BranchId = cup.BranchId,
                OrderId = cup.OrderId,
                SourceVersion = version,
                Payload = JsonSerializer.Serialize(snapshot),
                PayloadHash = FulfillmentSnapshotHash.Compute(snapshot),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            return new CupEffect(operation.Id, false, cup, claim, result, now, ticket.RecoveryHoldReason);
        }, ct);
    }
}
