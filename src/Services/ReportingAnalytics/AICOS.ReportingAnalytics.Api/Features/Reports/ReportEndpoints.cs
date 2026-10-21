using System.Globalization;
using System.Text;
using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.ReportingAnalytics.Api.Domain;
using AICOS.ReportingAnalytics.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AICOS.ReportingAnalytics.Api.Features.Reports;

public sealed record RevenueTotals(int Orders, int PaidOrders, decimal GrossRevenue, decimal Refunded, decimal NetRevenue, decimal AverageOrderValue);

public sealed record BranchRevenue(Guid BranchId, string BranchName, int Orders, int PaidOrders, decimal GrossRevenue, decimal Refunded, decimal NetRevenue, decimal AverageOrderValue, double RevenueShare);

public sealed record ChainReport(DateOnly From, DateOnly To, RevenueTotals Totals, List<BranchRevenue> Branches);

public sealed record MethodRevenue(string Method, int Orders, decimal GrossRevenue, decimal Refunded, decimal NetRevenue, double Share);

public sealed record TypeCount(string OrderType, int Orders);

public sealed record HourCount(int Hour, int Orders);

public sealed record DayRevenue(DateOnly Date, int Orders, decimal GrossRevenue);

public sealed record BranchReport(
    DateOnly From,
    DateOnly To,
    Guid BranchId,
    string BranchName,
    RevenueTotals Totals,
    List<MethodRevenue> PaymentMethods,
    List<TypeCount> OrderTypes,
    List<HourCount> Hours,
    List<HourCount> PeakHours,
    List<DayRevenue> Days);

/// <summary>
/// Chain and branch reports (SCRUM-50, FR-19, FR-31): orders, revenue, payment methods and peak hours, from the facts built from order and
/// payment events. Routes: /api/v1/reporting. The Brand Admin sees the whole chain or any branch; a Branch Manager only their branch.
/// Revenue is counted on the day the money arrived, orders on the day they were placed; days follow the shop time zone.
/// </summary>
public static class ReportEndpoints
{
    public const int MaxDays = 93;

    public static void MapReportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(string.Empty).WithTags("Reports").RequireAuthorization(AicosPolicies.Management);

        group.MapGet("/chain", ChainAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapGet("/chain/export", ChainExportAsync).RequireAuthorization(AicosPolicies.AdminOnly);
        group.MapGet("/branch", BranchAsync);
        group.MapGet("/branch/export", BranchExportAsync);
        group.MapGet("/payment-methods", PaymentMethodsAsync);
        group.MapGet("/peak-hours", PeakHoursAsync);
    }

    // ------------------------------------------------------------------ chain
    private static async Task<ChainReport> ChainAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        await BuildChainAsync(db, user, directory, clock, from, to, ct);

    private static async Task<ChainReport> BuildChainAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var (fromDate, toDate, startUtc, endUtc) = clock.Range(from, to);
        var facts = await LoadAsync(db, tenantId, null, startUtc, endUtc, ct);

        var rows = new List<BranchRevenue>();
        foreach (var group in facts.GroupBy(f => f.BranchId))
        {
            var totals = Totals(group.ToList(), startUtc, endUtc);
            rows.Add(new BranchRevenue(group.Key, await NameAsync(directory, group.Key, ct), totals.Orders, totals.PaidOrders, totals.GrossRevenue, totals.Refunded,
                totals.NetRevenue, totals.AverageOrderValue, 0));
        }

