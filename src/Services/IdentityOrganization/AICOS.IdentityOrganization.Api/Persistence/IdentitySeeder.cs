using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Persistence;

public static class IdentitySeeder
{
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
