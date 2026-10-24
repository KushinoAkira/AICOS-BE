using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AICOS.Ordering.Api.Persistence.Entities;

[Table("tenants")]
public class TenantEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("code")]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    public List<BranchEntity> Branches { get; set; } = new();
}

[Table("branches")]
public class BranchEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("code")]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Column("name")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("address")]
    public string? Address { get; set; }

    [Column("phone")]
    [MaxLength(20)]
    public string? Phone { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";

    [Column("latitude")]
    public double? Latitude { get; set; }

    [Column("longitude")]
    public double? Longitude { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(TenantId))]
    public TenantEntity? Tenant { get; set; }

    public List<CafeTableEntity> Tables { get; set; } = new();
}

[Table("users")]
public class UserEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("email")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Column("password_hash")]
    public string? PasswordHash { get; set; }

    [Column("full_name")]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Column("phone")]
    [MaxLength(20)]
    public string? Phone { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("cafe_tables")]
public class CafeTableEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("qr_code_hash")]
    [MaxLength(255)]
    public string? QrCodeHash { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "AVAILABLE";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(BranchId))]
    public BranchEntity? Branch { get; set; }
}

[Table("table_sessions")]
public class TableSessionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "OPEN";

    [Column("opened_at")]
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    [Column("closed_at")]
    public DateTime? ClosedAt { get; set; }

    [ForeignKey(nameof(BranchId))]
    public BranchEntity? Branch { get; set; }

    public List<TableSessionTableEntity> SessionTables { get; set; } = new();
}

[Table("table_session_tables")]
public class TableSessionTableEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("table_session_id")]
    public Guid TableSessionId { get; set; }

    [Column("table_id")]
    public Guid TableId { get; set; }

    [ForeignKey(nameof(TableSessionId))]
    public TableSessionEntity? Session { get; set; }

    [ForeignKey(nameof(TableId))]
    public CafeTableEntity? Table { get; set; }
}

[Table("categories")]
public class CategoryEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ProductEntity> Products { get; set; } = new();
}

[Table("products")]
public class ProductEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("category_id")]
    public Guid CategoryId { get; set; }

    [Column("name")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("base_price")]
    public decimal BasePrice { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(CategoryId))]
    public CategoryEntity? Category { get; set; }

    public List<ProductVariantEntity> Variants { get; set; } = new();
    public List<ProductOptionGroupEntity> OptionGroups { get; set; } = new();
}

[Table("product_variants")]
public class ProductVariantEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("product_id")]
    public Guid ProductId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("price_adjustment")]
    public decimal PriceAdjustment { get; set; } = 0;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }
}

[Table("product_option_groups")]
public class ProductOptionGroupEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("product_id")]
    public Guid ProductId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("is_required")]
    public bool IsRequired { get; set; } = false;

    [Column("min_select")]
    public int MinSelect { get; set; } = 0;

    [Column("max_selection")]
    public int MaxSelection { get; set; } = 1;

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }

    public List<ProductOptionEntity> Options { get; set; } = new();
}

[Table("product_options")]
public class ProductOptionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("group_id")]
    public Guid GroupId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("price_adjustment")]
    public decimal PriceAdjustment { get; set; } = 0;

    [ForeignKey(nameof(GroupId))]
    public ProductOptionGroupEntity? Group { get; set; }
}

[Table("branch_products")]
public class BranchProductEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("product_id")]
    public Guid ProductId { get; set; }

    [Column("is_available")]
    public bool IsAvailable { get; set; } = true;

    [ForeignKey(nameof(BranchId))]
    public BranchEntity? Branch { get; set; }

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }
}

