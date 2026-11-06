using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.KitchenOperations.Api.Persistence;

namespace AICOS.KitchenOperations.Api.Features;

public static class KitchenStickerEndpoints
{
    public static void MapKitchenStickerPreview(this RouteGroupBuilder group) =>
        group.MapGet("/tickets/{id:guid}/sticker-preview", PreviewAsync)
            .RequireAuthorization(policy => policy.RequireRole(KitchenEndpoints.BarRoles));

    // This endpoint never generates rows, sends print events or claims a printer succeeded.
    private static async Task<IResult> PreviewAsync(Guid id, KitchenOperationsDbContext db,
        ICurrentUser user, CancellationToken ct)
    {
        var ticket = await TicketAccess.LoadAsync(db, user, id, ct, track: false);
        ticket.RequireWholeOrderOperation(); // CupV1 preview needs the approved v2 admission/correlation path.
        ticket.RequireRecoveryClear();
        if (!ticket.IsEligibleForBar)
            throw new BusinessRuleException("ticket.not_paid", "Stickers require a paid or dine-in order.");
        var stickers = ticket.Stickers.OrderBy(s => s.CupIndex).ToArray();
        if (stickers.Length != ticket.TotalCups || !stickers.Select(s => s.CupIndex)
                .SequenceEqual(Enumerable.Range(1, ticket.TotalCups)))
            throw new ConflictException("Sticker data requires review.", "ticket.stickers_incomplete");
        return Results.Ok(new
        {
            TicketId = ticket.Id, ticket.OrderCode, ticket.TableNumber, ticket.OrderType,
            TotalStickers = stickers.Length, PrintStatus = "UNKNOWN", CanPrint = false,
            Stickers = stickers.Select(s => new
            {
                StickerId = s.Id, s.OrderId, s.OrderCode, ticket.TableNumber, s.ProductName,
                s.CupIndex, s.TotalCupsInOrder, s.Customizations,
                // Legacy PrintedAtUtc stores generation time, not hardware confirmation.
                GeneratedAtUtc = s.PrintedAtUtc
            })
        });
    }
}
