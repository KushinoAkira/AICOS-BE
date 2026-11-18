using System.Net.Http.Json;
using System.Text.Json;
using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;

namespace AICOS.Services.IntegrationTests;

/// <summary>UC-46 the incoming cashier counts the drawer the previous shift left, UC-33 the branch manager reviews the difference (BR-55).</summary>
public sealed class WorkforceCashClosingTests : WorkforceTestBase
{
    private const string Closings = $"{Api}/cash-closings";

    private Task CashAsync(Guid cashierUser, decimal amount, DateTime? at = null, Guid? branch = null, Guid? tenant = null, Guid? orderId = null) =>
        F.HandleAsync(new CashPaymentRecordedEvent(tenant ?? Tenant, branch ?? B1, cashierUser, orderId ?? Guid.NewGuid(), amount, at ?? F.Clock.Now.UtcDateTime));

    /// <summary>A cashier with a shift on <paramref name="day"/> (Monday by default, 07:00-12:00), checked in when it starts.</summary>
    private async Task<(Guid User, Guid Employee, Guid Assignment, HttpClient Client)> CashierOnShiftAsync(
        string name = "Thu Ngân", string start = "07:00", string end = "12:00", DateOnly? day = null)
    {
        var shift = await NewShiftAsync($"Ca {name}", start, end);
        var (user, employee) = await NewEmployeeAsync(name, AicosRoles.Cashier);
        var date = day ?? Monday;
        var assignment = await AssignAsync(employee, shift, date);
        var client = Staff(user, AicosRoles.Cashier);
        var at = TimeOnly.Parse(start);
        SetLocal(date, at.Hour, at.Minute);
        await client.PostOk($"{Api}/attendance/check-in", new { assignmentId = assignment });
        return (user, employee, assignment, client);
    }

