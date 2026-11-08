using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.KitchenOperations.Api;
using AICOS.KitchenOperations.Api.Features;
using AICOS.KitchenOperations.Api.Persistence;
using AICOS.KitchenOperations.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AICOS.Services.IntegrationTests;

/// <summary>Records what would have been pushed to the screens.</summary>
public sealed class RecordingNotifier : IKitchenNotifier
{
    public List<(Guid Tenant, Guid Branch, string OrderCode)> NewTickets { get; } = [];

    public List<(Guid Tenant, Guid Branch, string Status)> Statuses { get; } = [];

    public List<(Guid Tenant, Guid Branch, string OrderCode)> Ready { get; } = [];

    public List<(Guid Tenant, Guid Branch, int Count)> Stickers { get; } = [];

    public List<string> CounterEvents { get; } = [];

    public Task NotifyCounterAsync(Guid tenantId, Guid branchId, string eventName, object payload, CancellationToken ct = default)
    {
        CounterEvents.Add(eventName);
        return Task.CompletedTask;
    }

    public Task NotifyNewTicketAsync(Guid tenantId, Guid branchId, KitchenTicketDto ticket, CancellationToken ct = default)
    {
        NewTickets.Add((tenantId, branchId, ticket.OrderCode));
        return Task.CompletedTask;
    }

    public Task NotifyStatusChangedAsync(Guid tenantId, Guid branchId, Guid ticketId, string newStatus, CancellationToken ct = default)
    {
        Statuses.Add((tenantId, branchId, newStatus));
        return Task.CompletedTask;
    }

    public Task NotifyOrderReadyForPickupAsync(Guid tenantId, Guid branchId, OrderReadyNotificationDto notification, CancellationToken ct = default)
    {
        Ready.Add((tenantId, branchId, notification.OrderCode));
        return Task.CompletedTask;
    }

    public Task NotifyStickersPrintedAsync(Guid tenantId, Guid branchId, IReadOnlyList<CupStickerDto> stickers, CancellationToken ct = default)
    {
        Stickers.Add((tenantId, branchId, stickers.Count));
        return Task.CompletedTask;
    }
}

/// <summary>The bar queue (SCRUM-55, 56, 58): tickets from order events, status changes, notifications to the cashier, brand and branch isolation.</summary>
public sealed class KitchenTests : IDisposable
{
    private const string Kitchen = "/api/v1/kitchen";

    private readonly RecordingNotifier _notifier = new();
    private readonly ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext> _f;
    private readonly Guid _tenant = ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>.DefaultTenantId;
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _b1;
    private readonly Guid _b2;
    private readonly HttpClient _barista;
    private readonly HttpClient _cashier;
    private readonly HttpClient _manager;
    private readonly HttpClient _admin;
    private readonly HttpClient _otherAdmin;

