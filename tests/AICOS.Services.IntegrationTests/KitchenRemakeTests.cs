using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>"Pha lại": the bar makes a ready order again before the hand-over.</summary>
public sealed class KitchenRemakeTests : IDisposable
{
    private const string Kitchen = "/api/v1/kitchen";

    private readonly RecordingNotifier _notifier = new();
    private readonly ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly HttpClient _barista;
    private readonly HttpClient _cashier;
    private readonly HttpClient _manager;

    public KitchenRemakeTests()
    {
        _f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>(services =>
        {
            services.RemoveAll<IKitchenNotifier>();
            services.AddSingleton<IKitchenNotifier>(_notifier);
        });
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _barista = _f.As(AicosRoles.Barista, _b1);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
    }

    public void Dispose() => _f.Dispose();

    /// <summary>A dine-in ticket the bar has finished: Ready for pickup.</summary>
    private async Task<(Guid Ticket, Guid Order)> ReadyTicketAsync(string code = "D-1")
    {
        var orderId = Guid.NewGuid();
        await _f.HandleAsync(new OrderCreatedIntegrationEvent(
            _tenant, orderId, code, _b1, null, null, null, "T1", "DineIn", 55_000, false, null, [new OrderItemDto(Guid.NewGuid(), "Latte", 55_000, 1, null)], DateTime.UtcNow));
        var ticket = (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/queue")).EnumerateArray().First(t => t.GetProperty("orderCode").GetString() == code).Id();
        await _barista.Put($"{Kitchen}/tickets/{ticket}/status", new { status = "Brewing" }).ShouldBe(200);
        await _barista.Put($"{Kitchen}/tickets/{ticket}/status", new { status = "ReadyForPickup" }).ShouldBe(200);
        return (ticket, orderId);
    }

    [Fact]
    public async Task The_bar_makes_a_ready_order_again_and_the_order_service_is_told()
    {
        var (ticket, orderId) = await ReadyTicketAsync();

        var remade = await (await _barista.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "Đổ ly" })).Body();

        (remade.GetProperty("status").GetString(), remade.GetProperty("remakeCount").GetInt32(), remade.GetProperty("lastRemakeReason").GetString()).Should().Be(("Brewing", 1, "Đổ ly"));
        remade.GetProperty("readyAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        var ev = _f.Events.Published.OfType<KitchenOrderRemakeIntegrationEvent>().Single();
        (ev.OrderId, ev.TicketId, ev.RemakeCount, ev.Reason).Should().Be((orderId, ticket, 1, "Đổ ly"));
        _notifier.Statuses.Last().Status.Should().Be("Brewing");
        (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/queue")).EnumerateArray().Select(t => t.Id()).Should().Contain(ticket);   // back in the queue
    }

    [Fact]
    public async Task A_remade_order_can_be_finished_again_and_remade_again()
    {
        var (ticket, _) = await ReadyTicketAsync();
        await _barista.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "Sai món" }).ShouldBe(200);
        await _barista.Put($"{Kitchen}/tickets/{ticket}/status", new { status = "ReadyForPickup" }).ShouldBe(200);

        var again = await (await _manager.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "Quá ngọt" })).Body();

        again.GetProperty("remakeCount").GetInt32().Should().Be(2);
        _f.Events.Published.OfType<KitchenOrderRemakeIntegrationEvent>().Select(e => e.RemakeCount).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Only_a_ready_order_can_be_remade_and_a_reason_is_needed()
    {
        var (ticket, _) = await ReadyTicketAsync();
        await _barista.Post($"{Kitchen}/tickets/{ticket}/remake", new { }).ShouldBe(400);
        await _barista.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = new string('x', 201) }).ShouldBe(400);

        await _f.HandleAsync(new OrderCreatedIntegrationEvent(
            _tenant, Guid.NewGuid(), "D-2", _b1, null, null, null, "T2", "DineIn", 1, false, null, [new OrderItemDto(Guid.NewGuid(), "Latte", 1, 1, null)], DateTime.UtcNow));
        var pending = (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/queue")).EnumerateArray().First(t => t.GetProperty("orderCode").GetString() == "D-2").Id();
        await _barista.Post($"{Kitchen}/tickets/{pending}/remake", new { reason = "x" }).ShouldBe(422, "ticket.not_ready");          // not made yet

        await _barista.Put($"{Kitchen}/tickets/{ticket}/status", new { status = "Completed" }).ShouldBe(200);
        await _barista.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "x" }).ShouldBe(422, "ticket.not_ready");          // already handed over
    }

    [Fact]
    public async Task Only_the_bar_team_of_the_branch_remakes()
    {
        var (ticket, _) = await ReadyTicketAsync();

        await _cashier.Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "x" }).ShouldBe(403);
        await _f.As(AicosRoles.Customer).Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "x" }).ShouldBe(403);
        await _f.CreateClient().Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "x" }).ShouldBe(401);
        await _f.As(AicosRoles.Barista, _b2).Post($"{Kitchen}/tickets/{ticket}/remake", new { reason = "x" }).ShouldBe(403, "branch.forbidden");
        _f.Events.Published.OfType<KitchenOrderRemakeIntegrationEvent>().Should().BeEmpty();
    }
}