[Table("orders")]
public class OrderEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("order_code")]
    [MaxLength(50)]
    public string OrderCode { get; set; } = string.Empty;

    [Column("table_session_id")]
    public Guid? TableSessionId { get; set; }

    [Column("order_type")]
    [MaxLength(30)]
    public string OrderType { get; set; } = "DINE_IN";

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "SUBMITTED";

    [Column("total_amount")]
    public decimal TotalAmount { get; set; } = 0;

    [Column("discount_amount")]
    public decimal DiscountAmount { get; set; } = 0;

    [Column("final_amount")]
    public decimal FinalAmount { get; set; } = 0;

    [Column("loyalty_points_earned")]
    public int LoyaltyPointsEarned { get; set; } = 0;

    [Column("loyalty_points_redeemed")]
    public int LoyaltyPointsRedeemed { get; set; } = 0;

    [Column("payment_status")]
    [MaxLength(30)]
    public string PaymentStatus { get; set; } = "UNPAID";

    [Column("service_type")]
    [MaxLength(30)]
    public string ServiceType { get; set; } = "DINE_IN";

    [Column("customer_phone")]
    [MaxLength(20)]
    public string? CustomerPhone { get; set; }

    [Column("is_takeaway")]
    public bool IsTakeaway { get; set; } = false;

    [Column("scheduled_pickup_time")]
    public DateTime? ScheduledPickupTime { get; set; }

    [Column("pickup_time")]
    public DateTime? PickupTime { get; set; }

    [Column("payment_deadline")]
    public DateTime? PaymentDeadline { get; set; }

    [Column("pickup_code")]
    [MaxLength(10)]
    public string? PickupCode { get; set; }

    [Column("idempotency_key")]
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }

    [Column("redemption_id")]
    public Guid? RedemptionId { get; set; }

    [Column("cancel_reason")]
    [MaxLength(50)]
    public string? CancelReason { get; set; }

    [Column("cancelled_at")]
    public DateTime? CancelledAt { get; set; }

    [Column("ready_at")]
    public DateTime? ReadyAt { get; set; }

    [Column("picked_up_at")]
    public DateTime? PickedUpAt { get; set; }

    [Column("subtotal")]
    public decimal Subtotal { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(BranchId))]
    public BranchEntity? Branch { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserEntity? User { get; set; }

    public List<OrderItemEntity> Items { get; set; } = new();
    public BillEntity? Bill { get; set; }
}

[Table("order_items")]
public class OrderItemEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("product_id")]
    public Guid ProductId { get; set; }

    [Column("variant_id")]
    public Guid? VariantId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("unit_price")]
    public decimal UnitPrice { get; set; }

    [Column("subtotal")]
    public decimal Subtotal { get; set; }

    [Column("product_name")]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [Column("variant_name")]
    [MaxLength(100)]
    public string? VariantName { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("preparation_status")]
    [MaxLength(30)]
    public string PreparationStatus { get; set; } = "PENDING";

    [ForeignKey(nameof(OrderId))]
    public OrderEntity? Order { get; set; }

    [ForeignKey(nameof(ProductId))]
    public ProductEntity? Product { get; set; }

    public List<OrderItemOptionEntity> Options { get; set; } = new();
}

[Table("order_item_options")]
public class OrderItemOptionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("order_item_id")]
    public Guid OrderItemId { get; set; }

    [Column("option_id")]
    public Guid OptionId { get; set; }

    [Column("option_name")]
    [MaxLength(100)]
    public string OptionName { get; set; } = string.Empty;

    [Column("additional_price")]
    public decimal AdditionalPrice { get; set; } = 0;

    [Column("extra_price")]
    public decimal ExtraPrice { get; set; } = 0;

    [ForeignKey(nameof(OrderItemId))]
    public OrderItemEntity? OrderItem { get; set; }

    [ForeignKey(nameof(OptionId))]
    public ProductOptionEntity? Option { get; set; }
}

[Table("bills")]
public class BillEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("bill_code")]
    [MaxLength(50)]
    public string BillCode { get; set; } = string.Empty;

    [Column("total_amount")]
    public decimal TotalAmount { get; set; }

    [Column("discount_amount")]
    public decimal DiscountAmount { get; set; } = 0;

    [Column("final_amount")]
    public decimal FinalAmount { get; set; }

    [Column("payment_method")]
    [MaxLength(30)]
    public string PaymentMethod { get; set; } = "CASH";

    [Column("payment_time")]
    public DateTime? PaymentTime { get; set; } = DateTime.UtcNow;

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "PAID";

    [ForeignKey(nameof(OrderId))]
    public OrderEntity? Order { get; set; }

    public List<BillItemEntity> Items { get; set; } = new();
}

[Table("bill_items")]
public class BillItemEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("bill_id")]
    public Guid BillId { get; set; }

    [Column("product_name")]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [Column("variant_name")]
    [MaxLength(100)]
    public string? VariantName { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("unit_price")]
    public decimal UnitPrice { get; set; }

    [Column("subtotal")]
    public decimal Subtotal { get; set; }

    [ForeignKey(nameof(BillId))]
    public BillEntity? Bill { get; set; }
}

[Table("loyalty_accounts")]
public class LoyaltyAccountEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("total_points")]
    public int TotalPoints { get; set; } = 0;

    [Column("current_tier")]
    [MaxLength(50)]
    public string CurrentTier { get; set; } = "BRONZE";

    [Column("total_spending")]
    public decimal TotalSpending { get; set; } = 0;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(TenantId))]
    public TenantEntity? Tenant { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserEntity? User { get; set; }
}

