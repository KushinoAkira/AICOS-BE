using AICOS.BuildingBlocks.Persistence;
using AICOS.Ordering.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AICOS.Ordering.Api.Persistence;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => "public";

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<CafeTableEntity> CafeTables => Set<CafeTableEntity>();
    public DbSet<TableSessionEntity> TableSessions => Set<TableSessionEntity>();
    public DbSet<TableSessionTableEntity> TableSessionTables => Set<TableSessionTableEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<ProductVariantEntity> ProductVariants => Set<ProductVariantEntity>();
    public DbSet<ProductOptionGroupEntity> ProductOptionGroups => Set<ProductOptionGroupEntity>();
    public DbSet<ProductOptionEntity> ProductOptions => Set<ProductOptionEntity>();
    public DbSet<BranchProductEntity> BranchProducts => Set<BranchProductEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();
    public DbSet<OrderItemOptionEntity> OrderItemOptions => Set<OrderItemOptionEntity>();
    public DbSet<BillEntity> Bills => Set<BillEntity>();
    public DbSet<BillItemEntity> BillItems => Set<BillItemEntity>();
    public DbSet<LoyaltyAccountEntity> LoyaltyAccounts => Set<LoyaltyAccountEntity>();
    public DbSet<UserPreferenceEntity> UserPreferences => Set<UserPreferenceEntity>();
    public DbSet<BranchPickupSettingsEntity> BranchPickupSettings => Set<BranchPickupSettingsEntity>();
    public DbSet<PickupSlotEntity> PickupSlots => Set<PickupSlotEntity>();
    public DbSet<OrderStatusHistoryEntity> OrderStatusHistory => Set<OrderStatusHistoryEntity>();
    public DbSet<PaymentEntity> Payments => Set<PaymentEntity>();
    public DbSet<RefundEntity> Refunds => Set<RefundEntity>();
    public DbSet<PaymentWebhookEventEntity> PaymentWebhookEvents => Set<PaymentWebhookEventEntity>();
    public DbSet<TenantUserEntity> TenantUsers => Set<TenantUserEntity>();
    public DbSet<StaffAssignmentEntity> StaffAssignments => Set<StaffAssignmentEntity>();
    public DbSet<LoyaltyTransactionEntity> LoyaltyTransactions => Set<LoyaltyTransactionEntity>();
    public DbSet<RewardEntity> Rewards => Set<RewardEntity>();
    public DbSet<RewardRedemptionEntity> RewardRedemptions => Set<RewardRedemptionEntity>();
    public DbSet<FeedbackEntity> Feedbacks => Set<FeedbackEntity>();
    public DbSet<FeedbackAnalysisEntity> FeedbackAnalyses => Set<FeedbackAnalysisEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<PushTokenEntity> PushTokens => Set<PushTokenEntity>();
    public DbSet<DataDeletionRequestEntity> DataDeletionRequests => Set<DataDeletionRequestEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Explicit schema and table mapping to match PostgreSQL script exactly
        modelBuilder.Entity<TenantEntity>().ToTable("tenants", "public");
        modelBuilder.Entity<BranchEntity>().ToTable("branches", "public");
        modelBuilder.Entity<UserEntity>().ToTable("users", "public");
        modelBuilder.Entity<CafeTableEntity>().ToTable("cafe_tables", "public");
        modelBuilder.Entity<TableSessionEntity>().ToTable("table_sessions", "public");
        modelBuilder.Entity<TableSessionTableEntity>().ToTable("table_session_tables", "public");
        modelBuilder.Entity<CategoryEntity>().ToTable("categories", "public");
        modelBuilder.Entity<ProductEntity>().ToTable("products", "public");
        modelBuilder.Entity<ProductVariantEntity>().ToTable("product_variants", "public");
        modelBuilder.Entity<ProductOptionGroupEntity>().ToTable("product_option_groups", "public");
        modelBuilder.Entity<ProductOptionEntity>().ToTable("product_options", "public");
        modelBuilder.Entity<BranchProductEntity>().ToTable("branch_products", "public");
        modelBuilder.Entity<OrderEntity>().ToTable("orders", "public");
        modelBuilder.Entity<OrderItemEntity>().ToTable("order_items", "public");
        modelBuilder.Entity<OrderItemOptionEntity>().ToTable("order_item_options", "public");
        modelBuilder.Entity<BillEntity>().ToTable("bills", "public");
        modelBuilder.Entity<BillItemEntity>().ToTable("bill_items", "public");
        modelBuilder.Entity<LoyaltyAccountEntity>().ToTable("loyalty_accounts", "public");
        modelBuilder.Entity<UserPreferenceEntity>().ToTable("user_preferences", "public");
        modelBuilder.Entity<BranchPickupSettingsEntity>().ToTable("branch_pickup_settings", "public");
        modelBuilder.Entity<PickupSlotEntity>().ToTable("pickup_slots", "public");
        modelBuilder.Entity<OrderStatusHistoryEntity>().ToTable("order_status_history", "public");
        modelBuilder.Entity<PaymentEntity>().ToTable("payments", "public");
        modelBuilder.Entity<RefundEntity>().ToTable("refunds", "public");
        modelBuilder.Entity<PaymentWebhookEventEntity>().ToTable("payment_webhook_events", "public");
        modelBuilder.Entity<TenantUserEntity>().ToTable("tenant_users", "public");
        modelBuilder.Entity<StaffAssignmentEntity>().ToTable("staff_assignments", "public");
        modelBuilder.Entity<LoyaltyTransactionEntity>().ToTable("loyalty_transactions", "public");
        modelBuilder.Entity<RewardEntity>().ToTable("rewards", "public");
        modelBuilder.Entity<RewardRedemptionEntity>().ToTable("reward_redemptions", "public");
        modelBuilder.Entity<FeedbackEntity>().ToTable("feedbacks", "public");
        modelBuilder.Entity<FeedbackAnalysisEntity>().ToTable("feedback_analyses", "public");
        modelBuilder.Entity<NotificationEntity>().ToTable("notifications", "public");
        modelBuilder.Entity<PushTokenEntity>().ToTable("push_tokens", "public");
        modelBuilder.Entity<DataDeletionRequestEntity>().ToTable("data_deletion_requests", "public");

        modelBuilder.Entity<PickupSlotEntity>().HasIndex(s => new { s.BranchId, s.SlotStart }).IsUnique();
        modelBuilder.Entity<PaymentWebhookEventEntity>().HasIndex(e => new { e.Provider, e.EventId }).IsUnique();
        modelBuilder.Entity<FeedbackEntity>().HasIndex(f => f.OrderId).IsUnique();
        modelBuilder.Entity<PushTokenEntity>().HasIndex(t => t.Token).IsUnique();
        modelBuilder.Entity<OrderEntity>().HasIndex(o => new { o.UserId, o.CreatedAt });
        modelBuilder.Entity<NotificationEntity>().HasIndex(n => new { n.UserId, n.CreatedAt });
    }
}
