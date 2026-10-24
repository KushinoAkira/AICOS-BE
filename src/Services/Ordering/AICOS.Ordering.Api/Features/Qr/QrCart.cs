using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AICOS.BuildingBlocks.Caching;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Features.Orders;
using AICOS.Ordering.Api.Services;

namespace AICOS.Ordering.Api.Features.Qr;

/// <summary>The token a table gives to the people sitting at it. It is derived from the session id and a server secret, so it needs no storage and cannot be guessed.</summary>
public sealed class GuestSessionTokens(IConfiguration configuration)
{
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(
        "aicos-qr-session|" + (configuration["Ordering:Qr:SessionSecret"] ?? configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Set Ordering:Qr:SessionSecret or Jwt:SigningKey."))));

    public string Create(Guid sessionId)
    {
        var id = sessionId.ToString("N");
        return $"{id}.{Convert.ToBase64String(Sign(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    public bool TryRead(string? token, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        var parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id))
        {
            return false;
        }

        var expected = Convert.ToBase64String(Sign(parts[0])).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1])))
        {
            return false;
        }

        sessionId = id;
        return true;
    }

    private byte[] Sign(string id) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(id));
}

/// <summary>One line of a table cart as stored: what was chosen, not what it costs (the price is always read from Menu).</summary>
public sealed record QrCartLine(Guid LineId, Guid ProductId, int Quantity, string? Note, IReadOnlyList<Guid> OptionIds);

/// <summary>
/// The shared cart of a table, kept in Redis as one hash per session: field <c>m:{line}</c> holds the choice, <c>q:{line}</c> the quantity.
/// Each line is its own field and the quantity is changed with an atomic increment, so two phones at the same table cannot overwrite each other.
/// The same product with the same note and options is the same line. The cart expires after <see cref="Ttl"/> without a change.
/// </summary>
public sealed class QrCartStore(IHashStore store)
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 99;
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    private sealed record Meta(Guid ProductId, string? Note, List<Guid> OptionIds);

    private static string Key(Guid tenantId, Guid sessionId) => $"t:{tenantId:N}:qrcart:{sessionId:N}";

    public static Guid LineIdOf(Guid productId, string? note, IEnumerable<Guid> optionIds)
    {
        var signature = $"{productId:N}|{note}|{string.Join(",", optionIds.Distinct().Order())}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(signature))[..16]);
    }

    public async Task<IReadOnlyList<QrCartLine>> GetAsync(Guid tenantId, Guid sessionId, CancellationToken ct) =>
        ToLines(await store.GetAllAsync(Key(tenantId, sessionId), ct));

    public async Task<Guid> AddAsync(Guid tenantId, Guid sessionId, Guid productId, int quantity, string? note, IEnumerable<Guid>? optionIds, CancellationToken ct)
    {
        var key = Key(tenantId, sessionId);
        var options = (optionIds ?? []).Distinct().Order().ToList();
        var lineId = LineIdOf(productId, note, options);
        var existing = await store.GetAllAsync(key, ct);
        if (!existing.ContainsKey($"m:{lineId:N}") && existing.Keys.Count(k => k.StartsWith("m:", StringComparison.Ordinal)) >= MaxLines)
        {
            throw new BusinessRuleException("qr.cart_full", $"The cart holds at most {MaxLines} different items.");
        }

        await store.SetFieldAsync(key, $"m:{lineId:N}", JsonSerializer.Serialize(new Meta(productId, note, options)), Ttl, ct);
        var total = await store.IncrementFieldAsync(key, $"q:{lineId:N}", quantity, Ttl, ct);
        if (total > MaxQuantity)
        {
            await store.SetFieldAsync(key, $"q:{lineId:N}", MaxQuantity.ToString(), Ttl, ct);
        }

        return lineId;
    }

    /// <summary>Sets the quantity of a line; 0 removes it. False when the line is not in the cart.</summary>
    public async Task<bool> SetQuantityAsync(Guid tenantId, Guid sessionId, Guid lineId, int quantity, CancellationToken ct)
    {
        var key = Key(tenantId, sessionId);
        if (!(await store.GetAllAsync(key, ct)).ContainsKey($"m:{lineId:N}"))
        {
            return false;
        }

        if (quantity <= 0)
        {
            await store.DeleteFieldsAsync(key, [$"m:{lineId:N}", $"q:{lineId:N}"], ct);
        }
        else
        {
            await store.SetFieldAsync(key, $"q:{lineId:N}", Math.Min(quantity, MaxQuantity).ToString(), Ttl, ct);
        }

        return true;
    }

    public Task ClearAsync(Guid tenantId, Guid sessionId, CancellationToken ct) => store.DeleteAsync(Key(tenantId, sessionId), ct);

    /// <summary>Empties the cart and returns what was in it, atomically: of two checkouts at the same moment only one gets the lines.</summary>
    public async Task<IReadOnlyList<QrCartLine>> TakeAsync(Guid tenantId, Guid sessionId, CancellationToken ct) =>
        ToLines(await store.TakeAllAsync(Key(tenantId, sessionId), ct));

    /// <summary>Puts lines back after a checkout that could not go through, adding to whatever was put in meanwhile.</summary>
    public async Task RestoreAsync(Guid tenantId, Guid sessionId, IReadOnlyList<QrCartLine> lines, CancellationToken ct)
    {
        var key = Key(tenantId, sessionId);
        foreach (var line in lines)
        {
            await store.SetFieldAsync(key, $"m:{line.LineId:N}", JsonSerializer.Serialize(new Meta(line.ProductId, line.Note, line.OptionIds.ToList())), Ttl, ct);
            await store.IncrementFieldAsync(key, $"q:{line.LineId:N}", line.Quantity, Ttl, ct);
        }
    }

    private static IReadOnlyList<QrCartLine> ToLines(IReadOnlyDictionary<string, string> fields)
    {
        var lines = new List<QrCartLine>();
        foreach (var (name, json) in fields.Where(f => f.Key.StartsWith("m:", StringComparison.Ordinal)))
        {
            var id = name[2..];
            if (!Guid.TryParseExact(id, "N", out var lineId)
                || !fields.TryGetValue($"q:{id}", out var raw) || !int.TryParse(raw, out var quantity) || quantity <= 0)
            {
                continue;
            }

            var meta = JsonSerializer.Deserialize<Meta>(json);
            if (meta is not null)
            {
                lines.Add(new QrCartLine(lineId, meta.ProductId, Math.Min(quantity, MaxQuantity), meta.Note, meta.OptionIds));
            }
        }

        return lines.OrderBy(l => l.LineId).ToList();
    }
}

