using AdRecon.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Api.Data;

public class AdReconDbContext(DbContextOptions<AdReconDbContext> options) : DbContext(options)
{
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<AdSlot> AdSlots => Set<AdSlot>();
    public DbSet<ContractSlot> ContractSlots => Set<ContractSlot>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ExposureFact> ExposureFacts => Set<ExposureFact>();
    public DbSet<DailyLedger> DailyLedgers => Set<DailyLedger>();
    public DbSet<Adjustment> Adjustments => Set<Adjustment>();
    public DbSet<MakeGoodPlan> MakeGoodPlans => Set<MakeGoodPlan>();
    public DbSet<MakeGoodAllocation> MakeGoodAllocations => Set<MakeGoodAllocation>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<CalcSnapshot> CalcSnapshots => Set<CalcSnapshot>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Contract>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        mb.Entity<AdSlot>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        mb.Entity<ContractSlot>(e =>
        {
            e.HasKey(x => new { x.ContractId, x.SlotId });
            e.HasOne(x => x.Contract).WithMany(x => x.ContractSlots).HasForeignKey(x => x.ContractId);
            e.HasOne(x => x.Slot).WithMany(x => x.ContractSlots).HasForeignKey(x => x.SlotId);
        });

        mb.Entity<ImportBatch>(e =>
        {
            // 批次级幂等键：同一批次重传不得叠加
            e.HasIndex(x => x.BatchKey).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        mb.Entity<ExposureFact>(e =>
        {
            // 行级去重键：来源|广告位|小时
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.SlotId, x.ServiceDate });
            e.HasOne(x => x.Batch).WithMany(x => x.Facts).HasForeignKey(x => x.BatchId);
            e.HasOne(x => x.Slot).WithMany().HasForeignKey(x => x.SlotId);
        });

        mb.Entity<DailyLedger>(e =>
        {
            e.HasIndex(x => new { x.ContractId, x.ServiceDate }).IsUnique();
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId);
        });

        mb.Entity<Adjustment>(e =>
        {
            e.HasIndex(x => new { x.ContractId, x.ServiceDate });
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId);
            e.HasOne(x => x.Batch).WithMany().HasForeignKey(x => x.BatchId);
        });

        mb.Entity<MakeGoodPlan>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId);
        });

        mb.Entity<MakeGoodAllocation>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.SourceSlotId, x.SourceServiceDate });
            e.HasOne(x => x.Plan).WithMany(x => x.Allocations).HasForeignKey(x => x.PlanId);
            e.HasOne(x => x.SourceSlot).WithMany().HasForeignKey(x => x.SourceSlotId);
        });

        mb.Entity<Discount>(e =>
        {
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId);
        });

        mb.Entity<CalcSnapshot>(e =>
        {
            e.HasIndex(x => new { x.ContractId, x.Version }).IsUnique();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId);
        });
    }
}
