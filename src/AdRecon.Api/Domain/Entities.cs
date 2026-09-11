namespace AdRecon.Api.Domain;

/// <summary>包量合同：客户经理维护的承诺量与账期。</summary>
public class Contract
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string AdvertiserName { get; set; }
    public long CommittedImpressions { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;

    public List<ContractSlot> ContractSlots { get; set; } = new();
}

/// <summary>广告位。曝光按广告位所属时区归日。</summary>
public class AdSlot
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }

    /// <summary>IANA 时区，如 Asia/Shanghai、America/New_York。</summary>
    public required string TimeZoneId { get; set; }

    public List<ContractSlot> ContractSlots { get; set; } = new();
}

/// <summary>合同-广告位关联（一个合同可包多个广告位）。</summary>
public class ContractSlot
{
    public Guid ContractId { get; set; }
    public Guid SlotId { get; set; }

    public Contract Contract { get; set; } = null!;
    public AdSlot Slot { get; set; } = null!;
}

/// <summary>原始导入批次。BatchKey 是批次级幂等键：同键重传不叠加。</summary>
public class ImportBatch
{
    public Guid Id { get; set; }
    public required string BatchKey { get; set; }
    public required string Source { get; set; }
    public required string ImportedBy { get; set; }
    public DateTimeOffset ImportedAtUtc { get; set; }
    public int RowCount { get; set; }
    public int AcceptedCount { get; set; }
    public int DuplicateCount { get; set; }
    public BatchStatus Status { get; set; }
    public string? Note { get; set; }

    public List<ExposureFact> Facts { get; set; } = new();
}

/// <summary>
/// 按小时汇总的有效曝光（原始事实，永不更新）。
/// DedupKey 是行级去重键：不同批次重复投递同一 (来源,广告位,小时) 不会叠加。
/// ServiceDate 是导入时按广告位时区算出的归日，冗余存储以免重算漂移。
/// </summary>
public class ExposureFact
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid SlotId { get; set; }

    /// <summary>小时起点（UTC）。</summary>
    public DateTime HourUtc { get; set; }

    /// <summary>归日：HourUtc 换算到广告位时区后的日历日。</summary>
    public DateOnly ServiceDate { get; set; }

    public long ValidImpressions { get; set; }
    public required string DedupKey { get; set; }

    public ImportBatch Batch { get; set; } = null!;
    public AdSlot Slot { get; set; } = null!;
}

/// <summary>
/// 已确认账期的日台账。确认后冻结，迟到数据只能走 Adjustment 差额。
/// (ContractId, ServiceDate) 唯一。
/// </summary>
public class DailyLedger
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public DateOnly ServiceDate { get; set; }
    public long ConfirmedImpressions { get; set; }
    public DateTimeOffset ConfirmedAtUtc { get; set; }

    public Contract Contract { get; set; } = null!;
}

/// <summary>差额调整：迟到数据落在已确认账期时生成，正向累加，不覆盖台账。</summary>
public class Adjustment
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public DateOnly ServiceDate { get; set; }
    public long DeltaImpressions { get; set; }
    public required string Reason { get; set; }
    public Guid BatchId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Contract Contract { get; set; } = null!;
    public ImportBatch Batch { get; set; } = null!;
}

/// <summary>补量计划：结算人员针对缺口发起，可撤销。</summary>
public class MakeGoodPlan
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public long PlannedImpressions { get; set; }
    public MakeGoodStatus Status { get; set; }
    public required string Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }

    public Contract Contract { get; set; } = null!;
    public List<MakeGoodAllocation> Allocations { get; set; } = new();
}

/// <summary>
/// 补量划拨：把资源池某广告位某日的曝光占用给某个计划。
/// 同一 (SourceSlotId, SourceServiceDate) 的 Active 占用量受池容量约束，
/// 因此同一笔补量曝光不可能同时冲抵两个合同；撤销计划即释放占用。
/// </summary>
public class MakeGoodAllocation
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid SourceSlotId { get; set; }
    public DateOnly SourceServiceDate { get; set; }
    public long AllocatedImpressions { get; set; }
    public AllocationStatus Status { get; set; }

    public MakeGoodPlan Plan { get; set; } = null!;
    public AdSlot SourceSlot { get; set; } = null!;
}

/// <summary>折让：结算人员选择不补量、以折让当量了结缺口。</summary>
public class Discount
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public long Impressions { get; set; }
    public decimal? Amount { get; set; }
    public required string Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Contract Contract { get; set; } = null!;
}

/// <summary>
/// 计算快照：每次核算触发动作（导入/确认账期/补量/撤销/折让）后落库，
/// 记录当时的缺口构成与明细载荷，作为可回查依据。
/// </summary>
public class CalcSnapshot
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset AsOfUtc { get; set; }
    public required string Trigger { get; set; }

    public long CommittedImpressions { get; set; }
    public long ConfirmedImpressions { get; set; }
    public long UnconfirmedImpressions { get; set; }
    public long AdjustmentImpressions { get; set; }
    public long MakeGoodImpressions { get; set; }
    public long DiscountImpressions { get; set; }
    public long DeliveredImpressions { get; set; }
    public long GapImpressions { get; set; }

    /// <summary>jsonb：每日明细 + 来源汇总 + 当时生效的补量/差额，供回查。</summary>
    public required string PayloadJson { get; set; }

    public Contract Contract { get; set; } = null!;
}
