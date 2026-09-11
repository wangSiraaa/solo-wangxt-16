using AdRecon.Api.Contracts;
using AdRecon.Api.Data;
using AdRecon.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Api.Services;

/// <summary>
/// 结算服务：确认账期（冻结台账）、补量计划（占用/释放资源池曝光）、折让。
/// 补量独占规则：
///   - 资源池 = 未挂在任何 Active 合同下的广告位曝光（避免一笔曝光既算自然交付又算补量）；
///   - 同一 (广告位, 归日) 的 Active 占用总量不得超过当日池内曝光量，
///     因此同一笔补量曝光不可能同时冲抵两个合同；
///   - 撤销计划 → 划拨全部 Released，占用立即释放。
/// </summary>
public class SettlementService(AdReconDbContext db, AccountingService accounting)
{
    /// <summary>确认账期：把 throughDate 之前（含）未确认的归日冻结进台账。</summary>
    public async Task<int> ClosePeriodAsync(
        Guid contractId, ClosePeriodRequest req, CancellationToken ct = default)
    {
        var contract = await db.Contracts
            .Include(c => c.ContractSlots)
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        var through = req.ThroughDate ?? contract.PeriodEnd;
        if (through > contract.PeriodEnd) through = contract.PeriodEnd;
        if (through < contract.PeriodStart)
            throw new ApiException(400, "INVALID_RANGE", "确认截止日期早于账期开始");

        var slotIds = contract.ContractSlots.Select(s => s.SlotId).ToList();
        var confirmedDates = (await db.DailyLedgers
                .Where(l => l.ContractId == contractId)
                .Select(l => l.ServiceDate)
                .ToListAsync(ct))
            .ToHashSet();

        var rawByDay = await db.ExposureFacts
            .Where(f => slotIds.Contains(f.SlotId)
                        && f.ServiceDate >= contract.PeriodStart
                        && f.ServiceDate <= through)
            .GroupBy(f => f.ServiceDate)
            .Select(g => new { Date = g.Key, Total = g.Sum(f => f.ValidImpressions) })
            .ToDictionaryAsync(x => x.Date, x => x.Total, ct);

        var now = DateTimeOffset.UtcNow;
        var closed = 0;
        for (var d = contract.PeriodStart; d <= through; d = d.AddDays(1))
        {
            if (confirmedDates.Contains(d)) continue;
            db.DailyLedgers.Add(new DailyLedger
            {
                Id = Guid.NewGuid(),
                ContractId = contractId,
                ServiceDate = d,
                ConfirmedImpressions = rawByDay.GetValueOrDefault(d, 0),
                ConfirmedAtUtc = now
            });
            closed++;
        }
        await db.SaveChangesAsync(ct);

        await accounting.WriteSnapshot(contractId, $"ClosePeriod:through={through:yyyy-MM-dd}", ct);
        return closed;
    }