        var all = Totals(facts, startUtc, endUtc);
        rows = rows.Select(r => r with { RevenueShare = all.NetRevenue == 0 ? 0 : Math.Round((double)(r.NetRevenue / all.NetRevenue) * 100, 1) })
            .OrderByDescending(r => r.NetRevenue).ThenBy(r => r.BranchName).ToList();
        return new ChainReport(fromDate, toDate, all, rows);
    }

    private static async Task<IResult> ChainExportAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var report = await BuildChainAsync(db, user, directory, clock, from, to, ct);
        var csv = new StringBuilder("Branch,Orders,Paid orders,Gross revenue,Refunded,Net revenue,Average order value,Revenue share %\n");
        foreach (var b in report.Branches)
        {
            csv.AppendLine(string.Join(',', Escape(b.BranchName), b.Orders, b.PaidOrders, N(b.GrossRevenue), N(b.Refunded), N(b.NetRevenue), N(b.AverageOrderValue), N(b.RevenueShare)));
        }

        csv.AppendLine(string.Join(',', "TOTAL", report.Totals.Orders, report.Totals.PaidOrders, N(report.Totals.GrossRevenue), N(report.Totals.Refunded), N(report.Totals.NetRevenue),
            N(report.Totals.AverageOrderValue), "100"));
        return File(csv, $"chain-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    // ------------------------------------------------------------------ branch
    private static async Task<BranchReport> BranchAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        await BuildBranchAsync(db, user, directory, clock, branchId, from, to, ct);

    private static async Task<BranchReport> BuildBranchAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        var branch = await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        var (fromDate, toDate, startUtc, endUtc) = clock.Range(from, to);
        var facts = await LoadAsync(db, tenantId, branch, startUtc, endUtc, ct);

        var hours = Hours(facts, startUtc, endUtc, clock);
        return new BranchReport(
            fromDate, toDate, branch, await NameAsync(directory, branch, ct), Totals(facts, startUtc, endUtc), Methods(facts, startUtc, endUtc),
            facts.Where(f => Placed(f, startUtc, endUtc)).GroupBy(f => f.OrderType).OrderByDescending(g => g.Count()).Select(g => new TypeCount(g.Key, g.Count())).ToList(),
            hours, hours.Where(h => h.Orders > 0).OrderByDescending(h => h.Orders).ThenBy(h => h.Hour).Take(3).ToList(), Days(facts, startUtc, endUtc, clock));
    }

    private static async Task<IResult> BranchExportAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var report = await BuildBranchAsync(db, user, directory, clock, branchId, from, to, ct);
        var csv = new StringBuilder();
        csv.AppendLine($"Branch,{Escape(report.BranchName)}");
        csv.AppendLine($"Period,{report.From:yyyy-MM-dd},{report.To:yyyy-MM-dd}");
        csv.AppendLine("Orders,Paid orders,Gross revenue,Refunded,Net revenue,Average order value");
        csv.AppendLine(string.Join(',', report.Totals.Orders, report.Totals.PaidOrders, N(report.Totals.GrossRevenue), N(report.Totals.Refunded), N(report.Totals.NetRevenue), N(report.Totals.AverageOrderValue)));
        csv.AppendLine();
        csv.AppendLine("Payment method,Orders,Gross revenue,Refunded,Net revenue,Share %");
        foreach (var m in report.PaymentMethods)
        {
            csv.AppendLine(string.Join(',', Escape(m.Method), m.Orders, N(m.GrossRevenue), N(m.Refunded), N(m.NetRevenue), N(m.Share)));
        }

        csv.AppendLine();
        csv.AppendLine("Hour,Orders");
        foreach (var h in report.Hours)
        {
            csv.AppendLine($"{h.Hour:00}:00,{h.Orders}");
        }

        csv.AppendLine();
        csv.AppendLine("Date,Orders,Gross revenue");
        foreach (var d in report.Days)
        {
            csv.AppendLine($"{d.Date:yyyy-MM-dd},{d.Orders},{N(d.GrossRevenue)}");
        }

        return File(csv, $"branch-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    // ------------------------------------------------------------------ methods and peak hours (the whole chain for a Brand Admin who names no branch)
    private static async Task<List<MethodRevenue>> PaymentMethodsAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (facts, startUtc, endUtc, _) = await ScopedAsync(db, user, directory, clock, branchId, from, to, ct);
        return Methods(facts, startUtc, endUtc);
    }

    private static async Task<List<HourCount>> PeakHoursAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (facts, startUtc, endUtc, _) = await ScopedAsync(db, user, directory, clock, branchId, from, to, ct);
        return Hours(facts, startUtc, endUtc, clock);
    }

    private static async Task<(List<OrderFact> Facts, DateTime StartUtc, DateTime EndUtc, Guid? Branch)> ScopedAsync(
        ReportingAnalyticsDbContext db, ICurrentUser user, IBranchDirectory directory, ReportClock clock, Guid? branchId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var tenantId = TenantScope.Require(user);
        Guid? branch = null;
        if (!(user.IsInRole(AicosRoles.BrandAdmin) && branchId is null))
        {
            branch = await BranchScope.ResolveAsync(user, directory, branchId, requireActive: false, ct);
        }

        var (_, _, startUtc, endUtc) = clock.Range(from, to);
        return (await LoadAsync(db, tenantId, branch, startUtc, endUtc, ct), startUtc, endUtc, branch);
    }

    // ------------------------------------------------------------------ calculation
    /// <summary>The facts that count for the period: placed in it, or paid in it.</summary>
    private static async Task<List<OrderFact>> LoadAsync(ReportingAnalyticsDbContext db, Guid tenantId, Guid? branch, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var query = db.OrderFacts.AsNoTracking().Where(f => f.TenantId == tenantId
            && ((f.PlacedAtUtc >= startUtc && f.PlacedAtUtc < endUtc) || (f.PaidAtUtc >= startUtc && f.PaidAtUtc < endUtc)));
        if (branch is not null)
        {
            query = query.Where(f => f.BranchId == branch);
        }

        return await query.ToListAsync(ct);
    }

    private static bool Placed(OrderFact f, DateTime startUtc, DateTime endUtc) => f.Counts && f.PlacedAtUtc >= startUtc && f.PlacedAtUtc < endUtc;

    private static bool PaidIn(OrderFact f, DateTime startUtc, DateTime endUtc) => f.PaidAtUtc >= startUtc && f.PaidAtUtc < endUtc;

    private static RevenueTotals Totals(List<OrderFact> facts, DateTime startUtc, DateTime endUtc)
    {
        var orders = facts.Count(f => Placed(f, startUtc, endUtc));
        var paid = facts.Where(f => PaidIn(f, startUtc, endUtc)).ToList();
        var gross = paid.Sum(f => f.PaidAmount);
        var refunded = paid.Sum(f => f.RefundedAmount);
        return new RevenueTotals(orders, paid.Count, gross, refunded, gross - refunded, paid.Count == 0 ? 0 : Math.Round(gross / paid.Count, 0));
    }

    private static List<MethodRevenue> Methods(List<OrderFact> facts, DateTime startUtc, DateTime endUtc)
    {
        var paid = facts.Where(f => PaidIn(f, startUtc, endUtc)).ToList();
        var net = paid.Sum(f => f.PaidAmount - f.RefundedAmount);
        return paid.GroupBy(f => f.PaymentMethod ?? "Unknown")
            .Select(g =>
            {
                var gross = g.Sum(f => f.PaidAmount);
                var refunded = g.Sum(f => f.RefundedAmount);
                return new MethodRevenue(g.Key, g.Count(), gross, refunded, gross - refunded, net == 0 ? 0 : Math.Round((double)((gross - refunded) / net) * 100, 1));
            })
            .OrderByDescending(m => m.NetRevenue).ThenBy(m => m.Method).ToList();
    }

    /// <summary>Orders placed per hour of the shop day, all 24 hours (peak hours are the busiest ones).</summary>
    private static List<HourCount> Hours(List<OrderFact> facts, DateTime startUtc, DateTime endUtc, ReportClock clock)
    {
        var counts = new int[24];
        foreach (var f in facts.Where(f => Placed(f, startUtc, endUtc)))
        {
            counts[clock.ToLocal(f.PlacedAtUtc!.Value).Hour]++;
        }

        return counts.Select((n, hour) => new HourCount(hour, n)).ToList();
    }

    private static List<DayRevenue> Days(List<OrderFact> facts, DateTime startUtc, DateTime endUtc, ReportClock clock)
    {
        var orders = facts.Where(f => Placed(f, startUtc, endUtc)).GroupBy(f => DateOnly.FromDateTime(clock.ToLocal(f.PlacedAtUtc!.Value))).ToDictionary(g => g.Key, g => g.Count());
        var revenue = facts.Where(f => PaidIn(f, startUtc, endUtc)).GroupBy(f => DateOnly.FromDateTime(clock.ToLocal(f.PaidAtUtc!.Value))).ToDictionary(g => g.Key, g => g.Sum(f => f.PaidAmount));
        return orders.Keys.Union(revenue.Keys).OrderBy(d => d).Select(d => new DayRevenue(d, orders.GetValueOrDefault(d), revenue.GetValueOrDefault(d))).ToList();
    }

    private static async Task<string> NameAsync(IBranchDirectory directory, Guid branchId, CancellationToken ct)
    {
        try
        {
            var info = await directory.GetAsync(branchId, ct);
            return string.IsNullOrWhiteSpace(info.Name) ? $"Branch {branchId.ToString()[..8]}" : info.Name;
        }
        catch (Exception ex) when (ex is AppException)
        {
            return $"Branch {branchId.ToString()[..8]}";
        }
    }

    private static IResult File(StringBuilder csv, string name) =>
        Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", name);

    private static string N(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Quotes a text field and neutralises spreadsheet formulas, so a branch name cannot run code when the file is opened.</summary>
    private static string Escape(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}

/// <summary>The shop time zone (default Asia/Ho_Chi_Minh, <c>Reporting:TimeZone</c>) and the date range of a report.</summary>
public sealed class ReportClock(TimeProvider clock, IConfiguration configuration)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Reporting:TimeZone"] ?? "Asia/Ho_Chi_Minh");

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    public DateOnly Today => DateOnly.FromDateTime(ToLocal(clock.GetUtcNow().UtcDateTime));

    /// <summary>The first and last day (both included; default today) and the matching UTC interval.</summary>
    public (DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtc) Range(DateOnly? from, DateOnly? to)
    {
        var start = from ?? to ?? Today;
        var end = to ?? from ?? Today;
        if (end < start)
        {
            throw new ValidationFailedException("to", "The end date is before the start date.");
        }

        if (end.DayNumber - start.DayNumber >= ReportEndpoints.MaxDays)
        {
            throw new ValidationFailedException("to", $"A report covers at most {ReportEndpoints.MaxDays} days.");
        }

        return (start, end,
            TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone),
            TimeZoneInfo.ConvertTimeToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone));
    }
}
