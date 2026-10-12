using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using AICOS.BuildingBlocks.Http;
using AICOS.BuildingBlocks.Security;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Branches;
using AICOS.BuildingBlocks.Persistence;

namespace AICOS.IdentityOrganization.Tests;

public sealed class BranchEndpointsTests : IDisposable
{
    private const string Url = "/api/v1/identity/branches";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new TimeOnlyJsonConverter() },
    };

    private readonly IdentityApiFactory _factory = new();
    private readonly HttpClient _admin;

    public BranchEndpointsTests() => _admin = _factory.CreateClientAs(AicosRoles.SystemAdmin);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Admin_can_create_and_get_a_branch()
    {
        var created = await CreateAsync("AICOS Quận 1", phone: "0901234567");

        created.Status.Should().Be(BranchStatus.Active);
        created.OpeningTime.Should().Be(new TimeOnly(7, 0));
        created.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var fetched = await _admin.GetFromJsonAsync<BranchResponse>($"{Url}/{created.Id}", Json);
        fetched!.Name.Should().Be("AICOS Quận 1");
    }

    [Fact]
    public async Task Create_returns_201_with_location_header()
    {
        var response = await _admin.PostAsJsonAsync(Url, NewRequest("AICOS Thủ Đức"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Contain("/api/v1/identity/branches/");
    }

    [Fact]
    public async Task Create_rejects_invalid_input_with_field_errors()
    {
        var response = await _admin.PostAsJsonAsync(
            Url, new BranchRequest(" ", null, "abc", new TimeOnly(7, 0), null), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("Name", out _).Should().BeTrue();
        errors.TryGetProperty("Phone", out _).Should().BeTrue();
        errors.TryGetProperty("OpeningTime", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_name_is_rejected_case_insensitively()
    {
        await CreateAsync("AICOS Quận 3");

        var response = await _admin.PostAsJsonAsync(Url, NewRequest("aicos quận 3"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("branch.duplicate_name");
    }

    [Fact]
    public async Task Admin_can_update_a_branch()
    {
        var created = await CreateAsync("AICOS Bình Thạnh");

        var response = await _admin.PutAsJsonAsync(
            $"{Url}/{created.Id}",
            new BranchRequest("AICOS Bình Thạnh 2", "12 Điện Biên Phủ", "+84901234567", new TimeOnly(6, 30), new TimeOnly(23, 0)),
            Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<BranchResponse>(Json);
        updated!.Name.Should().Be("AICOS Bình Thạnh 2");
        updated.Address.Should().Be("12 Điện Biên Phủ");
        updated.UpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Updating_with_own_name_is_not_a_duplicate()
    {
        var created = await CreateAsync("AICOS Gò Vấp");

        var response = await _admin.PutAsJsonAsync($"{Url}/{created.Id}", NewRequest("AICOS Gò Vấp"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_can_suspend_and_reactivate_a_branch()
    {
        var created = await CreateAsync("AICOS Tân Bình");

        var suspended = await PatchStatusAsync(created.Id, BranchStatus.Suspended);
        suspended.Status.Should().Be(BranchStatus.Suspended);

        var active = await PatchStatusAsync(created.Id, BranchStatus.Active);
        active.Status.Should().Be(BranchStatus.Active);
    }

    [Fact]
    public async Task List_supports_search_status_filter_and_paging()
    {
        await CreateAsync("AICOS A");
        await CreateAsync("AICOS B");
        var c = await CreateAsync("Other C");
        await PatchStatusAsync(c.Id, BranchStatus.Suspended);

        var search = await ListAsync("?search=aicos");
        search.TotalCount.Should().Be(2);

        var suspended = await ListAsync("?status=Suspended");
        suspended.Items.Should().ContainSingle().Which.Name.Should().Be("Other C");

        var page2 = await ListAsync("?page=2&pageSize=2");
        page2.TotalCount.Should().Be(3);
        page2.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_branch_returns_404()
    {
        var response = await _admin.GetAsync($"{Url}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_caller_gets_401()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(AicosRoles.Manager)]
    [InlineData(AicosRoles.Cashier)]
    [InlineData(AicosRoles.Customer)]
    public async Task Non_admin_cannot_create_or_list(string role)
    {
        using var client = _factory.CreateClientAs(role);

        (await client.PostAsJsonAsync(Url, NewRequest("Hack"), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_can_view_only_their_own_branch()
    {
        var own = await CreateAsync("AICOS Own");
        var other = await CreateAsync("AICOS Other");
        using var manager = _factory.CreateClientAs(AicosRoles.Manager, own.Id);

        (await manager.GetAsync($"{Url}/{own.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync($"{Url}/{other.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accepts_HH_mm_times_as_typed_in_swagger_and_returns_HH_mm()
    {
        var response = await PostRawAsync(
            """{ "name": "AICOS Raw", "openingTime": "07:00", "closingTime": "22:30:00" }""");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("openingTime").GetString().Should().Be("07:00");
        body.GetProperty("closingTime").GetString().Should().Be("22:30");
    }

    [Theory]
    [InlineData("""{ "name": "AICOS Bad Time", "openingTime": "25:00", "closingTime": "22:00" }""")]
    [InlineData("""{ "name": "AICOS Bad Json", """)]
    public async Task Malformed_body_returns_400_not_500(string json)
    {
        var response = await PostRawAsync(json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errorCode").GetString().Should().Be("malformed_request");
    }

    [Theory]
    [InlineData("Bearer Bearer {0}")]
    [InlineData("Bearer \"{0}\"")]
    public async Task Token_pasted_with_extra_Bearer_or_quotes_is_still_accepted(string headerFormat)
    {
        var token = _admin.DefaultRequestHeaders.Authorization!.Parameter!;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", string.Format(headerFormat, token));

        var response = await client.PostAsJsonAsync(Url, NewRequest("AICOS Paste"), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private Task<HttpResponseMessage> PostRawAsync(string json) =>
        _admin.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static BranchRequest NewRequest(string name, string? phone = null) =>
        new(name, "1 Lê Lợi, TP.HCM", phone, new TimeOnly(7, 0), new TimeOnly(22, 0));

    private async Task<BranchResponse> CreateAsync(string name, string? phone = null)
    {
        var response = await _admin.PostAsJsonAsync(Url, NewRequest(name, phone), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task<BranchResponse> PatchStatusAsync(Guid id, BranchStatus status)
    {
        var response = await _admin.PatchAsJsonAsync($"{Url}/{id}/status", new ChangeBranchStatusRequest(status), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchResponse>(Json))!;
    }

    private async Task<PagedResponse<BranchResponse>> ListAsync(string query) =>
        (await _admin.GetFromJsonAsync<PagedResponse<BranchResponse>>(Url + query, Json))!;
}