    /// <summary>可划拨的补量资源池：未被任何 Active 合同占用的广告位曝光。</summary>
    public async Task<List<PoolRowDto>> GetPoolAsync(CancellationToken ct = default)
    {
        var attachedSlotIds = db.ContractSlots
            .Where(cs => cs.Contract.Status == ContractStatus.Active)
            .Select(cs => cs.SlotId);

        var poolFacts = await db.ExposureFacts
            .Where(f => !attachedSlotIds.Contains(f.SlotId))
            .GroupBy(f => new { f.SlotId, f.ServiceDate })
            .Select(g => new { g.Key.SlotId, g.Key.ServiceDate, Total = g.Sum(f => f.ValidImpressions) })
            .ToListAsync(ct);

        var occupied = await db.MakeGoodAllocations
            .Where(a => a.Status == AllocationStatus.Active && a.Plan.Status == MakeGoodStatus.Active)
            .GroupBy(a => new { a.SourceSlotId, a.SourceServiceDate })
            .Select(g => new { g.Key.SourceSlotId, g.Key.SourceServiceDate, Total = g.Sum(a => a.AllocatedImpressions) })
            .ToListAsync(ct);

        var slotIds = poolFacts.Select(p => p.SlotId).Distinct().ToList();
        var slots = await db.AdSlots.Where(s => slotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        return poolFacts
            .Select(p =>
            {
                var occ = occupied.FirstOrDefault(o =>
                    o.SourceSlotId == p.SlotId && o.SourceServiceDate == p.ServiceDate)?.Total ?? 0;
                var slot = slots[p.SlotId];
                return new PoolRowDto(
                    p.SlotId, slot.Code, slot.Name, p.ServiceDate,
                    p.Total, occ, p.Total - occ);
            })
            .OrderBy(p => p.SlotCode).ThenBy(p => p.ServiceDate)
            .ToList();
    }

    /// <summary>创建补量计划：校验池容量后占用。同一笔曝光不会冲抵两个合同。</summary>
    public async Task<MakeGoodPlanDto> CreateMakeGoodAsync(
        Guid contractId, CreateMakeGoodRequest req, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");

        if (req.Allocations.Count == 0)
            throw new ApiException(400, "ALLOCATIONS_REQUIRED", "补量计划至少包含一条划拨");
        if (req.Allocations.Any(a => a.Impressions <= 0))
            throw new ApiException(400, "INVALID_ALLOCATION", "划拨量必须为正数");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pool = await GetPoolAsync(ct);
        // 同一请求内多条划拨命中同一 (广告位,归日) 时，逐条扣减可用量
        var pendingUse = new Dictionary<(Guid SlotId, DateOnly Date), long>();
        var plan = new MakeGoodPlan
        {
            Id = Guid.NewGuid(),
            ContractId = contractId,
            Status = MakeGoodStatus.Active,
            Reason = req.Reason,
            CreatedBy = req.CreatedBy,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PlannedImpressions = req.Allocations.Sum(a => a.Impressions)
        };

        foreach (var allocReq in req.Allocations)
        {
            var slot = await db.AdSlots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Code == allocReq.SourceSlotCode, ct)
                ?? throw new ApiException(400, "UNKNOWN_SLOT", $"未知广告位: {allocReq.SourceSlotCode}");

            // 挂在其他 Active 合同下的广告位，其曝光已计入该合同自然交付，禁止再当补量
            var attachedToActive = await db.ContractSlots
                .AnyAsync(cs => cs.SlotId == slot.Id && cs.Contract.Status == ContractStatus.Active, ct);
            if (attachedToActive)
                throw new ApiException(409, "SLOT_NOT_POOL",
                    $"广告位 {slot.Code} 挂在生效合同下，其曝光已计入自然交付，不能用作补量");

            var poolRow = pool.FirstOrDefault(p =>
                p.SlotId == slot.Id && p.ServiceDate == allocReq.SourceServiceDate);
            if (poolRow is null)
                throw new ApiException(409, "POOL_EMPTY",
                    $"资源池中 {slot.Code} 在 {allocReq.SourceServiceDate:yyyy-MM-dd} 无可用曝光");

            var key = (slot.Id, allocReq.SourceServiceDate);
            var alreadyPending = pendingUse.GetValueOrDefault(key, 0);
            var available = poolRow.AvailableImpressions - alreadyPending;
            if (allocReq.Impressions > available)
                throw new ApiException(409, "POOL_EXHAUSTED",
                    $"资源池余量不足：{slot.Code} {allocReq.SourceServiceDate:yyyy-MM-dd} " +
                    $"可用 {available}，申请 {allocReq.Impressions}。" +
                    "该笔曝光可能已被其他合同的补量计划占用，同一笔补量曝光不能冲抵两个合同。");
            pendingUse[key] = alreadyPending + allocReq.Impressions;

            plan.Allocations.Add(new MakeGoodAllocation
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                SourceSlotId = slot.Id,
                SourceServiceDate = allocReq.SourceServiceDate,
                AllocatedImpressions = allocReq.Impressions,
                Status = AllocationStatus.Active
            });
        }

