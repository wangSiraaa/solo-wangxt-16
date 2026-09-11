namespace AdRecon.Api.Contracts;

// ---------- 核算汇总 ----------
public record RollupDto(
    Guid ContractId,
    string Code,
    string AdvertiserName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    long Committed,
    long Confirmed,
    long Unconfirmed,
    long LateAdjustments,
    long MakeGood,
    long Discount,
    long Delivered,          // Confirmed + Unconfirmed + LateAdjustments
    long Gap,                // max(0, Committed - Delivered - MakeGood - Discount)
    long OverDelivered,      // max(0, Delivered + MakeGood + Discount - Committed)
    double Progress);        // (Delivered + MakeGood + Discount) / Committed

public record DailyRowDto(
    DateOnly ServiceDate,
    long RawImpressions,       // 当日全部原始事实（含确认后迟到的部分）
    long ConfirmedImpressions, // 已确认台账（未确认为 null 语义，用 0 + IsConfirmed=false）
    long AdjustmentImpressions,
    bool IsConfirmed);

public record SlotRollupDto(Guid SlotId, string Code, string Name, string TimeZoneId, long Impressions);

public record BatchRollupDto(
    Guid BatchId, string BatchKey, string Source, string ImportedBy,
    DateTimeOffset ImportedAtUtc, int Rows, long Impressions);

public record FactRowDto(DateTime HourUtc, DateOnly ServiceDate, long ValidImpressions, string DedupKey);

public record AdjustmentDto(
    Guid Id, DateOnly ServiceDate, long DeltaImpressions, string Reason,
    Guid BatchId, string BatchKey, DateTimeOffset CreatedAtUtc);

public record SnapshotDto(
    Guid Id, int Version, DateTimeOffset AsOfUtc, string Trigger,
    long Committed, long Confirmed, long Unconfirmed, long LateAdjustments,
    long MakeGood, long Discount, long Delivered, long Gap);

public record SnapshotDetailDto(
    Guid Id, int Version, DateTimeOffset AsOfUtc, string Trigger,
    long Committed, long Confirmed, long Unconfirmed, long LateAdjustments,
    long MakeGood, long Discount, long Delivered, long Gap,
    object Payload);

public record MakeGoodPlanDto(
    Guid Id, Guid ContractId, string ContractCode, long PlannedImpressions,
    string Status, string Reason, string CreatedBy,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CancelledAtUtc,
    List<MakeGoodAllocationDto> Allocations);

public record MakeGoodAllocationDto(
    Guid Id, string SourceSlotCode, string SourceSlotName,
    DateOnly SourceServiceDate, long AllocatedImpressions, string Status);

public record PoolRowDto(
    Guid SlotId, string SlotCode, string SlotName, DateOnly ServiceDate,
    long TotalImpressions, long OccupiedImpressions, long AvailableImpressions);

public record DiscountDto(
    Guid Id, long Impressions, decimal? Amount, string Reason,
    string CreatedBy, DateTimeOffset CreatedAtUtc);

// ---------- 请求 ----------
public record ImportRowRequest(string SlotCode, DateTime HourUtc, long ValidImpressions);

public record ImportRequest(
    string BatchKey, string Source, string ImportedBy,
    string? Note, List<ImportRowRequest> Rows);

public record ImportResultDto(
    Guid BatchId, string BatchKey, string Status, bool AlreadyExisted,
    int RowCount, int AcceptedCount, int DuplicateCount,
    int LateAdjustmentsGenerated, List<Guid> AffectedContractIds);

public record ClosePeriodRequest(DateOnly? ThroughDate, string? OperatorName);

public record CreateMakeGoodRequest(
    string Reason, string CreatedBy, List<CreateMakeGoodAllocationRequest> Allocations);

public record CreateMakeGoodAllocationRequest(
    string SourceSlotCode, DateOnly SourceServiceDate, long Impressions);

public record CreateDiscountRequest(
    long Impressions, decimal? Amount, string Reason, string CreatedBy);

public record ApiError(string Code, string Message);
