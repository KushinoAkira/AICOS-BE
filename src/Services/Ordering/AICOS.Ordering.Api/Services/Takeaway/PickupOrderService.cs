using AICOS.BuildingBlocks.Exceptions;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace AICOS.Ordering.Api.Services.Takeaway;

public sealed class PickupOrderService(OrderingDbContext db)
{
    private static readonly Dictionary<string, string> AllowedStaffTransitions = new()
    {
        [TakeawayOrderStatuses.Paid] = TakeawayOrderStatuses.Preparing,
        [TakeawayOrderStatuses.Preparing] = TakeawayOrderStatuses.Ready,
        [TakeawayOrderStatuses.Ready] = TakeawayOrderStatuses.PickedUp
    };

    public async Task<IReadOnlyList<PickupSlotDto>> GetPickupSlotsAsync(Guid branchId, DateOnly date, CancellationToken ct)
    {
        var settings = await GetOrCreateSettingsAsync(branchId, ct);
        var booked = await db.PickupSlots
            .Where(s => s.BranchId == branchId && s.SlotStart.Date == date.ToDateTime(TimeOnly.MinValue).Date)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var cutoff = now.AddMinutes(settings.CutoffMinutes);
        var slots = new List<PickupSlotDto>();
        var cursor = date.ToDateTime(settings.OpenTime, DateTimeKind.Utc);
        var close = date.ToDateTime(settings.CloseTime, DateTimeKind.Utc);

        while (cursor < close)
        {
            var existing = booked.FirstOrDefault(s => s.SlotStart == cursor);
            var capacity = existing?.Capacity ?? settings.CapacityPerSlot;
            var bookedCount = existing?.BookedCount ?? 0;
            slots.Add(new PickupSlotDto(
                SlotStart: cursor,
                Capacity: capacity,
                BookedCount: bookedCount,
                Remaining: Math.Max(0, capacity - bookedCount),
                Expired: cursor < cutoff));
            cursor = cursor.AddMinutes(settings.SlotMinutes);
        }

        return slots;
    }

