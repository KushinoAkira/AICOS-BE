namespace AICOS.BuildingBlocks.Domain;

public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    public bool Equals(Entity<TId>? other) =>
        other is not null
        && GetType() == other.GetType()
        && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}

/// <summary>Filled automatically by <c>AuditableEntityInterceptor</c> on SaveChanges. Never set manually.</summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    Guid? CreatedBy { get; set; }
    DateTime? UpdatedAtUtc { get; set; }
    Guid? UpdatedBy { get; set; }
}

/// <summary>Marker for data owned by a single branch. Use it to apply branch-level query filters / authorization.</summary>
public interface IBranchScoped
{
    Guid BranchId { get; }
}

public abstract class AuditableEntity<TId> : Entity<TId>, IAuditable
    where TId : notnull
{
    public DateTime CreatedAtUtc { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}
