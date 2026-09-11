using System.Text.Json;
using AdRecon.Api.Contracts;
using AdRecon.Api.Data;
using AdRecon.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Api.Services;

/// <summary>
/// 核算服务：把原始事实、已确认台账、迟到差额、补量、折让汇总成合同缺口。
/// 口径：Delivered = Confirmed + Unconfirmed + LateAdjustments
///       Gap      = Committed - Delivered - MakeGood(Active) - Discount
/// 已确认账期内的原始事实不再重复计入 Unconfirmed，迟到部分只经 Adjustment 体现。
/// </summary>
public class AccountingService(AdReconDbContext db)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task<RollupDto> GetRollup(Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking()
            .Include(c => c.ContractSlots)
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        return await ComputeRollup(contract, ct);
    }

    public async Task<List<RollupDto>> GetRollups(CancellationToken ct = default)
    {
        var contracts = await db.Contracts.AsNoTracking()
            .Include(c => c.ContractSlots)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);

        var result = new List<RollupDto>(contracts.Count);
        foreach (var c in contracts)
            result.Add(await ComputeRollup(c, ct));
        return result;
    }

    private async Task<RollupDto> ComputeRollup(Contract contract, CancellationToken ct)
    {
        var slotIds = contract.ContractSlots.Select(s => s.SlotId).ToList();

        // 已确认台账
        var confirmed = await db.DailyLedgers
            .Where(l => l.ContractId == contract.Id)
            .SumAsync(l => (long?)l.ConfirmedImpressions, ct) ?? 0;

        // 未确认部分：合同期内、尚未落入已确认台账日期的原始事实
        var confirmedDates = db.DailyLedgers
            .Where(l => l.ContractId == contract.Id)
            .Select(l => l.ServiceDate);

        var unconfirmed = await db.ExposureFacts
            .Where(f => slotIds.Contains(f.SlotId)
                        && f.ServiceDate >= contract.PeriodStart
                        && f.ServiceDate <= contract.PeriodEnd
                        && !confirmedDates.Contains(f.ServiceDate))
            .SumAsync(f => (long?)f.ValidImpressions, ct) ?? 0;

        // 迟到差额（已确认账期之后补传的部分）
        var adjustments = await db.Adjustments
            .Where(a => a.ContractId == contract.Id)
            .SumAsync(a => (long?)a.DeltaImpressions, ct) ?? 0;

        // 生效中的补量占用
        var makeGood = await db.MakeGoodAllocations
            .Where(a => a.Status == AllocationStatus.Active
                        && a.Plan.ContractId == contract.Id
                        && a.Plan.Status == MakeGoodStatus.Active)
            .SumAsync(a => (long?)a.AllocatedImpressions, ct) ?? 0;

        // 折让当量
        var discount = await db.Discounts
            .Where(d => d.ContractId == contract.Id)
            .SumAsync(d => (long?)d.Impressions, ct) ?? 0;

        var delivered = confirmed + unconfirmed + adjustments;
        var settled = delivered + makeGood + discount;
        var gapRaw = contract.CommittedImpressions - settled;

        return new RollupDto(
            contract.Id, contract.Code, contract.AdvertiserName,
            contract.PeriodStart, contract.PeriodEnd, contract.Status.ToString(),
            contract.CommittedImpressions,
            confirmed, unconfirmed, adjustments, makeGood, discount,
            delivered,
            Math.Max(0, gapRaw),
            Math.Max(0, -gapRaw),
            contract.CommittedImpressions == 0
                ? 0
                : Math.Round((double)settled / contract.CommittedImpressions, 4));
    }

    /// <summary>每日归集：原始量 / 已确认量 / 迟到差额 / 是否已确认。</summary>
    public async Task<List<DailyRowDto>> GetDaily(Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking()
            .Include(c => c.ContractSlots)
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        var slotIds = contract.ContractSlots.Select(s => s.SlotId).ToList();

        var rawByDay = await db.ExposureFacts
            .Where(f => slotIds.Contains(f.SlotId)
                        && f.ServiceDate >= contract.PeriodStart
                        && f.ServiceDate <= contract.PeriodEnd)
            .GroupBy(f => f.ServiceDate)
            .Select(g => new { Date = g.Key, Total = g.Sum(f => f.ValidImpressions) })
            .ToListAsync(ct);

        var ledger = await db.DailyLedgers
            .Where(l => l.ContractId == contract.Id)
            .ToDictionaryAsync(l => l.ServiceDate, l => l.ConfirmedImpressions, ct);

        var adjByDay = await db.Adjustments
            .Where(a => a.ContractId == contract.Id)
            .GroupBy(a => a.ServiceDate)
            .Select(g => new { Date = g.Key, Total = g.Sum(a => a.DeltaImpressions) })
            .ToListAsync(ct);

        var dates = rawByDay.Select(r => r.Date)
            .Union(ledger.Keys)
            .Union(adjByDay.Select(a => a.Date))
            .OrderBy(d => d);

        return dates.Select(d => new DailyRowDto(
            d,
            rawByDay.FirstOrDefault(r => r.Date == d)?.Total ?? 0,
            ledger.TryGetValue(d, out var c) ? c : 0,
            adjByDay.FirstOrDefault(a => a.Date == d)?.Total ?? 0,
            ledger.ContainsKey(d)))
            .ToList();
    }

    /// <summary>钻取第一层：合同 → 广告位。</summary>
    public async Task<List<SlotRollupDto>> GetSlotRollup(Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking()
            .Include(c => c.ContractSlots).ThenInclude(cs => cs.Slot)
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        var result = new List<SlotRollupDto>();
        foreach (var cs in contract.ContractSlots)
        {
            var total = await db.ExposureFacts
                .Where(f => f.SlotId == cs.SlotId
                            && f.ServiceDate >= contract.PeriodStart
                            && f.ServiceDate <= contract.PeriodEnd)
                .SumAsync(f => (long?)f.ValidImpressions, ct) ?? 0;
            result.Add(new SlotRollupDto(cs.SlotId, cs.Slot.Code, cs.Slot.Name, cs.Slot.TimeZoneId, total));
        }
        return result.OrderByDescending(s => s.Impressions).ToList();
    }

    /// <summary>钻取第二层：广告位 → 导入批次。</summary>
    public async Task<List<BatchRollupDto>> GetSlotBatches(
        Guid contractId, Guid slotId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        var rows = await db.ExposureFacts
            .Where(f => f.SlotId == slotId
                        && f.ServiceDate >= contract.PeriodStart
                        && f.ServiceDate <= contract.PeriodEnd)
            .GroupBy(f => new
            {
                f.BatchId,
                f.Batch.BatchKey,
                f.Batch.Source,
                f.Batch.ImportedBy,
                f.Batch.ImportedAtUtc
            })
            .Select(g => new BatchRollupDto(
                g.Key.BatchId, g.Key.BatchKey, g.Key.Source, g.Key.ImportedBy,
                g.Key.ImportedAtUtc, g.Count(), g.Sum(f => f.ValidImpressions)))
            .OrderByDescending(b => b.ImportedAtUtc)
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>钻取第三层：批次 → 小时行（UTC 小时与归日并列，时区换算可回查）。</summary>
    public async Task<List<FactRowDto>> GetBatchRows(Guid batchId, CancellationToken ct = default)
        => await db.ExposureFacts.AsNoTracking()
            .Where(f => f.BatchId == batchId)
            .OrderBy(f => f.HourUtc)
            .Select(f => new FactRowDto(f.HourUtc, f.ServiceDate, f.ValidImpressions, f.DedupKey))
            .ToListAsync(ct);

    /// <summary>写计算快照：任何改变缺口构成的动作之后调用，留下可回查依据。</summary>
    public async Task<CalcSnapshot> WriteSnapshot(
        Guid contractId, string trigger, CancellationToken ct = default)
    {
        var rollup = await GetRollup(contractId, ct);
        var daily = await GetDaily(contractId, ct);
        var slots = await GetSlotRollup(contractId, ct);

        var adjustments = await db.Adjustments.AsNoTracking()
            .Where(a => a.ContractId == contractId)
            .OrderBy(a => a.ServiceDate)
            .Select(a => new
            {
                a.ServiceDate,
                a.DeltaImpressions,
                a.Reason,
                BatchKey = a.Batch.BatchKey,
                a.CreatedAtUtc
            })
            .ToListAsync(ct);

        var plans = await db.MakeGoodPlans.AsNoTracking()
            .Where(p => p.ContractId == contractId)
            .Include(p => p.Allocations).ThenInclude(a => a.SourceSlot)
            .ToListAsync(ct);

        var version = await db.CalcSnapshots.CountAsync(s => s.ContractId == contractId, ct) + 1;

        var payload = new
        {
            contract = new
            {
                rollup.Code,
                rollup.AdvertiserName,
                rollup.PeriodStart,
                rollup.PeriodEnd,
                rollup.Committed
            },
            formula = "Delivered = Confirmed + Unconfirmed + LateAdjustments; Gap = Committed - Delivered - MakeGood - Discount",
            daily = daily.Select(d => new
            {
                d.ServiceDate,
                d.RawImpressions,
                d.ConfirmedImpressions,
                d.AdjustmentImpressions,
                d.IsConfirmed
            }),
            slots = slots.Select(s => new { s.Code, s.Name, s.TimeZoneId, s.Impressions }),
            lateAdjustments = adjustments,
            makeGoodPlans = plans.Select(p => new
            {
                p.Id,
                p.PlannedImpressions,
                Status = p.Status.ToString(),
                p.Reason,
                p.CreatedAtUtc,
                p.CancelledAtUtc,
                allocations = p.Allocations.Select(a => new
                {
                    a.SourceSlot.Code,
                    a.SourceServiceDate,
                    a.AllocatedImpressions,
                    Status = a.Status.ToString()
                })
            })
        };

        var snapshot = new CalcSnapshot
        {
            Id = Guid.NewGuid(),
            ContractId = contractId,
            Version = version,
            AsOfUtc = DateTimeOffset.UtcNow,
            Trigger = trigger,
            CommittedImpressions = rollup.Committed,
            ConfirmedImpressions = rollup.Confirmed,
            UnconfirmedImpressions = rollup.Unconfirmed,
            AdjustmentImpressions = rollup.LateAdjustments,
            MakeGoodImpressions = rollup.MakeGood,
            DiscountImpressions = rollup.Discount,
            DeliveredImpressions = rollup.Delivered,
            GapImpressions = rollup.Gap,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOpts)
        };
        db.CalcSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }
}

/// <summary>业务异常 → HTTP 状态码映射。</summary>
public class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
