using AICOS.Ordering.Api.Domain.Interfaces;

namespace AICOS.Ordering.Api.BackgroundServices;

/// <summary>
/// Thực thi quy tắc BR-09: Đơn thanh toán online thất bại / quá hạn tự động hủy sau 5 phút.
/// Đơn hàng được cập nhật trạng thái Status = Cancelled trực tiếp trong Ordering service.
/// </summary>
public class OnlineOrderTimeoutWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OnlineOrderTimeoutWorker> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    public OnlineOrderTimeoutWorker(IServiceProvider serviceProvider, ILogger<OnlineOrderTimeoutWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OnlineOrderTimeoutWorker (BR-09) bắt đầu chạy...");

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

                var expiredOrders = await repository.GetExpiredOnlineOrdersAsync(timeoutMinutes: 5);

                foreach (var order in expiredOrders)
                {
                    _logger.LogWarning("BR-09: Phát hiện đơn hàng {OrderId} ({OrderCode}) quá hạn thanh toán 5 phút. Đang tự động hủy...",
                        order.Id, order.OrderCode);

                    order.CancelOrder("BR-09: Tự động hủy do quá hạn thanh toán online 5 phút.");
                    await repository.UpdateAsync(order);

                    _logger.LogInformation("Đơn hàng {OrderId} ({OrderCode}) đã được cập nhật sang Cancelled.", order.Id, order.OrderCode);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình quét đơn hàng timeout (BR-09).");
            }
        }
    }
}
