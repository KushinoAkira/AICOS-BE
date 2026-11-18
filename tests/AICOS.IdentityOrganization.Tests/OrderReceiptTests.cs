using AICOS.BuildingBlocks.Security;
using AICOS.Contracts.Events;
using AICOS.IdentityOrganization.Api.Domain;
using AICOS.IdentityOrganization.Api.Features.Receipts;
using AICOS.IdentityOrganization.Api.Infrastructure;
using AICOS.IdentityOrganization.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AICOS.IdentityOrganization.Tests;

/// <summary>The receipt a member gets by mail after paying an order online.</summary>
public sealed class OrderReceiptTests
{
    private static OrderReceiptRequestedIntegrationEvent Receipt(Guid customerId, string method = "PayOS", decimal discount = 10_000) => new(
        IdentityApiFactory.TenantId, Guid.NewGuid(), Guid.NewGuid(), "A1B2C3", customerId, "Takeaway", null, method, Guid.NewGuid(),
        new DateTime(2026, 11, 18, 2, 5, 0, DateTimeKind.Utc), 84_000, discount > 0 ? "GIAM10K" : null, discount, 84_000 - discount,
        [
            new ReceiptLineDto("Cà phê sữa đá", "Size L, Ít đá", null, 2, 32_000, 64_000),
            new ReceiptLineDto("Trà đào", null, "Không đường", 1, 20_000, 20_000),
        ]);

    [Fact]
    public void The_receipt_lists_the_lines_the_discount_the_total_and_the_method_in_both_parts()
    {
        var mail = EmailTemplates.OrderReceipt("an@example.com", "Nguyễn An", "Demo Coffee", "Chi nhánh 1", "12 Lê Lợi", "0909000000", Receipt(Guid.NewGuid()));

        mail.To.Should().Be("an@example.com");
        mail.Subject.Should().Be("Demo Coffee - Hóa đơn đơn hàng A1B2C3");
        mail.TextBody.Should().Contain("2 x Cà phê sữa đá (Size L, Ít đá)  64.000 đ").And.Contain("Giảm giá (GIAM10K): -10.000 đ")
            .And.Contain("Tổng thanh toán: 74.000 đ").And.Contain("Chuyển khoản ngân hàng (payOS)").And.Contain("18/11/2026 09:05")
            .And.Contain("Chi nhánh 1 - 12 Lê Lợi").And.Contain("0909000000");
        mail.HtmlBody.Should().StartWith("<!doctype html>").And.Contain("Cà phê sữa đá").And.Contain("Ghi chú: Không đường")
            .And.Contain("74.000 đ").And.Contain("Mang đi");
    }

    [Fact]
    public void A_receipt_without_discount_has_no_discount_row_and_names_are_escaped()
    {
        var e = Receipt(Guid.NewGuid(), "Momo", discount: 0) with { Lines = [new ReceiptLineDto("<script>x</script>", null, null, 1, 84_000, 84_000)] };

        var mail = EmailTemplates.OrderReceipt("an@example.com", null, "<b>Brand</b>", null, null, null, e);

        mail.TextBody.Should().NotContain("Giảm giá").And.Contain("Ví MoMo").And.Contain("Xin chào,");
        mail.HtmlBody.Should().NotContain("<script>").And.Contain("&lt;script&gt;").And.Contain("&lt;b&gt;Brand&lt;/b&gt;");
    }

    [Fact]
    public async Task The_handler_mails_the_member_and_skips_an_account_that_cannot_receive_it()
    {
        using var factory = new IdentityApiFactory();
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var member = new User("Nguyễn An", "an.receipt@example.com", username: null, phone: null, AicosRoles.Customer, branchId: null);
        var locked = new User("Bị Khóa", "locked.receipt@example.com", username: null, phone: null, AicosRoles.Customer, branchId: null);
        locked.ChangeStatus(UserStatus.Locked);
        db.Users.AddRange(member, locked);
        await db.SaveChangesAsync();
        var handler = new OrderReceiptHandler(db, factory.Emails, NullLogger<OrderReceiptHandler>.Instance);

        await handler.HandleAsync(Receipt(member.Id), CancellationToken.None);
        await handler.HandleAsync(Receipt(locked.Id), CancellationToken.None);
        await handler.HandleAsync(Receipt(Guid.NewGuid()), CancellationToken.None);

        var mail = factory.Emails.Messages.Should().ContainSingle().Subject;
        mail.To.Should().Be("an.receipt@example.com");
        mail.Subject.Should().EndWith("Hóa đơn đơn hàng A1B2C3");
        mail.TextBody.Should().Contain("Xin chào Nguyễn An,").And.Contain("74.000 đ");
    }

    [Fact]
    public async Task A_mail_server_that_is_down_does_not_fail_the_handler()
    {
        using var factory = new IdentityApiFactory();
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityOrganizationDbContext>();
        var member = new User("Nguyễn An", "an.down@example.com", username: null, phone: null, AicosRoles.Customer, branchId: null);
        db.Users.Add(member);
        await db.SaveChangesAsync();
        factory.Emails.Fail = new InvalidOperationException("SMTP down");

        var act = () => new OrderReceiptHandler(db, factory.Emails, NullLogger<OrderReceiptHandler>.Instance).HandleAsync(Receipt(member.Id), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
