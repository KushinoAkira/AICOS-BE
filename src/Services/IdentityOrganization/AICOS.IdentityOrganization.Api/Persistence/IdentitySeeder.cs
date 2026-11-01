using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Persistence;

public static class IdentitySeeder
{
    /// <summary>Plans with real names for a fresh demo, added once each by name when <c>Seed:DemoPlans=true</c>. Prices and limits stay editable on the plans page.</summary>
    public static async Task SeedDemoPlansAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup") || !app.Configuration.GetValue<bool>("Seed:DemoPlans"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await AddDemoPlansAsync(scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>());
    }

    public static async Task<int> AddDemoPlansAsync(IdentityOrganizationDbContext db)
    {
        SubscriptionPlan[] plans =
        [
            new("Gói Đơn Lẻ - 1 Quán", "Basic", 490_000, BillingPeriod.Monthly, 1, 10, "Cho một quán: đủ gọi món QR, thu ngân, pha chế và báo cáo.", 30),
            new("Gói Chuỗi Tiêu Chuẩn - 5 Quán", "Standard", 1_990_000, BillingPeriod.Monthly, 5, 50, "Cho chuỗi đến 5 quán: thực đơn chung, giá riêng từng quán và báo cáo toàn chuỗi.", 100),
            new("Gói Chuỗi Cao Cấp", "Premium", 4_990_000, BillingPeriod.Monthly, 20, 300, "Cho chuỗi lớn đến 20 quán, không giới hạn số bàn mỗi quán.", null),
        ];
        var names = await db.SubscriptionPlans.Select(p => p.Name).ToListAsync();
        var missing = plans.Where(p => !names.Contains(p.Name)).ToList();
        db.SubscriptionPlans.AddRange(missing);
        await db.SaveChangesAsync();
        return missing.Count;
    }

    /// <summary>
    /// Creates the platform SuperAdmin from <c>Auth:SeedAdmin</c> when no SuperAdmin exists yet, so somebody can log in and
    /// create the other accounts. Runs together with migrations (<c>Database:MigrateOnStartup=true</c>).
    /// </summary>
    public static async Task SeedIdentityAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var seed = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.SeedAdmin;
        if (string.IsNullOrWhiteSpace(seed.Email) || string.IsNullOrWhiteSpace(seed.Password))
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == AicosRoles.SuperAdmin))
        {
            return;
        }

        var email = User.NormalizeLogin(seed.Email);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email || u.Username == "admin"))
        {
            app.Logger.LogWarning("SuperAdmin seed skipped: email {Email} or username admin is already used by another account.", LoggingEmailSender.Mask(email));
            return;
        }

        var admin = new User(seed.FullName, seed.Email, "admin", phone: null, AicosRoles.SuperAdmin, branchId: null);
        admin.SetPassword(scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(admin, seed.Password), mustChangePassword: false);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        app.Logger.LogWarning("Seeded SuperAdmin account {Email} - change its password outside development.", LoggingEmailSender.Mask(admin.Email));
    }
}
