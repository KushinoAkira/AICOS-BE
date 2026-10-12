using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.TableReservation.Api;
using AICOS.TableReservation.Api.Persistence;

namespace AICOS.Services.IntegrationTests;

public sealed class TableTests : IDisposable
{
    private readonly ServiceFactory<TableReservationService, TableReservationDbContext> _f = new();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly HttpClient _manager;
    private readonly HttpClient _cashier;
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly HttpClient _customer;

    public TableTests()
    {
        _manager = _f.As(AicosRoles.Manager, _branch);
        _cashier = _f.As(AicosRoles.Cashier, _branch);
        _customer = _f.As(AicosRoles.Customer, userId: _customerId);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Table_code_is_unique_per_branch_and_validated()
    {
        var table = await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "t01", capacity = 4, area = "Tầng 1" });
        table.GetProperty("tableCode").GetString().Should().Be("T01");
        table.GetProperty("qrCodeUrl").GetString().Should().EndWith(table.GetProperty("qrToken").GetString());

        await _manager.Post("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 2 }).ShouldBe(409, "table.duplicate_code");
        await _manager.Post("/api/v1/tables/dining-tables", new { tableCode = "T 02", capacity = 0 }).ShouldBe(400);
        await _cashier.Post("/api/v1/tables/dining-tables", new { tableCode = "T03", capacity = 2 }).ShouldBe(403);
    }

    [Fact]
    public async Task Qr_lookup_is_public_and_regenerate_invalidates_old_code()
    {
        var table = await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 4 });
        var oldToken = table.GetProperty("qrToken").GetString();
        var anonymous = _f.CreateClient();

        (await anonymous.GetFromJsonAsync<JsonElement>($"/api/v1/tables/public/qr/{oldToken}")).GetProperty("tableCode").GetString().Should().Be("T01");

        var regenerated = await _manager.PostOk($"/api/v1/tables/dining-tables/{table.Id()}/qr/regenerate", new { });
        regenerated.GetProperty("qrToken").GetString().Should().NotBe(oldToken);
        await anonymous.GetAsync($"/api/v1/tables/public/qr/{oldToken}").ShouldBe(404);
    }

    [Fact]
    public async Task Overlapping_reservation_on_the_same_table_is_409_BR10()
    {
        var table = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 4 })).Id();
        var at = _f.Clock.Now.AddDays(1);

        await _cashier.PostOk("/api/v1/tables/reservations", Booking(table, at, 120));
        await _cashier.Post("/api/v1/tables/reservations", Booking(table, at.AddMinutes(90), 60)).ShouldBe(409, "reservation.table_unavailable");
        await _cashier.PostOk("/api/v1/tables/reservations", Booking(table, at.AddMinutes(120), 60)); // back-to-back is fine
    }

    [Fact]
    public async Task Party_larger_than_table_and_past_time_are_400()
    {
        var table = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 2 })).Id();

        await _cashier.Post("/api/v1/tables/reservations", Booking(table, _f.Clock.Now.AddDays(1), 120, party: 5)).ShouldBe(400);
        await _cashier.Post("/api/v1/tables/reservations", Booking(table, _f.Clock.Now.AddHours(-2), 120)).ShouldBe(400);
    }

    [Fact]
    public async Task Customer_books_online_then_cashier_confirms_seats_and_completes()
    {
        var table = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 4 })).Id();

        await _customer.Post("/api/v1/tables/reservations", new { branchId = _branch, customerName = "An", customerPhone = "0901234567", partySize = 2, reservedAt = _f.Clock.Now.AddMinutes(5) })
            .ShouldBe(400); // less than 15 minutes ahead
        var booking = await _customer.PostOk("/api/v1/tables/reservations", new { branchId = _branch, customerName = "An", customerPhone = "0901234567", partySize = 2, reservedAt = _f.Clock.Now.AddHours(3) });
        booking.GetProperty("status").GetString().Should().Be("Pending");
        var id = booking.Id();

        (await _customer.GetFromJsonAsync<JsonElement>("/api/v1/tables/reservations/mine")).GetArrayLength().Should().Be(1);
        await _f.As(AicosRoles.Customer).GetAsync($"/api/v1/tables/reservations/{id}").ShouldBe(404); // other customer

        await _cashier.Post($"/api/v1/tables/reservations/{id}/confirm", new { tableId = table }).ShouldBe(200);
        await _cashier.Post($"/api/v1/tables/reservations/{id}/seat", new { }).ShouldBe(200);
        (await _cashier.GetFromJsonAsync<JsonElement>($"/api/v1/tables/dining-tables/{table}")).GetProperty("status").GetString().Should().Be("Occupied");
        await _cashier.Post($"/api/v1/tables/reservations/{id}/complete", new { }).ShouldBe(200);
        await _customer.Post($"/api/v1/tables/reservations/{id}/cancel", new { }).ShouldBe(422, "reservation.invalid_status");
    }

    [Fact]
    public async Task Availability_excludes_reserved_and_too_small_tables_and_delete_is_blocked()
    {
        var small = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T01", capacity = 2 })).Id();
        var big = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T02", capacity = 6 })).Id();
        var big2 = (await _manager.PostOk("/api/v1/tables/dining-tables", new { tableCode = "T03", capacity = 6 })).Id();
        var at = _f.Clock.Now.AddDays(1);
        await _cashier.PostOk("/api/v1/tables/reservations", Booking(big, at, 120, party: 4));

        var free = await _f.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/api/v1/tables/availability?branchId={_branch}&at={Uri.EscapeDataString(at.AddMinutes(30).ToString("O"))}&partySize=4");
        free.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).Should().BeEquivalentTo([big2]);

        await _manager.DeleteAsync($"/api/v1/tables/dining-tables/{big}").ShouldBe(409, "table.in_use");
        await _manager.DeleteAsync($"/api/v1/tables/dining-tables/{small}").ShouldBe(204);
    }

    private static object Booking(Guid table, DateTimeOffset at, int minutes, int party = 2) =>
        new { tableId = table, customerName = "Khách", customerPhone = "0901234567", partySize = party, reservedAt = at, durationMinutes = minutes };
}