    public KitchenTests()
    {
        _f = new ServiceFactory<KitchenOperationsService, KitchenOperationsDbContext>(services =>
        {
            services.RemoveAll<IKitchenNotifier>();
            services.AddSingleton<IKitchenNotifier>(_notifier);
        });
        _b1 = _f.Branches.Add(_tenant, name: "Quận 1");
        _b2 = _f.Branches.Add(_tenant, name: "Quận 3");
        _f.Branches.Add(_otherTenant, name: "Foreign");
        _barista = _f.As(AicosRoles.Barista, _b1);
        _cashier = _f.As(AicosRoles.Cashier, _b1);
        _manager = _f.As(AicosRoles.BranchManager, _b1);
        _admin = _f.As(AicosRoles.BrandAdmin);
        _otherAdmin = _f.As(AicosRoles.BrandAdmin, tenantId: _otherTenant);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Brewing")]
    [InlineData("ReadyForPickup")]
    public async Task Delivery_completes_unpaid_ticket_even_if_ready_projection_is_delayed(string status)
    {
        var order = Order("DELIVERY-" + status);
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        if (status != "Pending")
            await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status }).ShouldBe(200);
        var at = DateTime.UtcNow;
        var message = new OrderDeliveredIntegrationEvent(_tenant, _b1, order.OrderId, null, Guid.NewGuid(), at);
        await _f.HandleAsync(message);
        await _f.HandleAsync(message);
        var read = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}");
        read.GetProperty("status").GetString().Should().Be("Completed");
        read.GetProperty("isPaid").GetBoolean().Should().BeFalse();
        (await QueueAsync()).GetArrayLength().Should().Be(0);
        _notifier.Statuses.Count(s => s.Status == "Completed").Should().Be(1);
        await _barista.Post($"{Kitchen}/tickets/{id}/remake", new { reason = "Too late" }).ShouldBe(422, "ticket.not_ready");
    }

    [Fact]
    public async Task Delivery_event_respects_tenant_branch_and_cancelled_history()
    {
        var order = Order("DELIVERY-SCOPE");
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        var message = new OrderDeliveredIntegrationEvent(_tenant, _b1, order.OrderId, null, null, DateTime.UtcNow);
        await _f.HandleAsync(message with { TenantId = _otherTenant });
        await _f.HandleAsync(message with { BranchId = _b2 });
        (await QueueAsync()).GetArrayLength().Should().Be(1);
        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, order.OrderId, _b1, null, "Cancelled", false, [], DateTime.UtcNow));
        await _f.HandleAsync(message);
        var read = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}");
        read.GetProperty("status").GetString().Should().Be("Cancelled");
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Sticker_preview_is_read_only_scoped_and_preserves_ids_options_and_cup_numbers()
    {
        var order = Order("PREVIEW");
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        var path = $"{Kitchen}/tickets/{id}/sticker-preview";
        var before = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}");
        var events = _notifier.Stickers.Count;
        var preview = await _barista.GetFromJsonAsync<JsonElement>(path);
        preview.GetProperty("printStatus").GetString().Should().Be("UNKNOWN");
        preview.GetProperty("canPrint").GetBoolean().Should().BeFalse();
        var stickers = preview.GetProperty("stickers").EnumerateArray().ToArray();
        stickers.Select(s => s.GetProperty("cupIndex").GetInt32()).Should().Equal(1, 2, 3);
        stickers[0].GetProperty("customizations").GetString().Should().Be("ít đá");
        stickers.Select(s => s.GetProperty("stickerId").GetGuid()).Should().OnlyHaveUniqueItems();
        (await _barista.GetFromJsonAsync<JsonElement>(path)).ToString().Should().Be(preview.ToString());
        (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}")).ToString().Should().Be(before.ToString());
        _notifier.Stickers.Count.Should().Be(events);
        await _manager.GetAsync(path).ShouldBe(200);
        await _cashier.GetAsync(path).ShouldBe(403);
        await _f.CreateClient().GetAsync(path).ShouldBe(401);
        await _f.As(AicosRoles.Barista, _b2).GetAsync(path).ShouldBe(403);
        await _otherAdmin.GetAsync(path).ShouldBe(404);
    }

    [Fact]
    public async Task Sticker_preview_uses_current_table_without_rewriting_generation_snapshot()
    {
        var order = Order("TRANSFER-PREVIEW");
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        var transfer = new TableTransferredIntegrationEvent(_tenant, _b1, Guid.NewGuid(), Guid.NewGuid(), "S09", [order.OrderId, order.OrderId]);
        await _f.HandleAsync(transfer);
        await _f.HandleAsync(transfer);
        var preview = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}/sticker-preview");
        preview.GetProperty("tableNumber").GetString().Should().Be("S09");
        preview.GetProperty("stickers")[0].GetProperty("tableNumber").GetString().Should().Be("S09");
        var legacy = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}/stickers");
        legacy.GetProperty("stickers")[0].GetProperty("tableNumber").GetString().Should().Be("T1");
        legacy.GetProperty("totalStickers").GetInt32().Should().Be(3);
        await _f.HandleAsync(new OrderDeliveredIntegrationEvent(_tenant, _b1, order.OrderId, null, null, DateTime.UtcNow));
        await _f.HandleAsync(transfer with { TableCode = "S10" });
        var completed = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}");
        completed.GetProperty("status").GetString().Should().Be("Completed");
        completed.GetProperty("tableNumber").GetString().Should().Be("S09");
    }

    [Fact]
    public async Task Hold_blocks_both_sticker_routes_and_missing_stickers_are_not_generated_by_preview()
    {
        Guid id;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
            var ticket = new AICOS.KitchenOperations.Api.Domain.Entities.KitchenTicket(_tenant, Guid.NewGuid(), "MISSING-STICKERS", _b1, "S01", "DineIn", false);
            ticket.AddItem(Guid.NewGuid(), "Coffee", 1);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.Id;
        }
        await _barista.GetAsync($"{Kitchen}/tickets/{id}/sticker-preview").ShouldBe(409, "ticket.stickers_incomplete");
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
            var ticket = await db.Tickets.FindAsync(id);
            ticket!.Stickers.Should().BeEmpty();
            ticket.HoldRecovery("review");
            await db.SaveChangesAsync();
        }
        await _barista.GetAsync($"{Kitchen}/tickets/{id}/sticker-preview").ShouldBe(409, "ticket.recovery_review_required");
        await _barista.GetAsync($"{Kitchen}/tickets/{id}/stickers").ShouldBe(409, "ticket.recovery_review_required");
    }

    private OrderCreatedIntegrationEvent Order(string code, string type = "DineIn", bool paid = false, Guid? branch = null, Guid? tenant = null, DateTime? pickup = null, Guid? orderId = null) => new(
        tenant ?? _tenant, orderId ?? Guid.NewGuid(), code, branch ?? _b1, null, null, null, type == "DineIn" ? "T1" : null, type, 100_000, paid, pickup,
        [new OrderItemDto(Guid.NewGuid(), "Latte", 55_000, 2, "ít đá"), new OrderItemDto(Guid.NewGuid(), "Bạc xỉu", 29_000, 1, null)], DateTime.UtcNow);

    private async Task<JsonElement> QueueAsync(HttpClient? client = null, Guid? branch = null) =>
        await (client ?? _barista).GetFromJsonAsync<JsonElement>(branch is null ? $"{Kitchen}/queue" : $"{Kitchen}/queue?branchId={branch}");

    private async Task<Guid> TicketOfAsync(string code) =>
        (await QueueAsync()).EnumerateArray().First(t => t.GetProperty("orderCode").GetString() == code).Id();

    [Fact]
    public async Task Conditional_updates_replay_safely_and_reject_stale_staff_views()
    {
        var order = Order("CONDITIONAL-1");
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        var path = $"{Kitchen}/tickets/{id}/status";
        var capability = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/staff/capabilities");
        capability.GetProperty("conditionalTicketUpdatesVersion").GetInt32().Should().Be(1);
        await _f.CreateClient().GetAsync($"{Kitchen}/staff/capabilities").ShouldBe(401);

        await _barista.Put(path, new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(200);
        var secondBarista = _f.As(AicosRoles.Barista, _b1);
        await secondBarista.Put(path, new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(200);
        _notifier.Statuses.Count(s => s.Status == "Brewing").Should().Be(1);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(409, "ticket.stale");
        await _cashier.Put(path, new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(403);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 0 }).ShouldBe(200);
    }

    [Fact]
    public async Task Remake_cycle_invalidates_old_preparation_view_and_delivery_is_terminal()
    {
        var order = Order("CONDITIONAL-REMAKE");
        await _f.HandleAsync(order);
        var id = await TicketOfAsync(order.OrderCode);
        var path = $"{Kitchen}/tickets/{id}/status";
        await _barista.Put(path, new { status = "Brewing", expectedStatus = "Pending", expectedRemakeCount = 0 }).ShouldBe(200);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 0 }).ShouldBe(200);
        await _barista.Post($"{Kitchen}/tickets/{id}/remake", new { reason = "Wrong drink" }).ShouldBe(200);
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 0 }).ShouldBe(409, "ticket.stale");
        await _barista.Put(path, new { status = "ReadyForPickup", expectedStatus = "Brewing", expectedRemakeCount = 1 }).ShouldBe(200);
        await _f.HandleAsync(new OrderDeliveredIntegrationEvent(_tenant, _b1, order.OrderId, null, Guid.NewGuid(), DateTime.UtcNow));
        await _barista.Put(path, new { status = "Brewing", expectedStatus = "ReadyForPickup", expectedRemakeCount = 1 }).ShouldBe(409, "ticket.stale");
        (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}")).GetProperty("status").GetString().Should().Be("Completed");
    }

    // ------------------------------------------------------------------ tickets come from order events
    [Fact]
    public async Task A_dine_in_order_reaches_the_bar_at_once_with_one_sticker_per_cup()
    {
        await _f.HandleAsync(Order("D-1"));

        var queue = await QueueAsync();

        queue.GetArrayLength().Should().Be(1);
        var ticket = queue[0];
        (ticket.GetProperty("status").GetString(), ticket.GetProperty("totalCups").GetInt32(), ticket.GetProperty("isPaid").GetBoolean()).Should().Be(("Pending", 3, false));
        ticket.GetProperty("stickers").GetArrayLength().Should().Be(3);
        ticket.GetProperty("stickers")[0].GetProperty("totalCupsInOrder").GetInt32().Should().Be(3);
        _notifier.NewTickets.Should().ContainSingle().Which.Should().Be((_tenant, _b1, "D-1"));
        _notifier.Stickers.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    [Fact]
    public async Task A_takeaway_order_stays_out_of_the_queue_until_it_is_paid_BR43()
    {
        var order = Order("T-1", "Takeaway");
        await _f.HandleAsync(order);

        (await QueueAsync()).GetArrayLength().Should().Be(0);
        _notifier.NewTickets.Should().BeEmpty();

        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), order.OrderId, 100_000, "Momo", DateTime.UtcNow));

        var queue = await QueueAsync();
        queue.GetArrayLength().Should().Be(1);
        queue[0].GetProperty("isPaid").GetBoolean().Should().BeTrue();
        queue[0].GetProperty("stickers").GetArrayLength().Should().Be(3);
        _notifier.NewTickets.Should().ContainSingle();
    }

    [Fact]
    public async Task A_payment_that_arrives_before_its_order_is_kept_and_the_ticket_reaches_the_bar_paid()
    {
        var order = Order("EARLY-1", "Takeaway");
        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), order.OrderId, 100_000, "Cash", DateTime.UtcNow));   // events travel on separate channels
        (await QueueAsync()).GetArrayLength().Should().Be(0);

        await _f.HandleAsync(order);

        var queue = await QueueAsync();
        queue.GetArrayLength().Should().Be(1);
        queue[0].GetProperty("isPaid").GetBoolean().Should().BeTrue();
        queue[0].GetProperty("stickers").GetArrayLength().Should().Be(3);
        _notifier.NewTickets.Should().ContainSingle();

        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), order.OrderId, 100_000, "Cash", DateTime.UtcNow));   // a repeat changes nothing
        (await QueueAsync()).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_guest_order_from_the_table_QR_waits_for_payment_and_a_dine_in_order_keyed_by_staff_does_not()
    {
        var qr = Order("QR-1") with { PayFirst = true };
        await _f.HandleAsync(qr);
        await _f.HandleAsync(Order("POS-1"));

        (await QueueAsync()).EnumerateArray().Select(t => t.GetProperty("orderCode").GetString()).Should().Equal("POS-1");

        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), qr.OrderId, 100_000, "Mock", DateTime.UtcNow));

        (await QueueAsync()).EnumerateArray().Select(t => t.GetProperty("orderCode").GetString()).Should().BeEquivalentTo(["POS-1", "QR-1"]);
    }

    [Fact]
    public async Task A_paid_takeaway_order_is_shown_when_it_is_created_and_events_can_arrive_twice()
    {
        var order = Order("T-2", "Takeaway", paid: true);

        await _f.HandleAsync(order);
        await _f.HandleAsync(order);
        await _f.HandleAsync(new PaymentSucceededEvent(_tenant, _b1, Guid.NewGuid(), order.OrderId, 100_000, "Cash", DateTime.UtcNow));

        (await QueueAsync()).GetArrayLength().Should().Be(1);
        _notifier.NewTickets.Should().ContainSingle();
    }

    [Fact]
    public async Task An_order_without_a_brand_or_for_another_brand_never_reaches_this_bars_queue()
    {
        await _f.HandleAsync(Order("X-1", tenant: Guid.Empty));
        var foreign = _f.Branches.Add(_otherTenant);
        await _f.HandleAsync(Order("X-2", tenant: _otherTenant, branch: foreign));

        (await QueueAsync()).GetArrayLength().Should().Be(0);
        (await QueueAsync(_otherAdmin, foreign)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task The_queue_puts_scheduled_pickups_first_then_first_in_first_out()
    {
        await _f.HandleAsync(Order("A", pickup: null));
        _f.Clock.Now = _f.Clock.Now.AddMinutes(1);
        await _f.HandleAsync(Order("B", pickup: DateTime.UtcNow.AddMinutes(30)));
        _f.Clock.Now = _f.Clock.Now.AddMinutes(1);
        await _f.HandleAsync(Order("C", pickup: DateTime.UtcNow.AddMinutes(10)));
        _f.Clock.Now = _f.Clock.Now.AddMinutes(1);
        await _f.HandleAsync(Order("D"));

        var codes = (await QueueAsync()).EnumerateArray().Select(t => t.GetProperty("orderCode").GetString()).ToList();

        codes.Should().Equal("C", "B", "A", "D");
    }

    [Fact]
    public async Task Identical_drinks_are_grouped_into_batches_of_at_most_four()
    {
        await _f.HandleAsync(Order("A"));
        await _f.HandleAsync(Order("B"));

        var batches = await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/batches");

        var latte = batches.EnumerateArray().Where(b => b.GetProperty("productName").GetString() == "Latte").Select(b => b.GetProperty("totalQuantity").GetInt32()).ToList();
        latte.Should().Equal(4);                                                                              // 2 + 2 cups
        batches.EnumerateArray().Single(b => b.GetProperty("productName").GetString() == "Bạc xỉu").GetProperty("totalQuantity").GetInt32().Should().Be(2);
    }

    // ------------------------------------------------------------------ preparing and ready
    [Fact]
    public async Task The_bar_starts_and_finishes_a_ticket_and_the_order_service_and_the_cashier_screens_are_told()
    {
        await _f.HandleAsync(Order("D-1"));
        var id = await TicketOfAsync("D-1");

        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(200);
        var started = _f.Events.Published.OfType<KitchenOrderStartedIntegrationEvent>().Single();
        (started.TenantId, started.BranchId, started.TicketId).Should().Be((_tenant, _b1, id));

        var ready = await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "ReadyForPickup" });

        ready.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await ready.Body()).GetProperty("status").GetString().Should().Be("ReadyForPickup");
        var evt = _f.Events.Published.OfType<KitchenOrderReadyIntegrationEvent>().Single();
        (evt.TenantId, evt.BranchId, evt.OrderCode).Should().Be((_tenant, _b1, "D-1"));
        _notifier.Ready.Should().ContainSingle().Which.Should().Be((_tenant, _b1, "D-1"));
        _notifier.Statuses.Select(s => s.Status).Should().Equal("Brewing", "ReadyForPickup");
    }

    [Fact]
    public async Task Status_changes_follow_the_order_of_work_and_the_roles_of_the_bar()
    {
        await _f.HandleAsync(Order("D-1"));
        var id = await TicketOfAsync("D-1");

        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Completed" }).ShouldBe(422, "ticket.not_ready");
        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Pending" }).ShouldBe(400);
        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { }).ShouldBe(400);
        await _cashier.Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(403);
        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Cancelled" }).ShouldBe(403);
        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(200);
        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(422, "ticket.not_pending");
        await _manager.Put($"{Kitchen}/tickets/{id}/status", new { status = "Cancelled" }).ShouldBe(200);
        await _f.As(AicosRoles.Customer).Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(403);
        await _f.CreateClient().Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(401);
    }

    [Fact]
    public async Task An_unpaid_takeaway_ticket_cannot_be_made_by_the_bar()
    {
        var order = Order("T-1", "Takeaway");
        await _f.HandleAsync(order);
        var db = _f.Services.CreateScope().ServiceProvider.GetRequiredService<KitchenOperationsDbContext>();
        var id = db.Tickets.Single(t => t.OrderId == order.OrderId).Id;

        await _barista.Put($"{Kitchen}/tickets/{id}/status", new { status = "Brewing" }).ShouldBe(422, "ticket.not_paid");
        await _barista.GetAsync($"{Kitchen}/tickets/{id}/stickers").ShouldBe(422, "ticket.not_paid");
    }

    [Fact]
    public async Task Cancelling_or_completing_the_order_closes_the_ticket()
    {
        var cancelled = Order("C-1");
        var done = Order("C-2");
        await _f.HandleAsync(cancelled);
        await _f.HandleAsync(done);
        var doneId = await TicketOfAsync("C-2");
        await _barista.Put($"{Kitchen}/tickets/{doneId}/status", new { status = "ReadyForPickup" }).ShouldBe(200);

        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, cancelled.OrderId, _b1, null, "x", false, [], DateTime.UtcNow));
        await _f.HandleAsync(new OrderCompletedIntegrationEvent(_tenant, done.OrderId, _b1, null, 100_000, DateTime.UtcNow));

        (await QueueAsync()).GetArrayLength().Should().Be(0);
        _notifier.Statuses.Select(s => s.Status).Should().Contain(["Cancelled", "Completed"]);
        await _f.HandleAsync(new OrderCancelledIntegrationEvent(_tenant, done.OrderId, _b1, null, "late", false, [], DateTime.UtcNow));   // a completed ticket is never cancelled
        (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{doneId}")).GetProperty("status").GetString().Should().Be("Completed");
    }

    // ------------------------------------------------------------------ access
    [Fact]
    public async Task Tickets_follow_brand_and_branch_boundaries_and_roles()
    {
        await _f.HandleAsync(Order("D-1"));
        var id = await TicketOfAsync("D-1");

        await _admin.GetAsync($"{Kitchen}/queue").ShouldBe(400);                                              // a brand admin names the branch
        (await QueueAsync(_admin, _b1)).GetArrayLength().Should().Be(1);
        await _admin.GetAsync($"{Kitchen}/queue?branchId={Guid.NewGuid()}").ShouldBe(400);
        (await QueueAsync(_cashier)).GetArrayLength().Should().Be(1);                                         // the cashier reads the queue
        (await QueueAsync(_f.As(AicosRoles.Barista, _b2))).GetArrayLength().Should().Be(0);
        await _barista.GetAsync($"{Kitchen}/queue?branchId={_b2}").ShouldBe(403, "branch.forbidden");
        await _f.As(AicosRoles.Barista, _b2).GetAsync($"{Kitchen}/tickets/{id}").ShouldBe(403, "branch.forbidden");
        await _otherAdmin.GetAsync($"{Kitchen}/tickets/{id}").ShouldBe(404);
        await _f.As(AicosRoles.Customer).GetAsync($"{Kitchen}/queue").ShouldBe(403);
        await _f.As(AicosRoles.SuperAdmin).GetAsync($"{Kitchen}/queue").ShouldBe(403);
        await _f.CreateClient().GetAsync($"{Kitchen}/queue").ShouldBe(401);
        await _cashier.GetAsync($"{Kitchen}/tickets/{id}/stickers").ShouldBe(403);                           // stickers are for the bar
        (await _barista.GetFromJsonAsync<JsonElement>($"{Kitchen}/tickets/{id}/stickers")).GetProperty("totalStickers").GetInt32().Should().Be(3);
    }
}
