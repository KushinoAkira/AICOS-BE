using AICOS.CustomerEngagement.Api.Domain;
using AICOS.CustomerEngagement.Api.Infrastructure;
using AICOS.CustomerEngagement.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.CustomerEngagement.Api.Features.Campaigns;

public sealed class CrmOptions
{
    public const string SectionName = "Crm";

    public int WinBackDays { get; set; } = 30;

    public int BirthdayDiscountPercent { get; set; } = 15;

    public int WinBackDiscountPercent { get; set; } = 10;

    public int VoucherValidDays { get; set; } = 7;

    public int SweepIntervalHours { get; set; } = 1;
}

/// <summary>Birthday + win-back emails (MarketingOptIn only). Issues a personal voucher and logs CrmCampaignSend.</summary>
public sealed class CrmCampaignJob(
    IServiceScopeFactory scopes,
    IOptions<CrmOptions> options,
    ILogger<CrmCampaignJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Clamp(options.Value.SweepIntervalHours, 1, 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<CrmCampaignRunner>();
                var sent = await runner.RunAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("CRM campaign sent {Count} emails.", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CRM campaign job failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class CrmCampaignRunner(
    CustomerEngagementDbContext db,
    IEmailSender email,
    IOptions<CrmOptions> options,
    TimeProvider clock,
    ILogger<CrmCampaignRunner> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var opt = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var sent = 0;

        sent += await SendBirthdaysAsync(today, now, opt, ct);
        sent += await SendWinBacksAsync(today, now, opt, ct);
        return sent;
    }

    private async Task<int> SendBirthdaysAsync(DateOnly today, DateTime now, CrmOptions opt, CancellationToken ct)
    {
        var yearStart = new DateOnly(today.Year, 1, 1);
        var candidates = await db.Customers
            .Where(c => c.MarketingOptIn && c.Birthday != null
                && c.Birthday.Value.Month == today.Month && c.Birthday.Value.Day == today.Day)
            .ToListAsync(ct);
        var count = 0;
        foreach (var c in candidates)
        {
            if (await db.CrmCampaignSends.AnyAsync(
                    s => s.CustomerId == c.Id && s.Type == CrmCampaignType.Birthday && s.SentOn >= yearStart, ct))
            {
                continue;
            }

            var code = $"SN{today:yyyy}{c.Id.ToString("N")[..6]}".ToUpperInvariant();
            if (!await EnsureVoucherAsync(c, code, opt.BirthdayDiscountPercent, opt.VoucherValidDays, now, ct))
            {
                continue;
            }

            try
            {
                await email.SendAsync(CrmEmailTemplates.Birthday(c.Email, c.FullName, code, opt.BirthdayDiscountPercent, now.AddDays(opt.VoucherValidDays)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Birthday email failed for customer {CustomerId}", c.Id);
                continue;
            }

            db.CrmCampaignSends.Add(new CrmCampaignSend(c.TenantId, c.Id, CrmCampaignType.Birthday, today, code));
            await db.SaveChangesAsync(ct);
            count++;
        }

        return count;
    }

    private async Task<int> SendWinBacksAsync(DateOnly today, DateTime now, CrmOptions opt, CancellationToken ct)
    {
        var cutoff = now.AddDays(-Math.Max(7, opt.WinBackDays));
        var coolDown = today.AddDays(-Math.Max(7, opt.WinBackDays));
        var candidates = await db.Customers
            .Where(c => c.MarketingOptIn && c.LastPaidAtUtc != null && c.LastPaidAtUtc < cutoff)
            .ToListAsync(ct);
        var count = 0;
        foreach (var c in candidates)
        {
            if (await db.CrmCampaignSends.AnyAsync(
                    s => s.CustomerId == c.Id && s.Type == CrmCampaignType.WinBack && s.SentOn >= coolDown, ct))
            {
                continue;
            }

            var code = $"WB{today:yyMM}{c.Id.ToString("N")[..6]}".ToUpperInvariant();
            if (!await EnsureVoucherAsync(c, code, opt.WinBackDiscountPercent, opt.VoucherValidDays, now, ct))
            {
                continue;
            }

            try
            {
                await email.SendAsync(CrmEmailTemplates.WinBack(c.Email, c.FullName, code, opt.WinBackDiscountPercent, now.AddDays(opt.VoucherValidDays)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Win-back email failed for customer {CustomerId}", c.Id);
                continue;
            }

            db.CrmCampaignSends.Add(new CrmCampaignSend(c.TenantId, c.Id, CrmCampaignType.WinBack, today, code));
            await db.SaveChangesAsync(ct);
            count++;
        }

        return count;
    }

    private async Task<bool> EnsureVoucherAsync(Customer c, string code, int percent, int days, DateTime now, CancellationToken ct)
    {
        if (await db.Vouchers.AnyAsync(v => v.TenantId == c.TenantId && v.Code == code, ct))
        {
            return true;
        }

        db.Vouchers.Add(new Voucher(
            c.TenantId, branchId: null, code, VoucherType.Voucher, VoucherDiscountType.Percentage, percent,
            minOrderAmount: 30_000, expiresAtUtc: now.AddDays(days), issuedToName: c.FullName, issuedToCustomerId: c.Id));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Voucher {Code} already existed concurrently", code);
            return await db.Vouchers.AnyAsync(v => v.TenantId == c.TenantId && v.Code == code, ct);
        }
    }
}
