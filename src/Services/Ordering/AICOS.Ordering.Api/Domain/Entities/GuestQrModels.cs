namespace AICOS.Ordering.Api.Domain.Entities;

public class TableArea
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class CafeTable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public string TableNumber { get; set; } = string.Empty;
    public int Capacity { get; set; } = 2;
    public string Status { get; set; } = "AVAILABLE"; // AVAILABLE, OCCUPIED, RESERVED
    public string QrToken { get; set; } = string.Empty;
}

public class TableSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public string Status { get; set; } = "OPEN"; // OPEN, CLOSED
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

public class TableSessionTable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid TableId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LeftAt { get; set; }
}

public class Category
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ProductVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string SizeName { get; set; } = "Size M"; // Size S, Size M, Size L
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProductOptionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public string Name { get; set; } = string.Empty; // "Mức đường", "Mức đá", "Topping"
    public int MinSelect { get; set; } = 0;
    public int MaxSelect { get; set; } = 1;
}

public class ProductOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public string Name { get; set; } = string.Empty; // "100% đường", "Ít đá", "Trân châu hoàng kim"
    public decimal ExtraPrice { get; set; } = 0;
    public bool IsActive { get; set; } = true;
}

public class BranchProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Guid ProductId { get; set; }
    public bool IsAvailable { get; set; } = true;
}

public class OrderItemOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderItemId { get; set; }
    public Guid? OptionId { get; set; }
    public string OptionName { get; set; } = string.Empty;
    public decimal ExtraPrice { get; set; } = 0;
}

public class OrderItemEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderItemId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SessionId { get; set; }
    public Guid? OrderId { get; set; }
    public string BillCode { get; set; } = string.Empty;
    public string Status { get; set; } = "UNPAID"; // UNPAID, PAID
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class BillItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BillId { get; set; }
    public Guid OrderItemId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}

public class LoyaltyAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public int PointsBalance { get; set; } = 0;
    public string Tier { get; set; } = "STANDARD";
}

public class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Status { get; set; } = "ACTIVE";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
