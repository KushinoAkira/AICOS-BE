using AICOS.BuildingBlocks.Exceptions;
using AICOS.BuildingBlocks.Security;
using AICOS.Ordering.Api.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace AICOS.Ordering.Api.Services.Takeaway;

public sealed class CustomerProfileService(OrderingDbContext db)
{
    public async Task<MeProfileDto> GetMeAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User", userId);
        var loyalty = await db.LoyaltyAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == tenantId, ct);
        var prefs = await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return new MeProfileDto(
            user.Id,
            user.Email,
            user.FullName,
            user.Phone,
            loyalty?.TotalPoints ?? 0,
            loyalty?.CurrentTier ?? "STANDARD",
            prefs?.PreferredBranchId,
            prefs?.NotifyPush ?? true,
            ParseExtra(prefs?.ExtraJson));
    }

    public async Task<MeProfileDto> PatchMeAsync(Guid userId, Guid tenantId, PatchMeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > 200)
        {
            throw new BusinessRuleException("INVALID_PROFILE", "Họ tên không hợp lệ.");
        }

        if (request.Phone is { Length: > 20 })
        {
            throw new BusinessRuleException("INVALID_PROFILE", "Số điện thoại không hợp lệ.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User", userId);
        user.FullName = request.FullName.Trim();
        user.Phone = request.Phone;
        await db.SaveChangesAsync(ct);
        return await GetMeAsync(userId, tenantId, ct);
    }

    public async Task UpsertPreferencesAsync(Guid userId, UpsertPreferencesRequest request, CancellationToken ct)
    {
        var extra = request.Extra is null ? "{}" : JsonSerializer.Serialize(request.Extra);
        var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            db.UserPreferences.Add(new UserPreferenceEntity
            {
                UserId = userId,
                NotifyPush = request.NotifyPush ?? true,
                ExtraJson = extra,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            prefs.NotifyPush = request.NotifyPush ?? prefs.NotifyPush;
            prefs.ExtraJson = extra;
            prefs.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BranchListDto>> ListActiveBranchesAsync(Guid tenantId, CancellationToken ct)
    {
        return await db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Status == "ACTIVE")
            .Select(b => new BranchListDto(b.Id, b.Code, b.Name, b.Address, b.Phone, b.Latitude, b.Longitude))
            .ToListAsync(ct);
    }

    public async Task<BranchListDto> GetNearestBranchAsync(Guid tenantId, double lat, double lng, CancellationToken ct)
    {
        if (lat is < -90 or > 90 || lng is < -180 or > 180)
        {
            throw new BusinessRuleException("INVALID_COORDINATES", "Tọa độ ngoài miền hợp lệ.");
        }

        var branches = await db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Status == "ACTIVE" && b.Latitude != null && b.Longitude != null)
            .ToListAsync(ct);
        if (branches.Count == 0)
        {
            throw new NotFoundException("Branch", tenantId);
        }

        var nearest = branches
            .OrderBy(b => HaversineKm(lat, lng, b.Latitude!.Value, b.Longitude!.Value))
            .First();
        return new BranchListDto(nearest.Id, nearest.Code, nearest.Name, nearest.Address, nearest.Phone, nearest.Latitude, nearest.Longitude);
    }

    public async Task SetPreferredBranchAsync(Guid userId, Guid tenantId, Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId && b.TenantId == tenantId && b.Status == "ACTIVE", ct)
            ?? throw new NotFoundException("Branch", branchId);

        var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            db.UserPreferences.Add(new UserPreferenceEntity
            {
                UserId = userId,
                PreferredBranchId = branch.Id,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            prefs.PreferredBranchId = branch.Id;
            prefs.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<PointsDto> GetPointsAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var account = await db.LoyaltyAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == tenantId, ct);
        if (account is null)
        {
            return new PointsDto(0, "STANDARD", []);
        }

        var tx = await db.LoyaltyTransactions.AsNoTracking()
            .Where(t => t.AccountId == account.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .Select(t => new LoyaltyTxDto(t.Id, t.Type, t.PointsChange, t.OrderId, t.CreatedAt))
            .ToListAsync(ct);
        return new PointsDto(account.TotalPoints, account.CurrentTier, tx);
    }

    public async Task<RewardRedemptionEntity> RedeemAsync(Guid userId, Guid tenantId, Guid rewardId, CancellationToken ct)
    {
        var reward = await db.Rewards.FirstOrDefaultAsync(r => r.Id == rewardId && r.TenantId == tenantId && r.IsActive, ct)
            ?? throw new NotFoundException("Reward", rewardId);
        var account = await db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == tenantId, ct)
            ?? throw new ConflictException("Không đủ điểm.", "INSUFFICIENT_POINTS");

        if (db.Database.IsRelational())
        {
            var updated = await db.LoyaltyAccounts
                .Where(a => a.Id == account.Id && a.TotalPoints >= reward.RequiredPoints)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotalPoints, x => x.TotalPoints - reward.RequiredPoints), ct);
            if (updated == 0)
            {
                throw new ConflictException("Không đủ điểm.", "INSUFFICIENT_POINTS");
            }
        }
        else
        {
            if (account.TotalPoints < reward.RequiredPoints)
            {
                throw new ConflictException("Không đủ điểm.", "INSUFFICIENT_POINTS");
            }

            account.TotalPoints -= reward.RequiredPoints;
        }

        db.LoyaltyTransactions.Add(new LoyaltyTransactionEntity
        {
            AccountId = account.Id,
            PointsChange = -reward.RequiredPoints,
            Type = "REDEEM"
        });
        var redemption = new RewardRedemptionEntity
        {
            RewardId = reward.Id,
            AccountId = account.Id,
            Code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Status = "REDEEMED"
        };
        db.RewardRedemptions.Add(redemption);
        await db.SaveChangesAsync(ct);
        return redemption;
    }

    public async Task<(IReadOnlyList<NotificationEntity> Items, string? NextCursor)> ListNotificationsAsync(
        Guid userId, bool unreadOnly, string? cursor, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        if (!string.IsNullOrWhiteSpace(cursor) && DateTime.TryParse(cursor, out var createdAt))
        {
            query = query.Where(n => n.CreatedAt < createdAt);
        }

        var rows = await query.OrderByDescending(n => n.CreatedAt).Take(limit + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > limit)
        {
            next = rows[limit - 1].CreatedAt.ToString("O");
            rows = rows.Take(limit).ToList();
        }

        return (rows, next);
    }

    public async Task MarkNotificationReadAsync(Guid userId, Guid notificationId, CancellationToken ct)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId && x.UserId == userId, ct)
            ?? throw new NotFoundException("Notification", notificationId);
        n.ReadAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertPushTokenAsync(Guid userId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new BusinessRuleException("INVALID_TOKEN", "Token không hợp lệ.");
        }

        var existing = await db.PushTokens.FirstOrDefaultAsync(t => t.Token == token, ct);
        if (existing is null)
        {
            db.PushTokens.Add(new PushTokenEntity { UserId = userId, Token = token, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            existing.UserId = userId;
            existing.RevokedAt = null;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FeedbackEntity>> GetMyFeedbacksAsync(Guid userId, CancellationToken ct)
    {
        return await db.Feedbacks.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AdminFeedbackDto>> ListAdminFeedbacksAsync(
        Guid userId,
        string? role,
        Guid tenantId,
        Guid? branchId,
        int? rating,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        var query =
            from f in db.Feedbacks.AsNoTracking()
            join o in db.Orders.AsNoTracking() on f.OrderId equals o.Id
            join b in db.Branches.AsNoTracking() on o.BranchId equals b.Id
            where b.TenantId == tenantId
            select new { f, o, b };

        if (branchId is Guid bid)
        {
            query = query.Where(x => x.o.BranchId == bid);
        }

        if (rating is int r)
        {
            query = query.Where(x => x.f.Rating == r);
        }

        if (from is DateTime fromDt)
        {
            query = query.Where(x => x.f.CreatedAt >= fromDt);
        }

        if (to is DateTime toDt)
        {
            query = query.Where(x => x.f.CreatedAt <= toDt);
        }

        if (string.Equals(role, AicosRoles.Manager, StringComparison.OrdinalIgnoreCase))
        {
            var assigned = from sa in db.StaffAssignments
                           join tu in db.TenantUsers on sa.TenantUserId equals tu.Id
                           where tu.UserId == userId && sa.Status == "ACTIVE" && tu.Status == "ACTIVE"
                           select sa.BranchId;
            query = query.Where(x => assigned.Contains(x.o.BranchId));
        }

        return await query
            .OrderByDescending(x => x.f.CreatedAt)
            .Select(x => new AdminFeedbackDto(x.f.Id, x.f.OrderId, x.o.BranchId, x.f.Rating, x.f.Comment, x.f.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task CreateDeletionRequestAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User", userId);
        if (string.IsNullOrWhiteSpace(password) ||
            (!string.IsNullOrEmpty(user.PasswordHash) && !string.Equals(user.PasswordHash, password, StringComparison.Ordinal)))
        {
            throw new ForbiddenException("Mật khẩu không đúng.");
        }

        await EnsureNoOpenOrdersAsync(userId, ct);

        if (await db.DataDeletionRequests.AnyAsync(r => r.UserId == userId && r.Status == "PENDING", ct))
        {
            throw new ConflictException("Đã có yêu cầu xóa đang mở.", "DELETION_PENDING");
        }

        db.DataDeletionRequests.Add(new DataDeletionRequestEntity
        {
            UserId = userId,
            Status = "PENDING",
            ScheduledAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelDeletionRequestAsync(Guid userId, CancellationToken ct)
    {
        var request = await db.DataDeletionRequests.FirstOrDefaultAsync(r => r.UserId == userId && r.Status == "PENDING", ct)
            ?? throw new NotFoundException("DeletionRequest", userId);
        request.Status = "CANCELLED";
        await db.SaveChangesAsync(ct);
    }

    public async Task ProcessDueDeletionRequestsAsync(CancellationToken ct)
    {
        var due = await db.DataDeletionRequests
            .Where(r => r.Status == "PENDING" && r.ScheduledAt <= DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var request in due)
        {
            try
            {
                await EnsureNoOpenOrdersAsync(request.UserId, ct);
            }
            catch (ConflictException)
            {
                continue;
            }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
            if (user is not null)
            {
                user.Email = $"deleted+{user.Id:N}@anon.local";
                user.FullName = "Deleted User";
                user.Phone = null;
                user.PasswordHash = null;
                user.Status = "DELETED";
            }

            var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == request.UserId, ct);
            if (prefs is not null)
            {
                prefs.ExtraJson = "{}";
                prefs.PreferredBranchId = null;
            }

            var tokens = await db.PushTokens.Where(t => t.UserId == request.UserId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var token in tokens)
            {
                token.RevokedAt = DateTime.UtcNow;
            }

            request.Status = "COMPLETED";
            request.ProcessedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AnalyzePendingFeedbackAsync(CancellationToken ct)
    {
        var pending = await db.Feedbacks
            .Where(f => !db.FeedbackAnalyses.Any(a => a.FeedbackId == f.Id))
            .Take(50)
            .ToListAsync(ct);

        foreach (var feedback in pending)
        {
            var sentiment = feedback.Rating >= 4 ? "POSITIVE" : feedback.Rating <= 2 ? "NEGATIVE" : "NEUTRAL";
            db.FeedbackAnalyses.Add(new FeedbackAnalysisEntity
            {
                FeedbackId = feedback.Id,
                Sentiment = sentiment,
                Confidence = 0.5m,
                AnalyzedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNoOpenOrdersAsync(Guid userId, CancellationToken ct)
    {
        var openStatuses = new[]
        {
            TakeawayOrderStatuses.PendingPayment,
            TakeawayOrderStatuses.Paid,
            TakeawayOrderStatuses.Preparing,
            TakeawayOrderStatuses.Ready
        };
        if (await db.Orders.AnyAsync(o => o.UserId == userId && openStatuses.Contains(o.Status), ct))
        {
            throw new ConflictException("Còn đơn đang mở.", "HAS_OPEN_ORDERS");
        }

        if (await db.Refunds.AnyAsync(r => r.Status == "PENDING" &&
            db.Payments.Any(p => p.Id == r.PaymentId && db.Bills.Any(b => b.Id == p.BillId && db.Orders.Any(o => o.Id == b.OrderId && o.UserId == userId))), ct))
        {
            throw new ConflictException("Còn hoàn tiền đang xử lý.", "HAS_OPEN_ORDERS");
        }
    }

    private static object ParseExtra(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new { };
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return new { };
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

public sealed record MeProfileDto(
    Guid Id,
    string Email,
    string FullName,
    string? Phone,
    int PointsBalance,
    string Tier,
    Guid? PreferredBranchId,
    bool NotifyPush,
    object Extra);

public sealed record PatchMeRequest(string FullName, string? Phone);

public sealed record UpsertPreferencesRequest(bool? NotifyPush, Dictionary<string, object>? Extra);

public sealed record BranchListDto(Guid Id, string Code, string Name, string? Address, string? Phone, double? Latitude, double? Longitude);

public sealed record PreferredBranchRequest(Guid BranchId);

public sealed record PointsDto(int PointsBalance, string Tier, IReadOnlyList<LoyaltyTxDto> Recent);

public sealed record LoyaltyTxDto(Guid Id, string Type, int PointsChange, Guid? OrderId, DateTime CreatedAt);

public sealed record PushTokenRequest(string Token);

public sealed record DeletionRequestBody(string Password);

public sealed record AdminFeedbackDto(Guid Id, Guid OrderId, Guid BranchId, int Rating, string? Comment, DateTime CreatedAt);
