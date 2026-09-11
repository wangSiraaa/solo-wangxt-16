using AdRecon.Api.Contracts;
using AdRecon.Api.Data;
using AdRecon.Api.Domain;
using AdRecon.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Api.Seed;

/// <summary>
/// 演示数据：一条完整业务时间线，覆盖三类问题样本——
///   跨时区归日：纽约广告位 UTC 02:00 的小时归到当地前一天；
///   迟到日志：账期确认后补传 8/3 数据 → 只产生差额，台账不动；
///   重复导入：同 BatchKey 重传 + 换键重投相同行，均不叠加。
/// 另含补量占用/释放与"一量两抵"拒绝的现场证据。
/// </summary>
public class DemoSeeder(
    AdReconDbContext db,
    ImportService importService,
    SettlementService settlementService)
{
    public const string SlotSh = "SH-OPENSCREEN";
    public const string SlotNy = "NY-FEED";
    public const string SlotLdn = "LDN-BANNER";
    public const string SlotPool = "POOL-REMNANT";
    public const string ContractA = "HT-2026-0801";
    public const string ContractB = "HT-2026-0802";

    public async Task ResetAndSeedAsync(CancellationToken ct = default)
    {
        await db.Database.EnsureDeletedAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        // ---------- 主数据 ----------
        var sh = new AdSlot { Id = Guid.NewGuid(), Code = SlotSh, Name = "上海开屏大屏", TimeZoneId = "Asia/Shanghai" };
        var ny = new AdSlot { Id = Guid.NewGuid(), Code = SlotNy, Name = "纽约信息流", TimeZoneId = "America/New_York" };
        var ldn = new AdSlot { Id = Guid.NewGuid(), Code = SlotLdn, Name = "伦敦横幅", TimeZoneId = "Europe/London" };
        var pool = new AdSlot { Id = Guid.NewGuid(), Code = SlotPool, Name = "补量资源池(余量)", TimeZoneId = "Asia/Shanghai" };
        db.AdSlots.AddRange(sh, ny, ldn, pool);

        var contractA = new Contract
        {
            Id = Guid.NewGuid(), Code = ContractA, AdvertiserName = "星澜饮料",
            CommittedImpressions = 1_000_000,
            PeriodStart = new DateOnly(2026, 8, 1), PeriodEnd = new DateOnly(2026, 8, 31)
        };
        var contractB = new Contract
        {
            Id = Guid.NewGuid(), Code = ContractB, AdvertiserName = "北境汽车",
            CommittedImpressions = 600_000,
            PeriodStart = new DateOnly(2026, 8, 1), PeriodEnd = new DateOnly(2026, 8, 31)
        };
        db.Contracts.AddRange(contractA, contractB);
        db.ContractSlots.AddRange(
            new ContractSlot { ContractId = contractA.Id, SlotId = sh.Id },
            new ContractSlot { ContractId = contractA.Id, SlotId = ny.Id },
            new ContractSlot { ContractId = contractB.Id, SlotId = ldn.Id });
        await db.SaveChangesAsync(ct);

        // ---------- 第 1 周正常投放（跨时区样本含在其中，资源池余量随行入池）----------
        // 模拟日志分片丢失：8/3 的三个小时缺失，稍后经 RECOVERY 批次补传
        var lostShards = new[]
        {
            (SlotSh, Utc(2026, 8, 3, 2)),
            (SlotNy, Utc(2026, 8, 3, 6)),
            (SlotLdn, Utc(2026, 8, 3, 9))
        }.ToHashSet();
        await importService.ImportAsync(new ImportRequest(
            "AUG-W01", "ADX-LOG", "ops.zhang", "8月第1周小时汇总（缺 8/3 三个小时分片）",
            HourlyRows(new DateOnly(2026, 8, 1), 7, sh.Id, ny.Id, ldn.Id, pool.Id,
                includePool: true, skip: lostShards)), ct);

        // ---------- 确认 8/1~8/7 账期（冻结台账）----------
        await settlementService.ClosePeriodAsync(contractA.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 7), "settle.li"), ct);
        await settlementService.ClosePeriodAsync(contractB.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 7), "settle.li"), ct);

        // ---------- 迟到日志：8/3 丢失分片补传 → 落在已确认账期，只生成差额 ----------
        await importService.ImportAsync(new ImportRequest(
            "AUG-W01-RECOVERY", "ADX-LOG", "ops.zhang", "日志补传：8/3 丢失分片恢复",
            new List<ImportRowRequest>
            {
                new(SlotSh, Utc(2026, 8, 3, 2), 612),
                new(SlotNy, Utc(2026, 8, 3, 6), 338),
                new(SlotLdn, Utc(2026, 8, 3, 9), 527)
            }), ct);

        // ---------- 重复导入样本 1：同 BatchKey 整批重传 → 幂等，不叠加 ----------
        await importService.ImportAsync(new ImportRequest(
            "AUG-W01", "ADX-LOG", "ops.zhang", "（重传）8月第1周小时汇总",
            HourlyRows(new DateOnly(2026, 8, 1), 7, sh.Id, ny.Id, ldn.Id, pool.Id,
                includePool: true, skip: lostShards)), ct);

        // ---------- 重复导入样本 2：换了批次键、内容相同的行 → 行级去重 ----------
        await importService.ImportAsync(new ImportRequest(
            "AUG-W01-RETRY", "ADX-LOG", "ops.wang", "网络重试导致的重复投递",
            new List<ImportRowRequest>
            {
                new(SlotSh, Utc(2026, 8, 1, 0), 999),   // 与 AUG-W01 中同键行冲突，被吞掉
                new(SlotSh, Utc(2026, 8, 1, 1), 999),
                new(SlotNy, Utc(2026, 8, 2, 2), 999)
            }), ct);

        // ---------- 剩余账期投放（故意再缺 3 个小时分片，供迟到样本演示）----------
        var pendingShards = new[]
        {
            (SlotSh, Utc(2026, 8, 12, 10)),
            (SlotNy, Utc(2026, 8, 15, 10)),
            (SlotLdn, Utc(2026, 8, 18, 10))
        }.ToHashSet();
        await importService.ImportAsync(new ImportRequest(
            "AUG-W2-W4", "ADX-LOG", "ops.zhang", "8/8~8/31 小时汇总（缺 8/12、8/15、8/18 各一个小时分片）",
            HourlyRows(new DateOnly(2026, 8, 8), 24, sh.Id, ny.Id, ldn.Id, pool.Id,
                includePool: false, skip: pendingShards)), ct);

        // ---------- 补量：合同A 缺口 → 占用资源池 9/1 的 50,000 ----------
        var planA = await settlementService.CreateMakeGoodAsync(contractA.Id,
            new CreateMakeGoodRequest("8月账期缺口补量", "settle.li",
                new List<CreateMakeGoodAllocationRequest>
                {
                    new(SlotPool, new DateOnly(2026, 9, 1), 50_000)
                }), ct);

        // ---------- 撤销演示：释放占用（资源池回冲，缺口回升，快照留痕）----------
        await settlementService.CancelMakeGoodAsync(planA.Id, ct);

        // ---------- 重新补量 40,000（最终生效）----------
        await settlementService.CreateMakeGoodAsync(contractA.Id,
            new CreateMakeGoodRequest("撤销后重议：按 40,000 补量了结", "settle.li",
                new List<CreateMakeGoodAllocationRequest>
                {
                    new(SlotPool, new DateOnly(2026, 9, 1), 40_000)
                }), ct);

        // ---------- 合同B：折让 20,000 了结部分缺口 ----------
        await settlementService.CreateDiscountAsync(contractB.Id,
            new CreateDiscountRequest(20_000, 3_600m, "客户接受折让了结部分缺口", "settle.li"), ct);
    }

    /// <summary>
    /// 生成确定性的按小时曝光行。量值由 (广告位,小时) 哈希决定，可重复播种。
    /// 纽约广告位刻意压低量级，让合同A 留有缺口；资源池在 9/1、9/2 各放一笔余量。
    /// </summary>
    private static List<ImportRowRequest> HourlyRows(
        DateOnly start, int days, Guid shId, Guid nyId, Guid ldnId, Guid poolId,
        bool includePool, HashSet<(string Slot, DateTime Hour)>? skip = null)
    {
        var rows = new List<ImportRowRequest>();
        for (var d = 0; d < days; d++)
        {
            var date = start.AddDays(d);
            for (var h = 0; h < 24; h++)
            {
                var hour = new DateTime(date.Year, date.Month, date.Day, h, 0, 0, DateTimeKind.Utc);
                if (skip is null || !skip.Contains((SlotSh, hour)))
                    rows.Add(new ImportRowRequest(SlotSh, hour, 500 + Stable(shId, hour) % 300));
                if (skip is null || !skip.Contains((SlotNy, hour)))
                    rows.Add(new ImportRowRequest(SlotNy, hour, 250 + Stable(nyId, hour) % 150));
                if (skip is null || !skip.Contains((SlotLdn, hour)))
                    rows.Add(new ImportRowRequest(SlotLdn, hour, 450 + Stable(ldnId, hour) % 300));
            }
        }
        // 资源池余量：9/1、9/2 两笔（在账期外，不影响自然交付）
        if (includePool)
            foreach (var day in new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2) })
                for (var h = 0; h < 24; h++)
                    rows.Add(new ImportRowRequest(SlotPool,
                        new DateTime(day.Year, day.Month, day.Day, h, 0, 0, DateTimeKind.Utc),
                        5_000 + Stable(poolId, new DateTime(day.Year, day.Month, day.Day, h, 0, 0, DateTimeKind.Utc)) % 1_000));
        return rows;
    }

    private static int Stable(Guid slotId, DateTime hour)
        => Math.Abs(HashCode.Combine(slotId, hour.Date, hour.Hour));

    private static DateTime Utc(int y, int m, int d, int h)
        => new(y, m, d, h, 0, 0, DateTimeKind.Utc);
}
