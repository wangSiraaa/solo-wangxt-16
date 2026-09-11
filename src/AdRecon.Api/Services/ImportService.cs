using AdRecon.Api.Contracts;
using AdRecon.Api.Data;
using AdRecon.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AdRecon.Api.Services;

/// <summary>
/// 导入服务。三道防线保证"重传不叠加"：
/// 1) BatchKey 唯一 —— 整批重传直接返回已有批次；
/// 2) DedupKey 唯一 —— 不同批次里的重复行被吞掉并计数；
/// 3) 数据库唯一索引兜底并发。
/// 迟到数据：归日落在已确认账期 → 生成 Adjustment 差额，绝不回写台账。
/// </summary>
public class ImportService(AdReconDbContext db, AccountingService accounting)
{
    public async Task<ImportResultDto> ImportAsync(ImportRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.BatchKey))
            throw new ApiException(400, "BATCH_KEY_REQUIRED", "BatchKey 不能为空");
        if (req.Rows.Count == 0)
            throw new ApiException(400, "ROWS_REQUIRED", "导入行不能为空");
        if (req.Rows.Any(r => r.ValidImpressions < 0))
            throw new ApiException(400, "NEGATIVE_IMPRESSIONS", "曝光量不能为负数");

        // 防线 1：整批幂等 —— 同 BatchKey 重传，返回首次导入的结果，不叠加
        var existing = await db.ImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BatchKey == req.BatchKey, ct);
        if (existing is not null)
        {
            return new ImportResultDto(
                existing.Id, existing.BatchKey, existing.Status.ToString(),
                AlreadyExisted: true,
                existing.RowCount, existing.AcceptedCount, existing.DuplicateCount,
                LateAdjustmentsGenerated: 0, AffectedContractIds: new List<Guid>());
        }

        var slotCodes = req.Rows.Select(r => r.SlotCode).Distinct().ToList();
        var slots = await db.AdSlots
            .Include(s => s.ContractSlots)
            .Where(s => slotCodes.Contains(s.Code))
            .ToDictionaryAsync(s => s.Code, ct);

        var unknown = slotCodes.Where(c => !slots.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
            throw new ApiException(400, "UNKNOWN_SLOT", $"未知广告位: {string.Join(", ", unknown)}");

        var batch = new ImportBatch
        {
            Id = Guid.NewGuid(),
            BatchKey = req.BatchKey,
            Source = req.Source,
            ImportedBy = req.ImportedBy,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            RowCount = req.Rows.Count,
            Status = BatchStatus.Completed,
            Note = req.Note
        };
        db.ImportBatches.Add(batch);

        var affectedContracts = new HashSet<Guid>();
        var lateAdjustments = 0;

        foreach (var row in req.Rows)
        {
            var slot = slots[row.SlotCode];
            var hourUtc = row.HourUtc.Kind == DateTimeKind.Utc
                ? row.HourUtc
                : DateTime.SpecifyKind(row.HourUtc, DateTimeKind.Utc);
            var dedupKey = ServiceDateCalculator.DedupKey(req.Source, slot.Code, hourUtc);

            // 防线 2：行级去重 —— 同一 (来源,广告位,小时) 已存在则跳过
            var isDuplicate = await db.ExposureFacts.AnyAsync(f => f.DedupKey == dedupKey, ct);
            if (isDuplicate)
            {
                batch.DuplicateCount++;
                continue;
            }

            var serviceDate = ServiceDateCalculator.ToServiceDate(hourUtc, slot.TimeZoneId);
            db.ExposureFacts.Add(new ExposureFact
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                SlotId = slot.Id,
                HourUtc = hourUtc,
                ServiceDate = serviceDate,
                ValidImpressions = row.ValidImpressions,
                DedupKey = dedupKey
            });
            batch.AcceptedCount++;

            // 迟到判定：该归日若已被某合同确认账期 → 只记差额，不改台账
            foreach (var cs in slot.ContractSlots)
            {
                affectedContracts.Add(cs.ContractId);
                var isConfirmed = await db.DailyLedgers.AnyAsync(
                    l => l.ContractId == cs.ContractId && l.ServiceDate == serviceDate, ct);
                if (isConfirmed)
                {
                    db.Adjustments.Add(new Adjustment
                    {
                        Id = Guid.NewGuid(),
                        ContractId = cs.ContractId,
                        ServiceDate = serviceDate,
                        DeltaImpressions = row.ValidImpressions,
                        Reason = AdjustmentReasons.LateArrival,
                        BatchId = batch.Id,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    });
                    lateAdjustments++;
                }
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 防线 3：并发下同键撞车 —— 清掉失败状态，按已存在批次返回
            db.ChangeTracker.Clear();
            var winner = await db.ImportBatches.AsNoTracking()
                .FirstAsync(b => b.BatchKey == req.BatchKey, ct);
            return new ImportResultDto(
                winner.Id, winner.BatchKey, winner.Status.ToString(),
                AlreadyExisted: true,
                winner.RowCount, winner.AcceptedCount, winner.DuplicateCount,
                0, new List<Guid>());
        }

        // 每个受影响合同落一份核算快照（导入后口径）
        foreach (var contractId in affectedContracts)
            await accounting.WriteSnapshot(contractId, $"Import:{req.BatchKey}", ct);

        return new ImportResultDto(
            batch.Id, batch.BatchKey, batch.Status.ToString(),
            AlreadyExisted: false,
            batch.RowCount, batch.AcceptedCount, batch.DuplicateCount,
            lateAdjustments, affectedContracts.ToList());
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