    // ------------------------------------------------------------------ the drawer handed over
    [Fact]
    public async Task The_first_handover_of_the_day_expects_the_cash_taken_at_the_branch_since_midnight()
    {
        var earlyCashier = NewAccount(AicosRoles.Cashier, B1, "Sớm");
        SetLocal(Monday, 6, 0);
        await CashAsync(earlyCashier, 999_000, F.Clock.Now.UtcDateTime.AddHours(-8));       // Sunday evening: another shop day
        await CashAsync(earlyCashier, 120_000);                                              // earlier today, whoever took it
        await CashAsync(earlyCashier, -20_000);                                              // a cash refund
        await CashAsync(earlyCashier, 777_000, branch: B2);                                  // another branch
        await CashAsync(earlyCashier, 555_000, tenant: OtherTenant);                         // another brand
        var (_, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 7, 5);

        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000, note = "Đủ" });
        F.Events.Published.OfType<CashClosingSubmittedEvent>().Should().ContainSingle(e => e.ClosingId == closing.Id() && e.CountedAmount == 100_000);

        closing.GetProperty("expectedAmount").GetDecimal().Should().Be(100_000);
        closing.GetProperty("openingAmount").GetDecimal().Should().Be(0);
        closing.GetProperty("countedAmount").GetDecimal().Should().Be(100_000);
        closing.GetProperty("difference").GetDecimal().Should().Be(0);
        closing.GetProperty("status").GetString().Should().Be("PendingReview");
        closing.GetProperty("cashierNote").GetString().Should().Be("Đủ");
        closing.GetProperty("employeeName").GetString().Should().Be("Thu Ngân");
    }

    [Fact]
    public async Task The_next_cashier_takes_over_what_the_previous_handover_counted_plus_the_cash_taken_since()
    {
        var morning = await CashierOnShiftAsync("Ca Sáng");
        SetLocal(Monday, 7, 5);
        await morning.Client.PostOk(Closings, new { assignmentId = morning.Assignment, countedAmount = 50_000, note = "Tiền lẻ quản lý để lại" });
        SetLocal(Monday, 9, 0);
        await CashAsync(morning.User, 120_000);
        await CashAsync(morning.User, 80_000);
        await CashAsync(morning.User, -30_000);
        await CashAsync(morning.User, 777_000, branch: B2);

        var afternoon = await CashierOnShiftAsync("Ca Chiều", "12:00", "17:00");
        SetLocal(Monday, 12, 5);
        var preview = await (await afternoon.Client.GetAsync($"{Closings}/expected?assignmentId={afternoon.Assignment}")).Body();

        preview.GetProperty("openingAmount").GetDecimal().Should().Be(50_000);              // what the morning counted, not what it expected
        preview.GetProperty("previousCashierName").GetString().Should().Be("Ca Sáng");
        preview.GetProperty("cashReceived").GetDecimal().Should().Be(200_000);
        preview.GetProperty("cashRefunded").GetDecimal().Should().Be(30_000);
        preview.GetProperty("expectedAmount").GetDecimal().Should().Be(220_000);

        var closing = await afternoon.Client.PostOk(Closings, new { assignmentId = afternoon.Assignment, countedAmount = 220_000 });
        (closing.GetProperty("expectedAmount").GetDecimal(), closing.GetProperty("openingAmount").GetDecimal(), closing.GetProperty("difference").GetDecimal())
            .Should().Be((220_000m, 50_000m, 0m));
    }

    [Fact]
    public async Task A_new_shop_day_starts_from_an_empty_drawer()
    {
        var evening = await CashierOnShiftAsync("Ca Tối", "18:00", "22:00");
        SetLocal(Monday, 18, 5);
        await evening.Client.PostOk(Closings, new { assignmentId = evening.Assignment, countedAmount = 300_000, note = "Có tiền ca chiều" });

        var tuesday = await CashierOnShiftAsync("Sáng Thứ Ba", day: Monday.AddDays(1));
        SetLocal(Monday.AddDays(1), 7, 5);
        var preview = await (await tuesday.Client.GetAsync($"{Closings}/expected?assignmentId={tuesday.Assignment}")).Body();

        (preview.GetProperty("openingAmount").GetDecimal(), preview.GetProperty("expectedAmount").GetDecimal()).Should().Be((0m, 0m));
        preview.TryGetProperty("previousCashierName", out var name).Should().BeTrue();
        name.ValueKind.Should().Be(JsonValueKind.Null);
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

        var other = await CashierOnShiftAsync("Cashier Hai", "12:00", "17:00");
        SetLocal(Monday, 12, 10);
        (await other.Client.PostOk(Closings, new { assignmentId = other.Assignment, countedAmount = 180_000 })).GetProperty("difference").GetDecimal().Should().Be(30_000);
    }

    // ------------------------------------------------------------------ the expected amount before counting

    [Fact]
    public async Task The_cashier_previews_the_expected_amount_and_the_closing_uses_the_same_figure()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 8, 0);
        await CashAsync(user, 120_000);
        await CashAsync(user, 80_000);
        await CashAsync(user, -30_000);
        await CashAsync(user, 777_000, branch: B2);
        SetLocal(Monday, 12, 0);

        var preview = await (await cashier.GetAsync($"{Closings}/expected?assignmentId={assignment}")).Body();

        preview.GetProperty("assignmentId").GetGuid().Should().Be(assignment);
        preview.GetProperty("expectedAmount").GetDecimal().Should().Be(170_000);
        preview.GetProperty("cashReceived").GetDecimal().Should().Be(200_000);
        preview.GetProperty("cashRefunded").GetDecimal().Should().Be(30_000);
        preview.GetProperty("openingAmount").GetDecimal().Should().Be(0);

        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 170_000 });
        closing.GetProperty("expectedAmount").GetDecimal().Should().Be(preview.GetProperty("expectedAmount").GetDecimal());
    }

    [Fact]
    public async Task The_preview_follows_the_same_shift_rules_as_closing()
    {
        var (_, _, assignment, cashier) = await CashierOnShiftAsync();
        var barista = NewAccount(AicosRoles.Barista, B1, "Pha chế");
        var otherShift = await NewShiftAsync("Chiều", "13:00", "18:00");
        var (neverUser, neverEmp) = await NewEmployeeAsync("Chưa Làm", AicosRoles.Cashier);
        var notWorked = await AssignAsync(neverEmp, otherShift, Monday);

        await cashier.GetAsync($"{Closings}/expected").ShouldBe(400);
        await Staff(barista, AicosRoles.Barista).GetAsync($"{Closings}/expected?assignmentId={assignment}").ShouldBe(403, "cash_closing.cashier_only");
        await Staff(neverUser, AicosRoles.Cashier).GetAsync($"{Closings}/expected?assignmentId={notWorked}").ShouldBe(422, "cash_closing.shift_not_worked");
        await Staff(neverUser, AicosRoles.Cashier).GetAsync($"{Closings}/expected?assignmentId={assignment}").ShouldBe(404);   // a colleague's shift

        SetLocal(Monday, 12, 0);
        await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 0 });
        await cashier.GetAsync($"{Closings}/expected?assignmentId={assignment}").ShouldBe(409, "cash_closing.duplicate");
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

        var audit = F.Events.Published.OfType<AuditRecordedEvent>().Where(e => !e.Action.StartsWith("attendance.checked_")).Single();
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
        SetLocal(Monday, 7, 5);
        var exact = await first.Client.PostOk(Closings, new { assignmentId = first.Assignment, countedAmount = 0 });
        var second = await CashierOnShiftAsync("Thu Hai", "12:00", "17:00");
        SetLocal(Monday, 12, 5);
        var off = await second.Client.PostOk(Closings, new { assignmentId = second.Assignment, countedAmount = 10_000 });
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

    // ------------------------------------------------------------------ order exceptions of the shift (SCRUM-49, 62)
    [Fact]
    public async Task The_manager_sees_the_cash_movements_and_the_order_exceptions_of_the_shift()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 8, 0);
        await CashAsync(user, 120_000);
        await CashAsync(user, 80_000);
        await CashAsync(user, -30_000);
        OrderExceptions.Add("ORD-IN", 90_000, "RefundPending", F.Clock.Now.UtcDateTime.AddMinutes(30));
        OrderExceptions.Add("ORD-DONE", 40_000, "Refunded", F.Clock.Now.UtcDateTime.AddMinutes(40), refunded: 40_000);
        OrderExceptions.Add("ORD-BEFORE", 10_000, "RefundPending", F.Clock.Now.UtcDateTime.AddHours(-9));               // another shop day
        OrderExceptions.Add("ORD-AFTER", 10_000, "RefundPending", F.Clock.Now.UtcDateTime.AddHours(8));
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 170_000 });

        var details = await Manager.GetFromJsonAsync<JsonElement>($"{Closings}/{closing.Id()}/details");

        details.GetProperty("cashReceived").GetDecimal().Should().Be(200_000);
        details.GetProperty("cashRefunded").GetDecimal().Should().Be(30_000);
        details.GetProperty("cashTransactions").EnumerateArray().Select(t => t.GetProperty("isRefund").GetBoolean()).Should().Equal(false, false, true);
        details.GetProperty("orderExceptions").EnumerateArray().Select(e => e.GetProperty("orderCode").GetString()).Should().BeEquivalentTo("ORD-IN", "ORD-DONE");
        details.GetProperty("unresolvedExceptions").GetInt32().Should().Be(1);
        OrderExceptions.LastQuery!.Value.Branch.Should().Be(B1);
        await Manager2.GetAsync($"{Closings}/{closing.Id()}/details").ShouldBe(403, "branch.forbidden");
        await OtherAdmin.GetAsync($"{Closings}/{closing.Id()}/details").ShouldBe(404);
        await cashier.GetAsync($"{Closings}/{closing.Id()}/details").ShouldBe(403);
    }

    [Fact]
    public async Task A_shift_with_an_open_order_exception_is_only_confirmed_when_the_manager_acknowledges_it()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 8, 0);
        await CashAsync(user, 100_000);
        OrderExceptions.Add("ORD-OPEN", 50_000, "RefundFailed", F.Clock.Now.UtcDateTime.AddMinutes(20));
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000 });

        await Manager.Post($"{Closings}/{closing.Id()}/confirm", new { }).ShouldBe(422, "cash_closing.exceptions_unresolved");
        var confirmed = await Manager.PostOk($"{Closings}/{closing.Id()}/confirm", new { note = "Refund handled by hand", acknowledgeExceptions = true });

        confirmed.GetProperty("status").GetString().Should().Be("Confirmed");
        F.Events.Published.OfType<AuditRecordedEvent>().Single(e => e.Action == "cash_closing.confirmed").After.Should().Contain("\"openExceptionsAcknowledged\":1");
    }

    [Fact]
    public async Task A_shift_whose_exceptions_are_all_resolved_is_confirmed_normally()
    {
        var (user, _, assignment, cashier) = await CashierOnShiftAsync();
        SetLocal(Monday, 8, 0);
        await CashAsync(user, 100_000);
        OrderExceptions.Add("ORD-OK", 50_000, "Refunded", F.Clock.Now.UtcDateTime.AddMinutes(20), refunded: 50_000);
        SetLocal(Monday, 12, 0);
        var closing = await cashier.PostOk(Closings, new { assignmentId = assignment, countedAmount = 100_000 });

        (await Manager.PostOk($"{Closings}/{closing.Id()}/confirm", new { })).GetProperty("status").GetString().Should().Be("Confirmed");
    }
}
