
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
