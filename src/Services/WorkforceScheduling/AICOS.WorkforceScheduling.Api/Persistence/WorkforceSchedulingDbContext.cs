using AICOS.BuildingBlocks.Persistence;
using AICOS.WorkforceScheduling.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AICOS.WorkforceScheduling.Api.Persistence;

public sealed class WorkforceSchedulingDbContext(DbContextOptions<WorkforceSchedulingDbContext> options) : AicosDbContext(options)
{
    protected override string Schema => WorkforceSchedulingService.Info.Schema;

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<WorkShift> WorkShifts => Set<WorkShift>();

    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();

    public DbSet<ShiftSwapRequest> ShiftSwapRequests => Set<ShiftSwapRequest>();
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.FullName).HasMaxLength(255).IsRequired();
        builder.Property(e => e.Position).HasMaxLength(50);
        builder.Property(e => e.PaymentType).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.HourlyRate).HasPrecision(12, 2);
        builder.Property(e => e.FixedSalary).HasPrecision(14, 2);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(e => e.UserId).IsUnique();
        builder.HasIndex(e => e.BranchId);
    }
}

internal sealed class WorkShiftConfiguration : IEntityTypeConfiguration<WorkShift>
{
    public void Configure(EntityTypeBuilder<WorkShift> builder)
    {
        builder.ToTable("work_shifts");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(50).IsRequired();
        builder.Ignore(s => s.Hours);
        builder.HasIndex(s => new { s.BranchId, s.Name }).IsUnique();
    }
}

internal sealed class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> builder)
    {
        builder.ToTable("employee_shifts");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.DeclineReason).HasMaxLength(500);
        builder.Ignore(a => a.Hours);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkShift>().WithMany().HasForeignKey(a => a.WorkShiftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.WorkDate });
        builder.HasIndex(a => new { a.BranchId, a.WorkDate });
    }
}

internal sealed class ShiftSwapRequestConfiguration : IEntityTypeConfiguration<ShiftSwapRequest>
{
    public void Configure(EntityTypeBuilder<ShiftSwapRequest> builder)
    {
        builder.ToTable("shift_swap_requests");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Reason).HasMaxLength(500).IsRequired();
        builder.Property(s => s.ManagerNote).HasMaxLength(500);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<ShiftAssignment>().WithMany().HasForeignKey(s => s.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.BranchId, s.Status });
    }
}
