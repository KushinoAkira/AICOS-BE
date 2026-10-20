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

    public DbSet<ReplacementShift> ReplacementShifts => Set<ReplacementShift>();

    public DbSet<ReplacementApplication> ReplacementApplications => Set<ReplacementApplication>();

    public DbSet<PayRule> PayRules => Set<PayRule>();

    public DbSet<CashClosing> CashClosings => Set<CashClosing>();

    public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("employees");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.FullName).HasMaxLength(255).IsRequired();
        builder.Property(e => e.Role).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Position).HasMaxLength(50);
        builder.Property(e => e.ContractType).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(e => e.UserId).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.BranchId });
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
        builder.HasIndex(s => s.TenantId);
    }
}

internal sealed class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> builder)
    {
        builder.ToTable("employee_shifts");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.ReviewStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.DeclineReason).HasMaxLength(500);
        builder.Property(a => a.ReviewNote).HasMaxLength(500);
        builder.Ignore(a => a.Hours);
        builder.Ignore(a => a.EffectiveCheckInUtc);
        builder.Ignore(a => a.EffectiveCheckOutUtc);
        builder.Ignore(a => a.WorkedHours);
        builder.Ignore(a => a.CountsForPayroll);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkShift>().WithMany().HasForeignKey(a => a.WorkShiftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.WorkDate });
        builder.HasIndex(a => new { a.BranchId, a.WorkDate });
        builder.HasIndex(a => a.TenantId);
    }
}

internal sealed class ReplacementShiftConfiguration : IEntityTypeConfiguration<ReplacementShift>
{
    public void Configure(EntityTypeBuilder<ReplacementShift> builder)
    {
        builder.ToTable("replacement_shifts");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Reason).HasMaxLength(500).IsRequired();
        builder.Property(r => r.ManagerNote).HasMaxLength(500);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Ignore(r => r.Hours);
        builder.HasIndex(r => new { r.BranchId, r.Status });
        builder.HasIndex(r => r.TenantId);
    }
}

internal sealed class ReplacementApplicationConfiguration : IEntityTypeConfiguration<ReplacementApplication>
{
    public void Configure(EntityTypeBuilder<ReplacementApplication> builder)
    {
        builder.ToTable("replacement_applications");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Note).HasMaxLength(500);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<ReplacementShift>().WithMany().HasForeignKey(a => a.ReplacementShiftId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.ReplacementShiftId, a.EmployeeId });
    }
}

internal sealed class PayRuleConfiguration : IEntityTypeConfiguration<PayRule>
{
    public void Configure(EntityTypeBuilder<PayRule> builder)
    {
        builder.ToTable("pay_rules");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.FullTimeDayRate).HasPrecision(14, 2);
        builder.Property(r => r.PartTimeHourRate).HasPrecision(14, 2);
        builder.Property(r => r.ReplacementHourRate).HasPrecision(14, 2);
        builder.Property(r => r.OvertimeAfterHours).HasPrecision(5, 2);
        builder.Property(r => r.OvertimeMultiplier).HasPrecision(5, 2);
        builder.HasIndex(r => r.TenantId).IsUnique();
    }
}

internal sealed class CashClosingConfiguration : IEntityTypeConfiguration<CashClosing>
{
    public void Configure(EntityTypeBuilder<CashClosing> builder)
    {
        builder.ToTable("cash_closings");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.ExpectedAmount).HasPrecision(14, 2);
        builder.Property(c => c.CountedAmount).HasPrecision(14, 2);
        builder.Property(c => c.Difference).HasPrecision(14, 2);
        builder.Property(c => c.CashierNote).HasMaxLength(500);
        builder.Property(c => c.ManagerNote).HasMaxLength(500);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<ShiftAssignment>().WithMany().HasForeignKey(c => c.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.AssignmentId).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.BranchId, c.Status });
    }
}

internal sealed class CashTransactionConfiguration : IEntityTypeConfiguration<CashTransaction>
{
    public void Configure(EntityTypeBuilder<CashTransaction> builder)
    {
        builder.ToTable("cash_transactions");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Amount).HasPrecision(14, 2);
        builder.HasIndex(t => t.SourceEventId).IsUnique();
        builder.HasIndex(t => new { t.CashierUserId, t.OccurredAtUtc });
    }
}