    public async Task<(PickupOrderCreatedDto Result, bool Created)> CreatePickupOrderAsync(
        Guid customerId,
        Guid tenantId,
        string? idempotencyKey,
        CreatePickupOrderRequest request,
        CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new BusinessRuleException("ITEM_UNAVAILABLE", "Đơn phải có ít nhất một món.");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await db.Orders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.UserId == customerId && o.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return (ToCreatedDto(existing), false);
            }
        }

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == request.BranchId && b.TenantId == tenantId, ct)
            ?? throw new NotFoundException("Branch", request.BranchId);

        var settings = await GetOrCreateSettingsAsync(branch.Id, ct);
        ValidateSlotGrid(settings, request.SlotStart);

        if (request.SlotStart < DateTime.UtcNow.AddMinutes(settings.CutoffMinutes))
        {
            throw new BusinessRuleException("SLOT_CUTOFF", "Khung giờ đã quá hạn đặt.");
        }

        Guid? redemptionId = null;
        if (request.RedemptionId is Guid rid)
        {
            var redemption = await ValidateRedemptionAsync(rid, customerId, tenantId, ct);
            redemptionId = redemption.Id;
        }

        var pricedItems = await PriceItemsAsync(branch.Id, request.Items, ct);
        var subtotal = pricedItems.Sum(i => i.LineTotal);
        var paymentDeadline = DateTime.UtcNow.AddMinutes(settings.HoldMinutes);

        IDisposable? tx = null;
        try
        {
            if (db.Database.IsRelational())
            {
                tx = await db.Database.BeginTransactionAsync(ct);
            }

            var slot = await EnsureSlotAsync(branch.Id, request.SlotStart, settings.CapacityPerSlot, ct);
            if (!await TryIncrementBookedCountAsync(slot.Id, ct))
            {
                throw new ConflictException("Khung giờ đã hết chỗ.", "SLOT_FULL");
            }

            var order = new OrderEntity
            {
                Id = Guid.NewGuid(),
                BranchId = branch.Id,
                UserId = customerId,
                OrderCode = $"TW-{DateTime.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}",
                OrderType = "TAKEAWAY",
                Status = TakeawayOrderStatuses.PendingPayment,
                TotalAmount = subtotal,
                FinalAmount = subtotal,
                Subtotal = subtotal,
                PaymentStatus = "UNPAID",
                ServiceType = "TAKEAWAY",
                IsTakeaway = true,
                ScheduledPickupTime = request.SlotStart,
                PickupTime = request.SlotStart,
                PaymentDeadline = paymentDeadline,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                RedemptionId = redemptionId,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var item in pricedItems)
            {
                var orderItem = new OrderItemEntity
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    ProductId = item.ProductId,
                    VariantId = item.VariantId,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Subtotal = item.LineTotal,
                    ProductName = item.ProductName,
                    VariantName = item.VariantName,
                    Note = item.Note,
                    PreparationStatus = "PENDING"
                };
                foreach (var opt in item.Options)
                {
                    orderItem.Options.Add(new OrderItemOptionEntity
                    {
                        Id = Guid.NewGuid(),
                        OrderItemId = orderItem.Id,
                        OptionId = opt.OptionId,
                        OptionName = opt.OptionName,
                        AdditionalPrice = opt.ExtraPrice,
                        ExtraPrice = opt.ExtraPrice
                    });
                }
                order.Items.Add(orderItem);
            }

            db.Orders.Add(order);
            AddHistory(order.Id, customerId, null, TakeawayOrderStatuses.PendingPayment, "Tạo đơn mang đi");
            await db.SaveChangesAsync(ct);
            if (tx is IAsyncDisposable asyncTx)
            {
                await db.Database.CommitTransactionAsync(ct);
                await asyncTx.DisposeAsync();
            }
            else
            {
                tx?.Dispose();
            }
            return (ToCreatedDto(order), true);
        }
        catch
        {
            if (db.Database.CurrentTransaction is not null)
            {
                await db.Database.RollbackTransactionAsync(ct);
            }
            tx?.Dispose();
            throw;
        }
    }

    public async Task<PickupPaymentDto> StartPaymentAsync(Guid orderId, Guid customerId, CancellationToken ct)
    {
        var order = await db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == customerId, ct)
            ?? throw new NotFoundException("Order", orderId);

        if (order.Status != TakeawayOrderStatuses.PendingPayment ||
            order.PaymentDeadline is null ||
            order.PaymentDeadline <= DateTime.UtcNow)
        {
            throw new ConflictException("Đơn không còn thanh toán được.", "ORDER_NOT_PAYABLE");
        }

        var bill = await db.Bills.Include(b => b.Items).FirstOrDefaultAsync(b => b.OrderId == order.Id, ct);
        if (bill is null)
        {
            bill = new BillEntity
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                BillCode = $"BILL-{order.OrderCode}",
                TotalAmount = order.FinalAmount,
                FinalAmount = order.FinalAmount,
                PaymentMethod = "BANK_TRANSFER",
                Status = "UNPAID",
                PaymentTime = null
            };
            foreach (var item in order.Items)
            {
                bill.Items.Add(new BillItemEntity
                {
                    Id = Guid.NewGuid(),
                    BillId = bill.Id,
                    ProductName = item.ProductName,
                    VariantName = item.VariantName,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Subtotal = item.Subtotal
                });
            }
            db.Bills.Add(bill);
        }

        var payment = new PaymentEntity
        {
            Id = Guid.NewGuid(),
            BillId = bill.Id,
            Method = "BANK_TRANSFER",
            Status = "PENDING",
            Amount = bill.FinalAmount,
            TransactionRef = $"PAY-{Guid.NewGuid():N}"
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        return new PickupPaymentDto(
            payment.Id,
            payment.TransactionRef,
            payment.Amount,
            $"aicos://pay/{payment.TransactionRef}",
            $"000201{payment.TransactionRef}");
    }

    public async Task HandlePaymentWebhookAsync(PaymentWebhookRequest request, CancellationToken ct)
    {
        var inserted = await TryInsertWebhookEventAsync(request, ct);
        if (!inserted)
        {
            return;
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.TransactionRef == request.TransactionRef, ct)
            ?? throw new NotFoundException("Payment", request.TransactionRef);

        if (request.Amount != payment.Amount)
        {
            return;
        }

        var bill = await db.Bills.FirstAsync(b => b.Id == payment.BillId, ct);
        var order = await db.Orders.FirstAsync(o => o.Id == bill.OrderId, ct);

        payment.Status = "SUCCESS";
        payment.PaidAt = DateTime.UtcNow;
        bill.Status = "PAID";
        bill.PaymentTime = DateTime.UtcNow;

        if (order.Status == TakeawayOrderStatuses.Cancelled)
        {
            db.Refunds.Add(new RefundEntity
            {
                PaymentId = payment.Id,
                Amount = payment.Amount,
                Reason = "Paid after cancellation",
                Status = "PENDING"
            });
        }
        else if (order.Status == TakeawayOrderStatuses.PendingPayment)
        {
            var from = order.Status;
            order.Status = TakeawayOrderStatuses.Paid;
            order.PaymentStatus = "PAID";
            order.PickupCode = await GeneratePickupCodeAsync(order.BranchId, ct);
            AddHistory(order.Id, null, from, TakeawayOrderStatuses.Paid, "Thanh toán thành công");
            if (order.UserId is Guid customerId)
            {
                NotificationWriter.Add(db, customerId, "ORDER_PAID", "Đã thanh toán", "Đơn mang đi đã được thanh toán.", new { order.Id });
            }

            if (order.RedemptionId is Guid redemptionId)
            {
                var redemption = await db.RewardRedemptions.FirstOrDefaultAsync(r => r.Id == redemptionId, ct);
                if (redemption is not null)
                {
                    redemption.Status = "USED";
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid orderId, Guid actorUserId, bool asStaff, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException("Order", orderId);

        if (!asStaff && order.UserId != actorUserId)
        {
            throw new NotFoundException("Order", orderId);
        }

        if (asStaff && !await StaffCanAccessBranchAsync(actorUserId, order.BranchId, ct))
        {
            throw new NotFoundException("Order", orderId);
        }

        if (order.Status is TakeawayOrderStatuses.Cancelled or TakeawayOrderStatuses.PickedUp or TakeawayOrderStatuses.NoShow)
        {
            throw new ConflictException("Đơn không thể hủy.", "ORDER_NOT_CANCELLABLE");
        }

        var settings = await GetOrCreateSettingsAsync(order.BranchId, ct);
        var lockTime = (order.PickupTime ?? order.ScheduledPickupTime)?.AddMinutes(-settings.LockMinutes);
        if (order.Status != TakeawayOrderStatuses.PendingPayment &&
            lockTime is not null &&
            DateTime.UtcNow >= lockTime)
        {
            throw new BusinessRuleException("ORDER_LOCKED", "Đã qua thời điểm khóa đơn, không thể hủy.");
        }

        var from = order.Status;
        var wasPaid = order.Status is TakeawayOrderStatuses.Paid
            or TakeawayOrderStatuses.Preparing
            or TakeawayOrderStatuses.Ready;

        order.Status = TakeawayOrderStatuses.Cancelled;
        order.CancelledAt = DateTime.UtcNow;
        order.CancelReason = TakeawayCancelReasons.Customer;
        AddHistory(order.Id, actorUserId, from, TakeawayOrderStatuses.Cancelled, "Hủy đơn");

        if (order.PickupTime is DateTime slotStart)
        {
            await ReleaseSlotAsync(order.BranchId, slotStart, ct);
        }

        if (wasPaid)
        {
            var payment = await db.Payments
                .Where(p => db.Bills.Any(b => b.Id == p.BillId && b.OrderId == order.Id) && p.Status == "SUCCESS")
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (payment is not null)
            {
                db.Refunds.Add(new RefundEntity
                {
                    PaymentId = payment.Id,
                    Amount = payment.Amount,
                    Reason = "Customer cancel",
                    Status = "PENDING"
                });
            }
        }

        if (order.RedemptionId is Guid redemptionId)
        {
            var redemption = await db.RewardRedemptions.FirstOrDefaultAsync(r => r.Id == redemptionId, ct);
            if (redemption is not null && redemption.ExpiresAt > DateTime.UtcNow && redemption.Status == "USED")
            {
                redemption.Status = "REDEEMED";
            }
        }

        if (order.UserId is Guid customerId)
        {
            NotificationWriter.Add(db, customerId, "ORDER_CANCELLED", "Đơn đã hủy", "Đơn mang đi đã được hủy.", new { order.Id });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ChangeStaffStatusAsync(Guid orderId, Guid staffUserId, string to, string? pickupCode, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException("Order", orderId);

        if (!await StaffCanAccessBranchAsync(staffUserId, order.BranchId, ct))
        {
            throw new NotFoundException("Order", orderId);
        }

        if (!AllowedStaffTransitions.TryGetValue(order.Status, out var expected) || expected != to)
        {
            throw new BusinessRuleException("INVALID_STATUS", $"Không chuyển được từ {order.Status} sang {to}.");
        }

        if (to == TakeawayOrderStatuses.PickedUp &&
            !string.Equals(order.PickupCode, pickupCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException("WRONG_PICKUP_CODE", "Mã nhận món không khớp.");
        }

        var from = order.Status;
        order.Status = to;
        if (to == TakeawayOrderStatuses.Ready)
        {
            order.ReadyAt = DateTime.UtcNow;
            if (order.UserId is Guid customerId)
            {
                NotificationWriter.Add(db, customerId, "ORDER_READY", "Món sẵn sàng", "Vui lòng đến nhận đơn.", new { order.Id });
            }
        }

        if (to == TakeawayOrderStatuses.PickedUp)
        {
            order.PickedUpAt = DateTime.UtcNow;
            await AwardPointsAsync(order, ct);
        }

        AddHistory(order.Id, staffUserId, from, to, "Cập nhật trạng thái nhân viên");
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<OrderListItemDto> Items, string? NextCursor)> ListMyOrdersAsync(
        Guid customerId,
        string? status,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);
        var query = db.Orders.AsNoTracking().Where(o => o.UserId == customerId && o.IsTakeaway);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(o => o.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(cursor) && TryParseCursor(cursor, out var createdAt, out var id))
        {
            query = query.Where(o => o.CreatedAt < createdAt || (o.CreatedAt == createdAt && o.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(limit + 1)
            .Select(o => new OrderListItemDto(o.Id, o.OrderCode, o.Status, o.FinalAmount, o.PickupTime, o.CreatedAt))
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            next = $"{last.CreatedAt:O}|{last.Id}";
            rows = rows.Take(limit).ToList();
        }

        return (rows, next);
    }

    public async Task<OrderDetailDto> GetMyOrderAsync(Guid orderId, Guid customerId, CancellationToken ct)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == customerId, ct)
            ?? throw new NotFoundException("Order", orderId);

        var history = await db.OrderStatusHistory
            .AsNoTracking()
            .Where(h => h.OrderId == order.Id)
            .OrderBy(h => h.CreatedAt)
            .Select(h => new OrderStatusHistoryDto(h.FromStatus, h.ToStatus, h.Note, h.CreatedAt))
            .ToListAsync(ct);

        return new OrderDetailDto(
            order.Id,
            order.OrderCode,
            order.Status,
            order.FinalAmount,
            order.PickupTime,
            order.PickupCode,
            order.PaymentDeadline,
            order.CreatedAt,
            order.Items.Select(i => new PickupOrderLineDto(
                i.Id,
                i.ProductName,
                i.VariantName,
                i.Quantity,
                i.UnitPrice,
                i.Subtotal,
                i.Note,
                i.Options.Select(o => new OrderItemOptionDto(o.OptionName, o.ExtraPrice)).ToList())).ToList(),
            history);
    }

    public async Task SubmitFeedbackAsync(Guid orderId, Guid customerId, int rating, string? comment, CancellationToken ct)
    {
        if (rating is < 1 or > 5)
        {
            throw new BusinessRuleException("INVALID_RATING", "Đánh giá phải từ 1 đến 5.");
        }

        if (comment is { Length: > 1000 })
        {
            throw new BusinessRuleException("INVALID_COMMENT", "Bình luận tối đa 1000 ký tự.");
        }

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == customerId, ct)
            ?? throw new NotFoundException("Order", orderId);

        if (order.Status != TakeawayOrderStatuses.PickedUp)
        {
            throw new ConflictException("Chỉ đánh giá đơn đã nhận.", "ORDER_NOT_COMPLETED");
        }

        if (await db.Feedbacks.AnyAsync(f => f.OrderId == orderId, ct))
        {
            throw new ConflictException("Đơn đã được đánh giá.", "ALREADY_REVIEWED");
        }

        db.Feedbacks.Add(new FeedbackEntity
        {
            OrderId = orderId,
            UserId = customerId,
            Rating = rating,
            Comment = comment
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task RunExpiryJobAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var unpaid = await db.Orders
            .Where(o => o.IsTakeaway && o.Status == TakeawayOrderStatuses.PendingPayment && o.PaymentDeadline != null && o.PaymentDeadline < now)
            .ToListAsync(ct);

        foreach (var order in unpaid)
        {
            var from = order.Status;
            order.Status = TakeawayOrderStatuses.Cancelled;
            order.CancelReason = TakeawayCancelReasons.PaymentTimeout;
            order.CancelledAt = now;
            AddHistory(order.Id, null, from, TakeawayOrderStatuses.Cancelled, "Hết hạn thanh toán");
            if (order.PickupTime is DateTime slotStart)
            {
                await ReleaseSlotAsync(order.BranchId, slotStart, ct);
            }

            var bills = await db.Bills.Where(b => b.OrderId == order.Id && b.Status == "UNPAID").ToListAsync(ct);
            foreach (var bill in bills)
            {
                bill.Status = "CANCELLED";
            }
        }

        var noShowCutoff = now.AddMinutes(-30);
        var noShows = await db.Orders
            .Where(o => o.IsTakeaway &&
                        (o.Status == TakeawayOrderStatuses.Paid || o.Status == TakeawayOrderStatuses.Preparing || o.Status == TakeawayOrderStatuses.Ready) &&
                        o.PickupTime != null &&
                        o.PickupTime < noShowCutoff)
            .ToListAsync(ct);

        foreach (var order in noShows)
        {
            var from = order.Status;
            order.Status = TakeawayOrderStatuses.NoShow;
            AddHistory(order.Id, null, from, TakeawayOrderStatuses.NoShow, "Không đến nhận");
            if (order.UserId is Guid customerId)
            {
                NotificationWriter.Add(db, customerId, "ORDER_NO_SHOW", "Không nhận món", "Đơn đã chuyển sang không nhận.", new { order.Id });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AwardPointsAsync(OrderEntity order, CancellationToken ct)
    {
        if (order.UserId is not Guid userId)
        {
            return;
        }

        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == order.BranchId, ct);
        var account = await db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == branch.TenantId, ct);
        if (account is null)
        {
            account = new LoyaltyAccountEntity
            {
                TenantId = branch.TenantId,
                UserId = userId,
                TotalPoints = 0,
                CurrentTier = "STANDARD"
            };
            db.LoyaltyAccounts.Add(account);
            await db.SaveChangesAsync(ct);
        }

        if (await db.LoyaltyTransactions.AnyAsync(t => t.AccountId == account.Id && t.OrderId == order.Id && t.Type == "EARN", ct))
        {
            return;
        }

        var points = (int)Math.Floor(order.FinalAmount / 10000m);
        if (points <= 0)
        {
            return;
        }

        account.TotalPoints += points;
        db.LoyaltyTransactions.Add(new LoyaltyTransactionEntity
        {
            AccountId = account.Id,
            OrderId = order.Id,
            PointsChange = points,
            Type = "EARN"
        });
        NotificationWriter.Add(db, userId, "POINTS_EARNED", "Cộng điểm", $"Bạn nhận {points} điểm.", new { order.Id, points });
    }

    private async Task<RewardRedemptionEntity> ValidateRedemptionAsync(Guid redemptionId, Guid customerId, Guid tenantId, CancellationToken ct)
    {
        var redemption = await db.RewardRedemptions.FirstOrDefaultAsync(r => r.Id == redemptionId, ct)
            ?? throw new BusinessRuleException("INVALID_VOUCHER", "Voucher không tồn tại.");
        var account = await db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.Id == redemption.AccountId, ct);
        if (account is null || account.UserId != customerId || account.TenantId != tenantId)
        {
            throw new BusinessRuleException("INVALID_VOUCHER", "Voucher không thuộc tài khoản này.");
        }

        if (redemption.Status != "REDEEMED" || redemption.ExpiresAt <= DateTime.UtcNow)
        {
            throw new BusinessRuleException("INVALID_VOUCHER", "Voucher không còn hiệu lực.");
        }

        return redemption;
    }

    private async Task<List<PricedItem>> PriceItemsAsync(Guid branchId, IReadOnlyList<CreatePickupItemRequest> items, CancellationToken ct)
    {
        var result = new List<PricedItem>();
        foreach (var item in items)
        {
            if (item.Quantity <= 0)
            {
                throw new BusinessRuleException("ITEM_UNAVAILABLE", "Số lượng phải lớn hơn 0.");
            }

            var variant = await db.ProductVariants
                .Include(v => v.Product)
                .FirstOrDefaultAsync(v => v.Id == item.VariantId && v.IsActive, ct);
            if (variant?.Product is null || !variant.Product.IsActive)
            {
                throw new BusinessRuleException("ITEM_UNAVAILABLE", "Món không còn kinh doanh.");
            }

            var available = await db.BranchProducts
                .AnyAsync(bp => bp.BranchId == branchId && bp.ProductId == variant.ProductId && bp.IsAvailable, ct);
            if (!available)
            {
                throw new BusinessRuleException("ITEM_UNAVAILABLE", $"Món {variant.Product.Name} tạm hết tại chi nhánh.");
            }

            var groups = await db.ProductOptionGroups
                .Include(g => g.Options)
                .Where(g => g.ProductId == variant.ProductId)
                .ToListAsync(ct);

            var selected = item.OptionIds ?? [];
            var pricedOptions = new List<PricedOption>();
            foreach (var group in groups)
            {
                var selectedInGroup = group.Options.Where(o => selected.Contains(o.Id)).ToList();
                var min = group.MinSelect > 0 ? group.MinSelect : (group.IsRequired ? 1 : 0);
                var max = group.MaxSelection > 0 ? group.MaxSelection : 1;
                if (selectedInGroup.Count < min || selectedInGroup.Count > max)
                {
                    throw new BusinessRuleException("INVALID_OPTION", $"Lựa chọn nhóm {group.Name} không hợp lệ.");
                }

                foreach (var option in selectedInGroup)
                {
                    pricedOptions.Add(new PricedOption(option.Id, option.Name, option.PriceAdjustment));
                }
            }

            if (selected.Any(id => groups.SelectMany(g => g.Options).All(o => o.Id != id)))
            {
                throw new BusinessRuleException("INVALID_OPTION", "Có lựa chọn không thuộc món.");
            }

            var unitPrice = (variant.Product.BasePrice) + variant.PriceAdjustment;
            var extra = pricedOptions.Sum(o => o.ExtraPrice);
            var line = (unitPrice + extra) * item.Quantity;
            result.Add(new PricedItem(
                variant.ProductId,
                variant.Id,
                variant.Product.Name,
                variant.Name,
                item.Quantity,
                unitPrice,
                line,
                item.Note,
                pricedOptions));
        }

        return result;
    }

    private async Task<bool> TryIncrementBookedCountAsync(Guid slotId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            var updated = await db.PickupSlots
                .Where(s => s.Id == slotId && s.BookedCount < s.Capacity)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.BookedCount, x => x.BookedCount + 1), ct);
            return updated > 0;
        }

        var slot = await db.PickupSlots.FirstAsync(s => s.Id == slotId, ct);
        if (slot.BookedCount >= slot.Capacity)
        {
            return false;
        }

        slot.BookedCount += 1;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ReleaseSlotAsync(Guid branchId, DateTime slotStart, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await db.PickupSlots
                .Where(s => s.BranchId == branchId && s.SlotStart == slotStart && s.BookedCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.BookedCount, x => x.BookedCount - 1), ct);
            return;
        }

        var slot = await db.PickupSlots.FirstOrDefaultAsync(s => s.BranchId == branchId && s.SlotStart == slotStart, ct);
        if (slot is not null && slot.BookedCount > 0)
        {
            slot.BookedCount -= 1;
        }
    }

    private async Task<PickupSlotEntity> EnsureSlotAsync(Guid branchId, DateTime slotStart, int capacity, CancellationToken ct)
    {
        var existing = await db.PickupSlots.FirstOrDefaultAsync(s => s.BranchId == branchId && s.SlotStart == slotStart, ct);
        if (existing is not null)
        {
            return existing;
        }

        var slot = new PickupSlotEntity
        {
            BranchId = branchId,
            SlotStart = slotStart,
            Capacity = capacity,
            BookedCount = 0
        };
        db.PickupSlots.Add(slot);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(slot).State = EntityState.Detached;
            existing = await db.PickupSlots.FirstAsync(s => s.BranchId == branchId && s.SlotStart == slotStart, ct);
            return existing;
        }

        return slot;
    }

    private static void ValidateSlotGrid(BranchPickupSettingsEntity settings, DateTime slotStart)
    {
        var date = DateOnly.FromDateTime(slotStart);
        var open = date.ToDateTime(settings.OpenTime, DateTimeKind.Utc);
        var close = date.ToDateTime(settings.CloseTime, DateTimeKind.Utc);
        if (slotStart < open || slotStart >= close)
        {
            throw new BusinessRuleException("SLOT_CUTOFF", "Khung giờ nằm ngoài giờ mở cửa.");
        }

        var minutes = (int)(slotStart - open).TotalMinutes;
        if (minutes % settings.SlotMinutes != 0)
        {
            throw new BusinessRuleException("SLOT_CUTOFF", "Khung giờ không khớp lưới slot.");
        }
    }

    private async Task<BranchPickupSettingsEntity> GetOrCreateSettingsAsync(Guid branchId, CancellationToken ct)
    {
        var settings = await db.BranchPickupSettings.FirstOrDefaultAsync(s => s.BranchId == branchId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new BranchPickupSettingsEntity { BranchId = branchId };
        db.BranchPickupSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private async Task<bool> StaffCanAccessBranchAsync(Guid userId, Guid branchId, CancellationToken ct)
    {
        return await (
            from sa in db.StaffAssignments
            join tu in db.TenantUsers on sa.TenantUserId equals tu.Id
            where tu.UserId == userId && sa.BranchId == branchId && sa.Status == "ACTIVE" && tu.Status == "ACTIVE"
            select sa).AnyAsync(ct);
    }

    private async Task<string> GeneratePickupCodeAsync(Guid branchId, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var code = RandomNumberGenerator.GetInt32(1000, 1_000_000).ToString();
            var taken = await db.Orders.AnyAsync(
                o => o.BranchId == branchId &&
                     o.PickupCode == code &&
                     o.Status != TakeawayOrderStatuses.Cancelled &&
                     o.Status != TakeawayOrderStatuses.PickedUp &&
                     o.Status != TakeawayOrderStatuses.NoShow,
                ct);
            if (!taken)
            {
                return code;
            }
        }

        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    }

    private async Task<bool> TryInsertWebhookEventAsync(PaymentWebhookRequest request, CancellationToken ct)
    {
        db.PaymentWebhookEvents.Add(new PaymentWebhookEventEntity
        {
            Provider = request.Provider,
            EventId = request.EventId,
            PayloadJson = request.PayloadJson
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private void AddHistory(Guid orderId, Guid? actor, string? from, string to, string? note)
    {
        db.OrderStatusHistory.Add(new OrderStatusHistoryEntity
        {
            OrderId = orderId,
            ActorUserId = actor,
            FromStatus = from,
            ToStatus = to,
            Note = note
        });
    }

    private static PickupOrderCreatedDto ToCreatedDto(OrderEntity order) =>
        new(order.Id, order.OrderCode, order.FinalAmount, order.PaymentDeadline, order.Status);

    private static bool TryParseCursor(string cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        var parts = cursor.Split('|');
        return parts.Length == 2 && DateTime.TryParse(parts[0], out createdAt) && Guid.TryParse(parts[1], out id);
    }

    private sealed record PricedOption(Guid OptionId, string OptionName, decimal ExtraPrice);

    private sealed record PricedItem(
        Guid ProductId,
        Guid VariantId,
        string ProductName,
        string VariantName,
        int Quantity,
        decimal UnitPrice,
        decimal LineTotal,
        string? Note,
        List<PricedOption> Options);
}

public sealed record PickupSlotDto(DateTime SlotStart, int Capacity, int BookedCount, int Remaining, bool Expired);

public sealed record CreatePickupOrderRequest(Guid BranchId, DateTime SlotStart, Guid? RedemptionId, List<CreatePickupItemRequest> Items);

public sealed record CreatePickupItemRequest(Guid VariantId, int Quantity, List<Guid>? OptionIds, string? Note);

public sealed record PickupOrderCreatedDto(Guid OrderId, string OrderCode, decimal TotalAmount, DateTime? PaymentDeadline, string Status);

public sealed record PickupPaymentDto(Guid PaymentId, string TransactionRef, decimal Amount, string PaymentUrl, string QrPayload);

public sealed record PaymentWebhookRequest(string Provider, string EventId, string TransactionRef, decimal Amount, string? PayloadJson);

public sealed record OrderListItemDto(Guid Id, string OrderCode, string Status, decimal TotalAmount, DateTime? PickupTime, DateTime CreatedAt);

public sealed record OrderItemOptionDto(string OptionName, decimal ExtraPrice);

public sealed record PickupOrderLineDto(Guid Id, string ProductName, string? VariantName, int Quantity, decimal UnitPrice, decimal Subtotal, string? Note, IReadOnlyList<OrderItemOptionDto> Options);

public sealed record OrderStatusHistoryDto(string? FromStatus, string ToStatus, string? Note, DateTime CreatedAt);

public sealed record OrderDetailDto(
    Guid Id,
    string OrderCode,
    string Status,
    decimal TotalAmount,
    DateTime? PickupTime,
    string? PickupCode,
    DateTime? PaymentDeadline,
    DateTime CreatedAt,
    IReadOnlyList<PickupOrderLineDto> Items,
    IReadOnlyList<OrderStatusHistoryDto> History);

public sealed record StaffStatusRequest(string To, string? PickupCode);

public sealed record FeedbackRequest(int Rating, string? Comment);

public sealed record CancelOrderRequest(string? Reason);
