using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Exceptions;

namespace AICOS.Ordering.Api.Services;

/// <summary>A product as the Menu service sells it at a branch right now. The price here is the one the customer pays.</summary>
public sealed record MenuOptionInfo(Guid Id, string Name, decimal PriceDelta, bool IsDefault, Guid? RequiresOptionId);

/// <summary>An option group of a product (size, sugar, toppings): pick one or several, possibly required.</summary>
public sealed record MenuOptionGroupInfo(Guid Id, string Name, bool IsMultiple, bool IsRequired, int? MaxSelections, IReadOnlyList<MenuOptionInfo> Options);

public sealed record MenuProductInfo(Guid Id, string Name, decimal Price, IReadOnlyList<MenuOptionGroupInfo>? OptionGroups = null);

/// <summary>Menu Service: what a branch sells and at which price. Prices always come from here, never from the client request.</summary>
public interface IMenuCatalogClient
{
    /// <summary>The products a branch sells now (published and not sold out), by product id.</summary>
    Task<IReadOnlyDictionary<Guid, MenuProductInfo>> GetSellableProductsAsync(Guid branchId, CancellationToken ct);
}

public sealed record VoucherQuote(string Code, decimal Discount);

/// <summary>Customer Service vouchers. Redeeming happens when the order is placed, so one voucher cannot pay two orders.</summary>
public interface IVoucherClient
{
    /// <summary>The branch and the customer of the order let Customer Service refuse a voucher of another branch or of another member.</summary>
    Task<VoucherQuote> RedeemAsync(string code, decimal orderAmount, string orderReference, Guid branchId, Guid? customerId, CancellationToken ct);
}

public sealed record RefundRequestResult(string Status);

/// <summary>Payment Service: asks for money to go back when a manager cancels a paid order.</summary>
public interface IPaymentClient
{
    Task<RefundRequestResult> RequestRefundAsync(Guid paymentId, string kind, decimal? amount, string reason, CancellationToken ct);
}

internal static class ServiceCall
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<HttpResponseMessage> SendAsync(string service, Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException(service);
        }
    }

    /// <summary>Turns an error answer of another service into this service's own error, keeping its message.</summary>
    public static async Task<Exception> ErrorAsync(string service, string fallbackCode, HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        string? detail = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            code = body.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
            detail = body.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // the other service did not send a problem document
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new ForbiddenException(detail ?? "You cannot do that.", code ?? "forbidden"),
            HttpStatusCode.NotFound => new NotFoundException(service, "requested item"),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict =>
                new BusinessRuleException(code ?? fallbackCode, detail ?? "The request was refused."),
            _ => new DependencyUnavailableException(service),
        };
    }
}

internal sealed class HttpMenuCatalogClient(HttpClient http) : IMenuCatalogClient
{
    public async Task<IReadOnlyDictionary<Guid, MenuProductInfo>> GetSellableProductsAsync(Guid branchId, CancellationToken ct)
    {
        using var response = await ServiceCall.SendAsync("menu", () => http.GetAsync($"/api/v1/menu/public/branches/{branchId}/menu", ct), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ServiceCall.ErrorAsync("menu", "menu.unavailable", response, ct);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ServiceCall.Json, ct);
        var products = new Dictionary<Guid, MenuProductInfo>();
        if (body.TryGetProperty("categories", out var categories))
        {
            foreach (var category in categories.EnumerateArray())
            {
                foreach (var product in category.GetProperty("products").EnumerateArray())
                {
                    var available = !product.TryGetProperty("isAvailable", out var flag) || flag.GetBoolean();
                    if (available)
                    {
                        var id = product.GetProperty("id").GetGuid();
                        products[id] = new MenuProductInfo(id, product.GetProperty("name").GetString() ?? string.Empty, product.GetProperty("price").GetDecimal(), ParseGroups(product));
                    }
                }
            }
        }

        return products;
    }

