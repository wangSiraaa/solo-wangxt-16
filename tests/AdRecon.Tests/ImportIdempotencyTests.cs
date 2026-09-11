using AdRecon.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Tests;

/// <summary>同一导入批次重传不得叠加；不同批次的重复行也不得叠加。</summary>
public class ImportIdempotencyTests : PostgresTestBase
{
    [Fact]
    public async Task SameBatchKeyReimport_DoesNotDoubleCount()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        var request = new ImportRequest("B-1", "ADX", "ops", null,
            new List<ImportRowRequest>
            {
                new("SLOT-A", Utc(2026, 8, 1, 0), 1_000),
                new("SLOT-A", Utc(2026, 8, 1, 1), 2_000)
            });

        var first = await Imports.ImportAsync(request);
        Assert.False(first.AlreadyExisted);
        Assert.Equal(2, first.AcceptedCount);

        // 整批重传：幂等返回首次结果，事实表不增加
        var second = await Imports.ImportAsync(request);
        Assert.True(second.AlreadyExisted);

        var factCount = await Db.ExposureFacts.CountAsync();
        Assert.Equal(2, factCount);

        var rollup = await Accounting.GetRollup(contract.Id);
        Assert.Equal(3_000, rollup.Delivered);
    }

    [Fact]
    public async Task DifferentBatchWithSameRows_RowLevelDedup()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        await Imports.ImportAsync(new ImportRequest("B-1", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 1_000) }));

        // 换了批次键，但 (来源,广告位,小时) 相同 → 行级去重
        var retry = await Imports.ImportAsync(new ImportRequest("B-1-RETRY", "ADX", "ops", null,
            new List<ImportRowRequest>
            {
                new("SLOT-A", Utc(2026, 8, 1, 0), 9_999), // 重复行：被吞
                new("SLOT-A", Utc(2026, 8, 1, 1), 500)    // 新行：接受
            }));

        Assert.False(retry.AlreadyExisted);
        Assert.Equal(1, retry.AcceptedCount);
        Assert.Equal(1, retry.DuplicateCount);

        var rollup = await Accounting.GetRollup(contract.Id);
        Assert.Equal(1_500, rollup.Delivered); // 1_000 + 500，9_999 未叠加
    }

    [Fact]
    public async Task SameHourFromDifferentSources_BothAccepted()
    {
        var (contract, _) = await SeedContractWithSlot(
            "C-1", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        await Imports.ImportAsync(new ImportRequest("B-ADX", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 1_000) }));
        await Imports.ImportAsync(new ImportRequest("B-SSP", "SSP", "ops", null,
            new List<ImportRowRequest> { new("SLOT-A", Utc(2026, 8, 1, 0), 2_000) }));

        var rollup = await Accounting.GetRollup(contract.Id);
        Assert.Equal(3_000, rollup.Delivered);
    }
}
