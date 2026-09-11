using AdRecon.Api.Contracts;
using AdRecon.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AdRecon.Tests;

/// <summary>补量曝光不能同时冲抵两个合同；撤销计划必须释放占用。</summary>
public class MakeGoodTests : PostgresTestBase
{
    private async Task<(Guid contractA, Guid contractB)> SeedTwoContractsAndPool()
    {
        var a = await SeedContractWithSlot("C-A", "SLOT-A", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        var b = await SeedContractWithSlot("C-B", "SLOT-B", "Asia/Shanghai", 100_000,
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        await SeedPoolSlot("POOL", "Asia/Shanghai");

        // 资源池 9/1 共 60,000 可用
        await Imports.ImportAsync(new ImportRequest("B-POOL", "ADX", "ops", null,
            new List<ImportRowRequest> { new("POOL", Utc(2026, 9, 1, 0), 60_000) }));
        return (a.contract.Id, b.contract.Id);
    }

    [Fact]
    public async Task SamePoolImpressions_CannotOffsetTwoContracts()
    {
        var (contractA, contractB) = await SeedTwoContractsAndPool();
        var day = new DateOnly(2026, 9, 1);

        // 合同A 占用 50,000（池内共 60,000）
        await Settlement.CreateMakeGoodAsync(contractA,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 50_000) }));

        // 合同B 再要 30,000：只剩 10,000 可用 → 拒绝，防止一量两抵
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Settlement.CreateMakeGoodAsync(contractB,
                new CreateMakeGoodRequest("补量", "settle",
                    new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 30_000) })));
        Assert.Equal("POOL_EXHAUSTED", ex.Code);

        // 合同B 要 10,000（恰好剩余量）→ 允许：不同曝光，各抵各的
        var planB = await Settlement.CreateMakeGoodAsync(contractB,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 10_000) }));
        Assert.Equal("Active", planB.Status);

        var pool = await Settlement.GetPoolAsync();
        Assert.Equal(0, pool.Single(p => p.ServiceDate == day).AvailableImpressions);
    }

    [Fact]
    public async Task CancelPlan_ReleasesOccupation()
    {
        var (contractA, contractB) = await SeedTwoContractsAndPool();
        var day = new DateOnly(2026, 9, 1);

        var plan = await Settlement.CreateMakeGoodAsync(contractA,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 60_000) }));

        // 池子被占满
        Assert.Equal(0, (await Settlement.GetPoolAsync()).Single(p => p.ServiceDate == day)
            .AvailableImpressions);

        // 撤销 → 占用释放
        var cancelled = await Settlement.CancelMakeGoodAsync(plan.Id);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.All(cancelled.Allocations, a => Assert.Equal("Released", a.Status));

        // 合同B 现在可以用同一批曝光
        await Settlement.CreateMakeGoodAsync(contractB,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 60_000) }));

        var rollupB = await Accounting.GetRollup(contractB);
        Assert.Equal(60_000, rollupB.MakeGood);
    }

    [Fact]
    public async Task MakeGoodReducesGap_AndCancelRestoresIt()
    {
        var (contractA, _) = await SeedTwoContractsAndPool();
        var day = new DateOnly(2026, 9, 1);

        var before = await Accounting.GetRollup(contractA);
        Assert.Equal(100_000, before.Gap);

        var plan = await Settlement.CreateMakeGoodAsync(contractA,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 40_000) }));

        var after = await Accounting.GetRollup(contractA);
        Assert.Equal(60_000, after.Gap);
        Assert.Equal(40_000, after.MakeGood);

        await Settlement.CancelMakeGoodAsync(plan.Id);
        var restored = await Accounting.GetRollup(contractA);
        Assert.Equal(100_000, restored.Gap);
        Assert.Equal(0, restored.MakeGood);
    }

    [Fact]
    public async Task SlotAttachedToActiveContract_CannotBeUsedAsMakeGoodSource()
    {
        var (contractA, contractB) = await SeedTwoContractsAndPool();

        // SLOT-B 挂在合同B 下，其曝光已计入合同B 的自然交付
        await Imports.ImportAsync(new ImportRequest("B-NAT", "ADX", "ops", null,
            new List<ImportRowRequest> { new("SLOT-B", Utc(2026, 8, 1, 0), 20_000) }));

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Settlement.CreateMakeGoodAsync(contractA,
                new CreateMakeGoodRequest("挪用", "settle",
                    new List<CreateMakeGoodAllocationRequest>
                    {
                        new("SLOT-B", new DateOnly(2026, 8, 1), 20_000)
                    })));
        Assert.Equal("SLOT_NOT_POOL", ex.Code);
    }

    [Fact]
    public async Task EveryMutation_WritesAuditableSnapshot()
    {
        var (contractA, _) = await SeedTwoContractsAndPool();
        var day = new DateOnly(2026, 9, 1);

        var plan = await Settlement.CreateMakeGoodAsync(contractA,
            new CreateMakeGoodRequest("补量", "settle",
                new List<CreateMakeGoodAllocationRequest> { new("POOL", day, 40_000) }));
        await Settlement.CancelMakeGoodAsync(plan.Id);

        var snapshots = await Db.CalcSnapshots
            .Where(s => s.ContractId == contractA)
            .OrderBy(s => s.Version)
            .ToListAsync();

        // 资源池导入(不属任何合同)不触发快照；补量创建 + 撤销各一份
        Assert.Equal(2, snapshots.Count);
        Assert.Contains(snapshots, s => s.Trigger.StartsWith("MakeGoodCreated") && s.MakeGoodImpressions == 40_000);
        Assert.Contains(snapshots, s => s.Trigger.StartsWith("MakeGoodCancelled") && s.MakeGoodImpressions == 0);
        Assert.All(snapshots, s => Assert.False(string.IsNullOrWhiteSpace(s.PayloadJson)));
    }
}
