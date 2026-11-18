
using System.Globalization;
using AICOS.Contracts.Events;

namespace AICOS.IdentityOrganization.Api.Infrastructure;

/// <summary>
/// The mails of the Identity service, in Vietnamese: a plain text part and an HTML part. Every value that comes from a user (names, the reason
/// of a refusal) is HTML-encoded in the HTML part, so a brand called <c>&lt;script&gt;</c> cannot inject markup into a mail.
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage PasswordReset(string to, string? fullName, string code, int validMinutes)
    {
        var text = $"{Greeting(fullName)}\n\nMã đặt lại mật khẩu của bạn là: {code}\nMã có hiệu lực trong {validMinutes} phút. Nếu bạn không yêu cầu, hãy bỏ qua email này.";
        var html = Layout(
            "Đặt lại mật khẩu",
            $"<p>{Html(Greeting(fullName))}</p><p>Mã đặt lại mật khẩu của bạn là:</p>"
            + $"<p style=\"font-size:30px;letter-spacing:8px;font-weight:700;margin:16px 0\">{Html(code)}</p>"
            + $"<p>Mã có hiệu lực trong {validMinutes} phút. Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>");
        return new EmailMessage(to, "AICOS - Mã đặt lại mật khẩu", text, html);
    }

    public static EmailMessage SignUpCode(string to, string? fullName, string code, int validMinutes)
    {
        var text = $"{Greeting(fullName)}\n\nMã xác thực tài khoản AICOS của bạn là: {code}\nMã có hiệu lực trong {validMinutes} phút. Nếu bạn không đăng ký, hãy bỏ qua email này.";
        var html = Layout(
            "Xác thực email",
            $"<p>{Html(Greeting(fullName))}</p><p>Mã xác thực tài khoản của bạn là:</p>"
            + $"<p style=\"font-size:30px;letter-spacing:8px;font-weight:700;margin:16px 0\">{Html(code)}</p>"
            + $"<p>Mã có hiệu lực trong {validMinutes} phút. Nếu bạn không đăng ký, hãy bỏ qua email này.</p>");
        return new EmailMessage(to, "AICOS - Mã xác thực tài khoản", text, html);
    }

    public static EmailMessage BrandRegistrationReceived(string to, string? ownerName, string brandName)
    {
        var text = $"{Greeting(ownerName)}\n\nChúng tôi đã nhận đăng ký thương hiệu \"{brandName}\". Đội ngũ nền tảng sẽ xem xét và báo kết quả qua email này. Bạn đăng nhập được sau khi đăng ký được duyệt.";
        var html = Layout(
            "Đã nhận đăng ký",
            $"<p>{Html(Greeting(ownerName))}</p><p>Chúng tôi đã nhận đăng ký thương hiệu <strong>{Html(brandName)}</strong>. Đội ngũ nền tảng sẽ xem xét và báo kết quả qua email này.</p>"
            + "<p>Bạn đăng nhập được sau khi đăng ký được duyệt.</p>");
        return new EmailMessage(to, "AICOS - Đã nhận đăng ký thương hiệu", text, html);
    }

    public static EmailMessage BrandApproved(string to, string? ownerName, string brandName, string? appUrl)
    {
        var link = string.IsNullOrWhiteSpace(appUrl) ? null : appUrl.TrimEnd('/') + "/login";
        var text = $"{Greeting(ownerName)}\n\nThương hiệu \"{brandName}\" đã được duyệt. Bạn có thể đăng nhập ngay bây giờ.{(link is null ? string.Empty : $"\nĐăng nhập: {link}")}";
        var html = Layout(
            "Đăng ký đã được duyệt",
            $"<p>{Html(Greeting(ownerName))}</p><p>Thương hiệu <strong>{Html(brandName)}</strong> đã được duyệt. Bạn có thể đăng nhập ngay bây giờ.</p>"
            + (link is null ? string.Empty : $"<p><a href=\"{Html(link)}\" style=\"display:inline-block;background:#6f4e37;color:#ffffff;padding:12px 20px;border-radius:8px;text-decoration:none\">Đăng nhập</a></p>"));
        return new EmailMessage(to, "AICOS - Đăng ký thương hiệu đã được duyệt", text, html);
    }

    public static EmailMessage BrandRejected(string to, string? ownerName, string brandName, string reason)
    {
        var text = $"{Greeting(ownerName)}\n\nRất tiếc, đăng ký thương hiệu \"{brandName}\" chưa được duyệt.\nLý do: {reason}\nBạn có thể liên hệ đội ngũ nền tảng để được hỗ trợ.";
        var html = Layout(
            "Đăng ký chưa được duyệt",
            $"<p>{Html(Greeting(ownerName))}</p><p>Rất tiếc, đăng ký thương hiệu <strong>{Html(brandName)}</strong> chưa được duyệt.</p>"
            + $"<p><strong>Lý do:</strong> {Html(reason)}</p><p>Bạn có thể liên hệ đội ngũ nền tảng để được hỗ trợ.</p>");
        return new EmailMessage(to, "AICOS - Đăng ký thương hiệu chưa được duyệt", text, html);
    }

    /// <summary>The receipt of an order a member paid online: brand and branch, order code and time, the lines, the discount, the total and how it was paid.</summary>
    public static EmailMessage OrderReceipt(
        string to, string? fullName, string brandName, string? branchName, string? branchAddress, string? branchPhone, OrderReceiptRequestedIntegrationEvent e)
    {
        var paidAt = e.PaidAtUtc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var method = PaymentMethodName(e.PaymentMethod);
        var where = string.Join(" - ", new[] { branchName, branchAddress }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var kind = OrderTypeName(e.OrderType, e.TableNumber);

        var text = new System.Text.StringBuilder()
            .AppendLine(Greeting(fullName)).AppendLine()
            .AppendLine($"Cảm ơn bạn đã đặt hàng tại {brandName}. Đơn {e.OrderCode} đã được thanh toán.").AppendLine()
            .AppendLine($"Cửa hàng: {(where.Length == 0 ? brandName : where)}")
            .AppendLine($"Mã đơn: {e.OrderCode} ({kind})")
            .AppendLine($"Thời gian: {paidAt}")
            .AppendLine();
        foreach (var l in e.Lines)
        {
            text.AppendLine($"{l.Quantity} x {l.Name}{(l.Options is null ? string.Empty : $" ({l.Options})")}  {Money(l.LineTotal)}");
        }

        text.AppendLine().AppendLine($"Tạm tính: {Money(e.Subtotal)}");
        if (e.Discount > 0)
        {
            text.AppendLine($"Giảm giá{(string.IsNullOrWhiteSpace(e.PromotionCode) ? string.Empty : $" ({e.PromotionCode})")}: -{Money(e.Discount)}");
        }

        text.AppendLine($"Tổng thanh toán: {Money(e.Total)}").AppendLine($"Phương thức: {method}");
        if (!string.IsNullOrWhiteSpace(branchPhone))
        {
            text.AppendLine().AppendLine($"Cần hỗ trợ về đơn này, gọi cửa hàng: {branchPhone}");
        }

        var rows = string.Concat(e.Lines.Select(l =>
            "<tr>"
            + $"<td style="padding:8px 0;border-bottom:1px solid #eee8df;vertical-align:top"><strong>{l.Quantity} × {Html(l.Name)}</strong>"
            + (l.Options is null ? string.Empty : $"<br><span style="color:#777777;font-size:13px">{Html(l.Options)}</span>")
            + (string.IsNullOrWhiteSpace(l.Note) ? string.Empty : $"<br><span style="color:#777777;font-size:13px">Ghi chú: {Html(l.Note)}</span>")
            + $"<br><span style="color:#777777;font-size:13px">{Money(l.UnitPrice)} / ly</span></td>"
            + $"<td align="right" style="padding:8px 0;border-bottom:1px solid #eee8df;vertical-align:top;white-space:nowrap">{Money(l.LineTotal)}</td>"
            + "</tr>"));
        var discount = e.Discount <= 0
            ? string.Empty
            : $"<tr><td style="padding:4px 0">Giảm giá{(string.IsNullOrWhiteSpace(e.PromotionCode) ? string.Empty : $" ({Html(e.PromotionCode)})")}</td><td align="right" style="padding:4px 0;color:#2e7d32">-{Money(e.Discount)}</td></tr>";
        var html = Layout(
            $"Hóa đơn {e.OrderCode}",
            $"<p>{Html(Greeting(fullName))}</p><p>Cảm ơn bạn đã đặt hàng tại <strong>{Html(brandName)}</strong>. Đơn của bạn đã được thanh toán.</p>"
            + "<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:14px;margin:12px 0;color:#555555">"
            + (where.Length == 0 ? string.Empty : $"<tr><td style="padding:2px 0">Cửa hàng</td><td align="right" style="padding:2px 0">{Html(where)}</td></tr>")
            + $"<tr><td style="padding:2px 0">Mã đơn</td><td align="right" style="padding:2px 0"><strong>{Html(e.OrderCode)}</strong> · {Html(kind)}</td></tr>"
            + $"<tr><td style="padding:2px 0">Thời gian</td><td align="right" style="padding:2px 0">{paidAt}</td></tr>"
            + "</table>"
            + $"<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:15px;border-top:2px solid #6f4e37">{rows}</table>"
            + "<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:15px;margin-top:8px">"
            + $"<tr><td style="padding:4px 0">Tạm tính</td><td align="right" style="padding:4px 0">{Money(e.Subtotal)}</td></tr>"
            + discount
            + $"<tr><td style="padding:8px 0;font-size:17px;font-weight:700">Tổng thanh toán</td><td align="right" style="padding:8px 0;font-size:17px;font-weight:700;color:#6f4e37">{Money(e.Total)}</td></tr>"
            + $"<tr><td style="padding:4px 0;color:#555555">Phương thức</td><td align="right" style="padding:4px 0;color:#555555">{Html(method)}</td></tr>"
            + "</table>"
            + (string.IsNullOrWhiteSpace(branchPhone) ? string.Empty : $"<p style="color:#555555;font-size:13px">Cần hỗ trợ về đơn này, gọi cửa hàng: {Html(branchPhone)}</p>"));
        return new EmailMessage(to, $"{brandName} - Hóa đơn đơn hàng {e.OrderCode}", text.ToString(), html);
    }

    /// <summary>74000 -> "74.000 đ".</summary>
    private static string Money(decimal amount) => amount.ToString("#,##0", CultureInfo.InvariantCulture).Replace(',', '.') + " đ";

    private static string PaymentMethodName(string code) => code switch
    {
        "PayOS" => "Chuyển khoản ngân hàng (payOS)",
        "VietQR" => "Chuyển khoản ngân hàng (VietQR)",
        "Momo" => "Ví MoMo",
        "ZaloPay" => "ZaloPay",
        "Mock" => "Thanh toán trực tuyến",
        _ => code,
    };

    private static string OrderTypeName(string type, string? table) => type switch
    {
        "DineIn" => string.IsNullOrWhiteSpace(table) ? "Tại quán" : $"Tại quán, bàn {table}",
        "Takeaway" => "Mang đi",
        "Delivery" => "Giao hàng",
        _ => type,
    };

    private static string Greeting(string? name) => string.IsNullOrWhiteSpace(name) ? "Xin chào," : $"Xin chào {name.Trim()},";

    /// <summary>Escapes the five characters that matter in text and in quoted attributes; accented letters stay readable instead of becoming numeric entities.</summary>
    private static string Html(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>A small, mail-client friendly page (tables and inline styles only).</summary>
    private static string Layout(string title, string content) =>
        "<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>"
        + "<body style=\"margin:0;background:#f5efe6;font-family:Arial,Helvetica,sans-serif;color:#2b2b2b\">"
        + "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px 12px\">"
        + "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#ffffff;border-radius:12px;overflow:hidden\">"
        + "<tr><td style=\"background:#6f4e37;color:#ffffff;padding:18px 24px;font-size:20px;font-weight:700\">AICOS</td></tr>"
        + $"<tr><td style=\"padding:24px;font-size:15px;line-height:1.55\"><h1 style=\"font-size:20px;margin:0 0 12px\">{Html(title)}</h1>{content}</td></tr>"
        + "<tr><td style=\"padding:14px 24px;background:#faf7f2;color:#777777;font-size:12px\">Email tự động, vui lòng không trả lời.</td></tr>"
        + "</table></td></tr></table></body></html>";
}
