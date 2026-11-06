using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.IdentityOrganization.Api.Features.Users;

namespace AICOS.IdentityOrganization.Tests;

public sealed class StaffDisplayTests
{
    [Fact]
    public async Task Staff_can_resolve_same_branch_names_without_management_access_or_contact_data()
    {
        using var factory = new IdentityApiFactory();
        using var admin = factory.CreateClientAs(AicosRoles.BrandAdmin);
        async Task<Guid> Branch()
        {
            var r = await admin.PostAsJsonAsync("/api/v1/identity/branches", new BranchRequest(Guid.NewGuid().ToString(), null, null, null, null), AuthEndpointsTests.Json);
            r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<BranchResponse>(AuthEndpointsTests.Json))!.Id;
        }
        var own = await Branch(); var other = await Branch();
        async Task<UserResponse> User(Guid branch, string name)
        {
            var r = await admin.PostAsJsonAsync("/api/v1/identity/users", new CreateUserRequest(name, Guid.NewGuid()+"@example.test", null,
                null, "Test1234", AicosRoles.Cashier, branch), AuthEndpointsTests.Json);
            r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<UserResponse>(AuthEndpointsTests.Json))!;
        }
        var visible = await User(own, "Cashier One"); var hidden = await User(other, "Cashier Two");
        using var barista = factory.CreateClientAs(AicosRoles.Barista, own);
        var url = $"/api/v1/identity/staff/display-names?ids={visible.Id},{hidden.Id}";
        var data = await barista.GetFromJsonAsync<JsonElement>(url);
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("fullName").GetString().Should().Be("Cashier One");
        data[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "fullName");
        (await barista.GetAsync($"/api/v1/identity/users/{visible.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var customer = factory.CreateClientAs(AicosRoles.Customer);
        (await customer.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}