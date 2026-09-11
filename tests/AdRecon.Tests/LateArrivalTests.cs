using AdRecon.Api.Contracts;
using AdRecon.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Tests;

/// <summary>迟到数据必须生成差额，而不是覆盖已确认账期。</summary>
public class LateArrivalTests : PostgresTestBase
{
    [Fact]
    public async Task LateDataAfterClose_GeneratesAdjustment_LedgerUntouched()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        // 8/1 正常数据 → 确认 8/1 账期
        await Imports.ImportAsync(new ImportRequest("B-1", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 10_000) }));
        await Settlement.ClosePeriodAsync(contract.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 1), "settle"));

        var ledgerBefore = await Db.DailyLedgers
            .SingleAsync(l => l.ContractId == contract.Id && l.ServiceDate == new DateOnly(2026, 8, 1));
        Assert.Equal(10_000, ledgerBefore.ConfirmedImpressions);

        // 8/1 的迟到补传
        var late = await Imports.ImportAsync(new ImportRequest("B-LATE", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 1), 4_000) }));

        Assert.Equal(1, late.LateAdjustmentsGenerated);

        // 台账未被覆盖
        var ledgerAfter = await Db.DailyLedgers
            .SingleAsync(l => l.ContractId == contract.Id && l.ServiceDate == new DateOnly(2026, 8, 1));
        Assert.Equal(10_000, ledgerAfter.ConfirmedImpressions);

        // 差额存在，且计入交付
        var adjustment = await Db.Adjustments.SingleAsync(a => a.ContractId == contract.Id);
        Assert.Equal(4_000, adjustment.DeltaImpressions);
        Assert.Equal(AdjustmentReasons.LateArrival, adjustment.Reason);

        var rollup = await Accounting.GetRollup(contract.Id);
        Assert.Equal(10_000, rollup.Confirmed);
        Assert.Equal(0, rollup.Unconfirmed);
        Assert.Equal(4_000, rollup.LateAdjustments);
        Assert.Equal(14_000, rollup.Delivered);
    }

    [Fact]
    public async Task LateDataForUnconfirmedDate_CountsAsUnconfirmed_NotAdjustment()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        await Imports.ImportAsync(new ImportRequest("B-1", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 10_000) }));
        await Settlement.ClosePeriodAsync(contract.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 1), "settle"));

        // 8/5 尚未确认 → 正常计入未确认，不产生差额
        var result = await Imports.ImportAsync(new ImportRequest("B-2", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 5, 0), 7_000) }));

        Assert.Equal(0, result.LateAdjustmentsGenerated);
        Assert.Equal(0, await Db.Adjustments.CountAsync());

        var rollup = await Accounting.GetRollup(contract.Id);
        Assert.Equal(10_000, rollup.Confirmed);
        Assert.Equal(7_000, rollup.Unconfirmed);
        Assert.Equal(17_000, rollup.Delivered);
    }

    [Fact]
    public async Task ClosePeriod_IsIdempotentForAlreadyConfirmedDates()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        await Imports.ImportAsync(new ImportRequest("B-1", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 10_000) }));

        var first = await Settlement.ClosePeriodAsync(contract.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 7), "settle"));
        var second = await Settlement.ClosePeriodAsync(contract.Id,
            new ClosePeriodRequest(new DateOnly(2026, 8, 7), "settle"));

        Assert.Equal(7, first);   // 8/1~8/7 全部冻结（无数据的日子记 0）
        Assert.Equal(0, second);  // 重复确认不再产生新行
        Assert.Equal(7, await Db.DailyLedgers.CountAsync());
    }
}
