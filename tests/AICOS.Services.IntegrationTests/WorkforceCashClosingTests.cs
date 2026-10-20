using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-46 the cashier counts the drawer, UC-33 the branch manager reviews the difference and closes the shift (BR-55).</summary>
public sealed class WorkforceCashClosingTests : WorkforceTestBase
{
    private const string Closings = $"{Api}/cash-closings";

    private Task CashAsync(Guid cashierUser, decimal amount, DateTime? at = null, Guid? branch = null, Guid? tenant = null, Guid? orderId = null) =>
        F.HandleAsync(new CashPaymentRecordedEvent(tenant ?? Tenant, branch ?? B1, cashierUser, orderId ?? Guid.NewGuid(), amount, at ?? F.Clock.Now.UtcDateTime));

    /// <summary>A cashier with a 07:00-12:00 shift on Monday, checked in at 07:00.</summary>
    private async Task<(Guid User, Guid Employee, Guid Assignment, HttpClient Client)> CashierOnShiftAsync(string name = "Thu Ngân")
    {
        var shift = await NewShiftAsync($"Ca {name}", "07:00", "12:00");
        var (user, employee) = await NewEmployeeAsync(name, AicosRoles.Cashier);
        var assignment = await AssignAsync(employee, shift, Monday);
        var client = Staff(user, AicosRoles.Cashier);
        SetLocal(Monday, 7, 0);
        await client.PostOk($"{Api}/attendance/check-in", new { assignmentId = assignment });
        return (user, employee, assignment, client);
    }