    private static List<MenuOptionGroupInfo> ParseGroups(JsonElement product)
    {
        var groups = new List<MenuOptionGroupInfo>();
        if (!product.TryGetProperty("optionGroups", out var raw) || raw.ValueKind != JsonValueKind.Array)
        {
            return groups;
        }

        foreach (var g in raw.EnumerateArray())
        {
            var selection = g.GetProperty("selectionType");
            var multiple = selection.ValueKind == JsonValueKind.String
                ? string.Equals(selection.GetString(), "Multiple", StringComparison.OrdinalIgnoreCase)
                : selection.GetInt32() == 1;
            var options = g.GetProperty("options").EnumerateArray().Select(o => new MenuOptionInfo(
                o.GetProperty("id").GetGuid(),
                o.GetProperty("name").GetString() ?? string.Empty,
                o.GetProperty("priceDelta").GetDecimal(),
                o.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True,
                o.TryGetProperty("requiresOptionId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetGuid() : null)).ToList();
            groups.Add(new MenuOptionGroupInfo(
                g.GetProperty("id").GetGuid(),
                g.GetProperty("name").GetString() ?? string.Empty,
                multiple,
                g.TryGetProperty("isRequired", out var req) && req.ValueKind == JsonValueKind.True,
                g.TryGetProperty("maxSelections", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null,
                options));
        }

        return groups;
    }
}

internal sealed class HttpVoucherClient(HttpClient http) : IVoucherClient
{
    public async Task<VoucherQuote> RedeemAsync(string code, decimal orderAmount, string orderReference, Guid branchId, Guid? customerId, CancellationToken ct)
    {
        using var response = await ServiceCall.SendAsync(
            "customer", () => http.PostAsJsonAsync("/api/v1/customers/vouchers/redeem", new { code, orderAmount, orderReference, branchId, customerId }, ct), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ServiceCall.ErrorAsync("customer", "voucher.invalid", response, ct);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ServiceCall.Json, ct);
        return new VoucherQuote(body.GetProperty("code").GetString() ?? code, body.GetProperty("discount").GetDecimal());
    }
}

internal sealed class HttpPaymentClient(HttpClient http) : IPaymentClient
{
    public async Task<RefundRequestResult> RequestRefundAsync(Guid paymentId, string kind, decimal? amount, string reason, CancellationToken ct)
    {
        using var response = await ServiceCall.SendAsync(
            "payment", () => http.PostAsJsonAsync($"/api/v1/payments/transactions/{paymentId}/refunds", new { kind, amount, reason }, ct), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ServiceCall.ErrorAsync("payment", "refund.refused", response, ct);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ServiceCall.Json, ct);
        return new RefundRequestResult(body.GetProperty("status").GetString() ?? "PendingApproval");
    }
}

/// <summary>What the Table service tells about a scanned table QR code (UC-49): brand, branch, table and whether ordering is open.</summary>
public sealed record TableQrInfo(Guid TenantId, Guid BranchId, Guid TableId, string TableCode, string AreaName, bool CanOrder);

/// <summary>Table Service: resolves the token of a scanned table QR code. Public, so no bearer token is needed.</summary>
public interface ITableQrClient
{
    /// <summary>Null when the code is unknown (a printed code that was replaced stops working at once).</summary>
    Task<TableQrInfo?> LookupAsync(string qrToken, CancellationToken ct);
}

internal sealed class HttpTableQrClient(HttpClient http) : ITableQrClient
{
    public async Task<TableQrInfo?> LookupAsync(string qrToken, CancellationToken ct)
    {
        using var response = await ServiceCall.SendAsync("table", () => http.GetAsync($"/api/v1/tables/public/qr/{Uri.EscapeDataString(qrToken.Trim())}", ct), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ServiceCall.ErrorAsync("table", "table.unavailable", response, ct);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ServiceCall.Json, ct);
        return new TableQrInfo(
            body.GetProperty("tenantId").GetGuid(),
            body.GetProperty("branchId").GetGuid(),
            body.GetProperty("tableId").GetGuid(),
            body.GetProperty("tableCode").GetString() ?? string.Empty,
            body.TryGetProperty("areaName", out var area) ? area.GetString() ?? string.Empty : string.Empty,
            !body.TryGetProperty("canOrder", out var can) || can.GetBoolean());
    }
}
