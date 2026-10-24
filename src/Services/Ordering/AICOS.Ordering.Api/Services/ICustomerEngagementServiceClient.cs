namespace AICOS.Ordering.Api.Services;

public interface ICustomerEngagementServiceClient
{
    /// <summary>
    /// Kiểm tra tính hợp lệ của mã khuyến mãi và tính số tiền giảm
    /// </summary>
    Task<VoucherValidationResult> ValidateVoucherAsync(string code, decimal originalAmount, CancellationToken ct = default);
}

public record VoucherValidationResult(bool IsValid, decimal DiscountAmount, string? Message);

public class MockCustomerEngagementServiceClient : ICustomerEngagementServiceClient
{
    private readonly ILogger<MockCustomerEngagementServiceClient> _logger;

    public MockCustomerEngagementServiceClient(ILogger<MockCustomerEngagementServiceClient> logger)
    {
        _logger = logger;
    }

    public Task<VoucherValidationResult> ValidateVoucherAsync(string code, decimal originalAmount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult(new VoucherValidationResult(false, 0, "Mã khuyến mãi không được rỗng."));
        }

        var normalizedCode = code.Trim().ToUpperInvariant();

        switch (normalizedCode)
        {
            case "COFFEE10":
                // Giảm 10%
                var discount10 = Math.Round(originalAmount * 0.10m);
                _logger.LogInformation("Voucher COFFEE10 áp dụng thành công. Giảm {Discount:N0}đ.", discount10);
                return Task.FromResult(new VoucherValidationResult(true, discount10, "Áp dụng giảm giá 10% thành công."));

            case "GIAM20K":
                // Giảm cố định 20,000đ
                var discount20k = Math.Min(20000m, originalAmount);
                _logger.LogInformation("Voucher GIAM20K áp dụng thành công. Giảm {Discount:N0}đ.", discount20k);
                return Task.FromResult(new VoucherValidationResult(true, discount20k, "Áp dụng giảm 20.000đ thành công."));

            case "VIPFREE":
                // Giảm 100%
                return Task.FromResult(new VoucherValidationResult(true, originalAmount, "Voucher VIP miễn phí 100%."));

            default:
                _logger.LogWarning("Mã voucher {Code} không hợp lệ hoặc đã hết hạn.", code);
                return Task.FromResult(new VoucherValidationResult(false, 0, $"Mã khuyến mãi '{code}' không tồn tại hoặc đã hết hạn."));
        }
    }
}