    // ------------------------------------------------------------------ the cash ledger
    [Fact]
    public async Task The_expected_amount_is_the_cash_the_cashier_took_during_the_shift()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        var otherCashier = NewAccount(AicosRoles.Cashier, B1, "Khác");
        await CashAsync(user, 50_000, F.Clock.Now.UtcDateTime.AddHours(-1));                // before check-in: another shift
        SetLocal(Monday, 8, 0);
        await CashAsync(user, 120_000);
        await CashAsync(user, 80_000);
        await CashAsync(user, -30_000);                                                      // a cash refund
        await CashAsync(otherCashier, 999_000);                                              // another cashier
        await CashAsync(user, 777_000, branch: B2);                                          // another branch
        await CashAsync(user, 555_000, tenant: OtherTenant);                                 // another brand
        SetLocal(Monday, 12, 0);

        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 170_000, note = "Đủ" });

        closing.GetProperty("expectedAmount").GetDecimal().Should().Be(170_000);
        closing.GetProperty("countedAmount").GetDecimal().Should().Be(170_000);
        closing.GetProperty("difference").GetDecimal().Should().Be(0);
        closing.GetProperty("status").GetString().Should().Be("PendingReview");
        closing.GetProperty("cashierNote").GetString().Should().Be("Đủ");
        closing.GetProperty("employeeName").GetString().Should().Be("Thu Ngân");
    }

    [Fact]
    public async Task A_redelivered_cash_event_is_counted_once()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 8, 0);
        var sale = new CashPaymentRecordedEvent(Tenant, B1, user, Guid.NewGuid(), 100_000, F.Clock.Now.UtcDateTime);
        await F.HandleAsync(sale);
        await F.HandleAsync(sale);
        SetLocal(Monday, 12, 0);

        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000 });

        closing.GetProperty("expectedAmount").GetDecimal().Should().Be(100_000);
    }

    [Fact]
    public async Task A_shortage_and_an_overage_show_as_a_negative_and_a_positive_difference()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 9, 0);
        await CashAsync(user, 200_000);
        SetLocal(Monday, 12, 0);

        var short1 = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 150_000 });
        short1.GetProperty("difference").GetDecimal().Should().Be(-50_000);

        var other = await CashierOnShiftAsync("Cashier Hai");
        SetLocal(Monday, 9, 30);
        await CashAsync(other.User, 100_000);
        SetLocal(Monday, 12, 0);
        (await other.Client.PostOk(Closings, new { assignmentId = other.Assignment, countedAmount = 130_000 })).GetProperty("difference").GetDecimal().Should().Be(30_000);
    }

    // ------------------------------------------------------------------ submitting
    [Fact]
    public async Task Only_a_cashier_who_worked_the_shift_closes_it_and_only_once()
    {
        var (_, _, assignment, cashier) = await CashierOnShiftAsync();
        var barista = NewAccount(AicosRoles.Barista, B1, "Pha chế");
        var otherShift = await NewShiftAsync("Chiều", "13:00", "18:00");
        var (neverUser, neverEmp) = await NewEmployeeAsync("Chưa Làm", AicosRoles.Cashier);
        var notWorked = await AssignAsync(neverEmp, otherShift, Monday);

        await Staff(barista, AicosRoles.Barista).Post(Closings, new { assignmentId = assignment, countedAmount = 1 }).ShouldBe(403, "cash_closing.cashier_only");
        await Manager.Post(Closings, new { assignmentId = assignment, countedAmount = 1 }).ShouldBe(403, "cash_closing.cashier_only");
        await Staff(neverUser, AicosRoles.Cashier).Post(Closings, new { assignmentId = notWorked, countedAmount = 1 }).ShouldBe(422, "cash_closing.shift_not_worked");
        await Staff(neverUser, AicosRoles.Cashier).Post(Closings, new { assignmentId = assignment, countedAmount = 1 }).ShouldBe(404);   // a colleague's shift

        await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 0 });
        await cashier.Post(Closings, new { assignmentId = assignment, countedAmount = 0 }).ShouldBe(409, "cash_closing.duplicate");
    }

    [Fact]
    public async Task The_counted_amount_and_note_are_validated()
    {
        var (_, _, assignment, cashier) = await CashierOnShiftAsync();

        await cashier.Post(Closings, new { assignmentId = assignment }).ShouldBe(400);
        await cashier.Post(Closings, new { assignmentId = assignment, countedAmount = -1 }).ShouldBe(400);
        await cashier.Post(Closings, new { assignmentId = assignment, countedAmount = 1_000_000_001 }).ShouldBe(400);
        await cashier.Post(Closings, new { assignmentId = assignment, countedAmount = 1, note = new string('n', 501) }).ShouldBe(400);
        await cashier.Post(Closings, new { countedAmount = 1 }).ShouldBe(400);
    }

    // ------------------------------------------------------------------ the manager reviews (BR-55)
    [Fact]
    public async Task A_difference_needs_a_manager_note_to_be_confirmed_and_is_audited()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 9, 0);
        await CashAsync(user, 200_000);
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 150_000, note = "Thiếu 50k" });

        await Manager.Post($"{Closings}/{closing.Id()}/confirm", new { }).ShouldBe(422, "cash_closing.note_required");
        var confirmed = await Manager.PostOk($"{Closings}/{closing.Id()}/confirm", new { note = "Đã trừ vào lương, nhân viên đồng ý" });
        confirmed.GetProperty("status").GetString().Should().Be("Confirmed");
        confirmed.GetProperty("managerNote").GetString().Should().Be("Đã trừ vào lương, nhân viên đồng ý");
        await Manager.Post($"{Closings}/{closing.Id()}/confirm", new { note = "again" }).ShouldBe(422, "cash_closing.already_confirmed");

        var audit = F.Events.Published.OfType<AuditRecordedEvent>().Single();
        audit.Action.Should().Be("cash_closing.confirmed");
        audit.EntityType.Should().Be("CashClosing");
        audit.TenantId.Should().Be(Tenant);
        audit.ActorRole.Should().Be(AicosRoles.BranchManager);
        audit.Before.Should().Contain("\"status\":\"PendingReview\"").And.Contain("\"difference\":-50000");
        audit.After.Should().Contain("\"status\":\"Confirmed\"");
    }

    [Fact]
    public async Task A_closing_without_a_difference_is_confirmed_without_a_note()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 9, 0);
        await CashAsync(user, 100_000);
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000 });

        var confirmed = await Manager.PostOk($"{Closings}/{closing.Id()}/confirm", new { });

        confirmed.GetProperty("status").GetString().Should().Be("Confirmed");
        confirmed.GetProperty("confirmedAtUtc").GetDateTime().Should().BeCloseTo(F.Clock.Now.UtcDateTime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_manager_lists_and_filters_closings_and_a_cashier_only_sees_their_own()
    {
        var first = await CashierOnShiftAsync("Thu Một");
        var second = await CashierOnShiftAsync("Thu Hai");
        SetLocal(Monday, 9, 0);
        await CashAsync(first.User, 100_000);
        await CashAsync(second.User, 100_000);
        SetLocal(Monday, 12, 0);
        var exact = await first.Client.PostOk(Closings, new { assignmentId = first.Assignment, countedAmount = 100_000 });
        var off = await second.Client.PostOk(Closings, new { assignmentId = second.Assignment, countedAmount = 90_000 });
        await Manager.PostOk($"{Closings}/{exact.Id()}/confirm", new { });

        (await Manager.GetFromJsonAsync<JsonElement>(Closings)).GetProperty("totalCount").GetInt32().Should().Be(2);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Closings}?status=PendingReview")).GetProperty("items")[0].Id().Should().Be(off.Id());
        (await Manager.GetFromJsonAsync<JsonElement>($"{Closings}?withDifference=true")).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Closings}?from={D(Monday)}&to={D(Monday)}")).GetProperty("totalCount").GetInt32().Should().Be(2);
        (await Manager.GetFromJsonAsync<JsonElement>($"{Closings}?from={D(Monday.AddDays(1))}")).GetProperty("totalCount").GetInt32().Should().Be(0);

        var mine = await first.Client.GetFromJsonAsync<JsonElement>($"{Closings}/mine");
        mine.GetArrayLength().Should().Be(1);
        mine[0].Id().Should().Be(exact.Id());
        await first.Client.GetAsync($"{Closings}/{exact.Id()}").ShouldBe(200);
        await first.Client.GetAsync($"{Closings}/{off.Id()}").ShouldBe(404);            // a colleague's closing
        await first.Client.GetAsync(Closings).ShouldBe(403);
        await first.Client.Post($"{Closings}/{exact.Id()}/confirm", new { }).ShouldBe(403);
    }

    // ------------------------------------------------------------------ isolation
    [Fact]
    public async Task Other_branches_and_other_brands_cannot_see_or_confirm_a_closing()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 9, 0);
        await CashAsync(user, 100_000);
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000 });

        await Manager2.GetAsync($"{Closings}/{closing.Id()}").ShouldBe(403, "branch.forbidden");
        await Manager2.Post($"{Closings}/{closing.Id()}/confirm", new { }).ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Closings}/{closing.Id()}").ShouldBe(404);
        await OtherAdmin.Post($"{Closings}/{closing.Id()}/confirm", new { }).ShouldBe(404);
        (await Manager2.GetFromJsonAsync<JsonElement>(Closings)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await OtherAdmin.GetFromJsonAsync<JsonElement>(Closings)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await Admin.GetFromJsonAsync<JsonElement>(Closings)).GetProperty("totalCount").GetInt32().Should().Be(1);
        (await Admin.GetFromJsonAsync<JsonElement>($"{Closings}?branchId={B2}")).GetProperty("totalCount").GetInt32().Should().Be(0);
        await F.CreateClient().GetAsync(Closings).ShouldBe(401);
    }
}