[Table("user_preferences")]
public class UserPreferenceEntity
{
    [Key]
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("preferred_branch_id")]
    public Guid? PreferredBranchId { get; set; }

    [Column("notify_push")]
    public bool NotifyPush { get; set; } = true;

    [Column("extra")]
    public string ExtraJson { get; set; } = "{}";

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("branch_pickup_settings")]
public class BranchPickupSettingsEntity
{
    [Key]
    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("open_time")]
    public TimeOnly OpenTime { get; set; } = new(7, 0);

    [Column("close_time")]
    public TimeOnly CloseTime { get; set; } = new(22, 0);

    [Column("slot_minutes")]
    public int SlotMinutes { get; set; } = 15;

    [Column("capacity_per_slot")]
    public int CapacityPerSlot { get; set; } = 5;

    [Column("cutoff_minutes")]
    public int CutoffMinutes { get; set; } = 30;

    [Column("hold_minutes")]
    public int HoldMinutes { get; set; } = 15;

    [Column("lock_minutes")]
    public int LockMinutes { get; set; } = 60;
}

[Table("pickup_slots")]
public class PickupSlotEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("slot_start")]
    public DateTime SlotStart { get; set; }

    [Column("capacity")]
    public int Capacity { get; set; }

    [Column("booked_count")]
    public int BookedCount { get; set; }
}

[Table("order_status_history")]
public class OrderStatusHistoryEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("actor_user_id")]
    public Guid? ActorUserId { get; set; }

    [Column("from_status")]
    [MaxLength(30)]
    public string? FromStatus { get; set; }

    [Column("to_status")]
    [MaxLength(30)]
    public string ToStatus { get; set; } = string.Empty;

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("payments")]
public class PaymentEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("bill_id")]
    public Guid BillId { get; set; }

    [Column("method")]
    [MaxLength(30)]
    public string Method { get; set; } = "BANK_TRANSFER";

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "PENDING";

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("transaction_ref")]
    [MaxLength(255)]
    public string TransactionRef { get; set; } = string.Empty;

    [Column("paid_at")]
    public DateTime? PaidAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("refunds")]
public class RefundEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("payment_id")]
    public Guid PaymentId { get; set; }

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "PENDING";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("payment_webhook_events")]
public class PaymentWebhookEventEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("provider")]
    [MaxLength(50)]
    public string Provider { get; set; } = string.Empty;

    [Column("event_id")]
    [MaxLength(255)]
    public string EventId { get; set; } = string.Empty;

    [Column("payload")]
    public string? PayloadJson { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

[Table("tenant_users")]
public class TenantUserEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("role_id")]
    public Guid RoleId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";
}

[Table("staff_assignments")]
public class StaffAssignmentEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("branch_id")]
    public Guid BranchId { get; set; }

    [Column("tenant_user_id")]
    public Guid TenantUserId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";
}

[Table("loyalty_transactions")]
public class LoyaltyTransactionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Column("order_id")]
    public Guid? OrderId { get; set; }

    [Column("points_change")]
    public int PointsChange { get; set; }

    [Column("type")]
    [MaxLength(30)]
    public string Type { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("rewards")]
public class RewardEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("name")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("required_points")]
    public int RequiredPoints { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

[Table("reward_redemptions")]
public class RewardRedemptionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("reward_id")]
    public Guid RewardId { get; set; }

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Column("code")]
    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("redeemed_at")]
    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "REDEEMED";
}

[Table("feedbacks")]
public class FeedbackEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

    [Column("rating")]
    public int Rating { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("feedback_analyses")]
public class FeedbackAnalysisEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("feedback_id")]
    public Guid FeedbackId { get; set; }

    [Column("sentiment")]
    [MaxLength(30)]
    public string? Sentiment { get; set; }

    [Column("confidence")]
    public decimal? Confidence { get; set; }

    [Column("analyzed_at")]
    public DateTime? AnalyzedAt { get; set; }
}

[Table("notifications")]
public class NotificationEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("type")]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    [Column("title")]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Column("body")]
    public string Body { get; set; } = string.Empty;

    [Column("data_json")]
    public string? DataJson { get; set; }

    [Column("read_at")]
    public DateTime? ReadAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("push_tokens")]
public class PushTokenEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("token")]
    [MaxLength(512)]
    public string Token { get; set; } = string.Empty;

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("data_deletion_requests")]
public class DataDeletionRequestEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "PENDING";

    [Column("scheduled_at")]
    public DateTime ScheduledAt { get; set; }

    [Column("processed_at")]
    public DateTime? ProcessedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}