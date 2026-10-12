using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AICOS.IdentityOrganization.Api.Persistence;

public static class IdentitySeeder
{
    /// <summary>
    /// Creates the first SystemAdmin from <c>Auth:SeedAdmin</c> when no admin exists yet, so somebody can log in and
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
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == AicosRoles.SystemAdmin))
        {
            return;
        }

        var admin = new User(seed.FullName, seed.Email, "admin", phone: null, AicosRoles.SystemAdmin, branchId: null);
        admin.SetPassword(scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(admin, seed.Password), mustChangePassword: false);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        app.Logger.LogWarning("Seeded SystemAdmin account {Email} - change its password outside development.", admin.Email);
    }
}