        db.MakeGoodPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await accounting.WriteSnapshot(contractId, $"MakeGoodCreated:{plan.Id}", ct);
        return await GetPlanDto(plan.Id, ct);
    }

    /// <summary>撤销补量计划：释放全部占用，缺口回到未补状态（快照留痕）。</summary>
    public async Task<MakeGoodPlanDto> CancelMakeGoodAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await db.MakeGoodPlans
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new ApiException(404, "PLAN_NOT_FOUND", $"补量计划不存在: {planId}");

        if (plan.Status != MakeGoodStatus.Active)
            throw new ApiException(409, "PLAN_NOT_ACTIVE", $"计划状态为 {plan.Status}，不能撤销");

        plan.Status = MakeGoodStatus.Cancelled;
        plan.CancelledAtUtc = DateTimeOffset.UtcNow;
        foreach (var alloc in plan.Allocations)
            alloc.Status = AllocationStatus.Released; // 释放占用，曝光回到资源池

        await db.SaveChangesAsync(ct);
        await accounting.WriteSnapshot(plan.ContractId, $"MakeGoodCancelled:{plan.Id}", ct);
        return await GetPlanDto(plan.Id, ct);
    }

    public async Task<DiscountDto> CreateDiscountAsync(
        Guid contractId, CreateDiscountRequest req, CancellationToken ct = default)
    {
        _ = await db.Contracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw new ApiException(404, "CONTRACT_NOT_FOUND", $"合同不存在: {contractId}");
        if (req.Impressions <= 0)
            throw new ApiException(400, "INVALID_DISCOUNT", "折让当量必须为正数");

        var discount = new Discount
        {
            Id = Guid.NewGuid(),
            ContractId = contractId,
            Impressions = req.Impressions,
            Amount = req.Amount,
            Reason = req.Reason,
            CreatedBy = req.CreatedBy,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Discounts.Add(discount);
        await db.SaveChangesAsync(ct);

        await accounting.WriteSnapshot(contractId, $"Discount:{discount.Id}", ct);
        return new DiscountDto(discount.Id, discount.Impressions, discount.Amount,
            discount.Reason, discount.CreatedBy, discount.CreatedAtUtc);
    }

    public async Task<List<MakeGoodPlanDto>> GetPlansAsync(Guid? contractId, CancellationToken ct = default)
    {
        var query = db.MakeGoodPlans.AsNoTracking()
            .Include(p => p.Contract)
            .Include(p => p.Allocations).ThenInclude(a => a.SourceSlot)
            .AsQueryable();
        if (contractId.HasValue)
            query = query.Where(p => p.ContractId == contractId.Value);

        var plans = await query.OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        return plans.Select(ToDto).ToList();
    }

    private async Task<MakeGoodPlanDto> GetPlanDto(Guid planId, CancellationToken ct)
    {
        var plan = await db.MakeGoodPlans.AsNoTracking()
            .Include(p => p.Contract)
            .Include(p => p.Allocations).ThenInclude(a => a.SourceSlot)
            .FirstAsync(p => p.Id == planId, ct);
        return ToDto(plan);
    }

    private static MakeGoodPlanDto ToDto(MakeGoodPlan p) => new(
        p.Id, p.ContractId, p.Contract.Code, p.PlannedImpressions,
        p.Status.ToString(), p.Reason, p.CreatedBy, p.CreatedAtUtc, p.CancelledAtUtc,
        p.Allocations.Select(a => new MakeGoodAllocationDto(
            a.Id, a.SourceSlot.Code, a.SourceSlot.Name,
            a.SourceServiceDate, a.AllocatedImpressions, a.Status.ToString()))
            .ToList());
}