/// <summary>A cart line with name, options and price filled in from the menu; <c>Problem</c> says why it cannot be ordered right now.</summary>
public sealed record QrCartLineView(
    Guid LineId, Guid ProductId, string ProductName, int Quantity, string? Note, decimal UnitPrice, decimal TotalPrice,
    IReadOnlyList<OrderItemOptionResponse> Options, string? Problem);

public sealed record QrCartView(Guid SessionId, IReadOnlyList<QrCartLineView> Lines, int ItemCount, decimal Total, bool HasProblems);

public static class QrCartPricing
{
    /// <summary>Prices a cart with the menu of the branch as it is now. A product that is sold out, or options that changed, are flagged, never priced from the client.</summary>
    public static QrCartView Price(Guid sessionId, IReadOnlyList<QrCartLine> lines, IReadOnlyDictionary<Guid, MenuProductInfo> menu)
    {
        var views = new List<QrCartLineView>();
        foreach (var line in lines)
        {
            if (!menu.TryGetValue(line.ProductId, out var product))
            {
                views.Add(new QrCartLineView(line.LineId, line.ProductId, "(unavailable)", line.Quantity, line.Note, 0, 0, [], "qr.item_unavailable"));
                continue;
            }

            try
            {
                var priced = OrderOptions.Resolve(product, line.OptionIds);
                var unit = product.Price + priced.Surcharge;
                views.Add(new QrCartLineView(
                    line.LineId, product.Id, product.Name, line.Quantity, line.Note, unit, unit * line.Quantity,
                    priced.Options.Select(o => new OrderItemOptionResponse(o.OptionId, o.GroupName, o.Name, o.PriceDelta)).ToList(), null));
            }
            catch (BusinessRuleException ex)
            {
                views.Add(new QrCartLineView(line.LineId, product.Id, product.Name, line.Quantity, line.Note, product.Price, 0, [], ex.ErrorCode));
            }
        }

        return new QrCartView(sessionId, views, views.Sum(v => v.Quantity), views.Where(v => v.Problem is null).Sum(v => v.TotalPrice), views.Any(v => v.Problem is not null));
    }
}
