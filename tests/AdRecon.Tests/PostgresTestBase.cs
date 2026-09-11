using AdRecon.Api.Data;
using AdRecon.Api.Domain;
using AdRecon.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AdRecon.Tests;

/// <summary>
/// 每个测试类一个独立数据库（连本机真实 PostgreSQL），
/// 保证唯一约束、事务、jsonb 等行为与生产一致。
/// </summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    private const string AdminConn =
        "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres";

    private readonly string _dbName =
        "adrecon_test_" + Guid.NewGuid().ToString("N")[..12];

    protected AdReconDbContext Db = null!;
    protected AccountingService Accounting = null!;
    protected ImportService Imports = null!;
    protected SettlementService Settlement = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConn))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_dbName}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AdReconDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port=5432;Database={_dbName};Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        Db = new AdReconDbContext(options);
        await Db.Database.EnsureCreatedAsync();

        Accounting = new AccountingService(Db);
        Imports = new ImportService(Db, Accounting);
        Settlement = new SettlementService(Db, Accounting);
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await using var admin = new NpgsqlConnection(AdminConn);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 测试数据助手 ----------
    protected async Task<(Contract contract, AdSlot slot)> SeedContractWithSlot(
        string contractCode, string slotCode, string timeZone,
        long committed, DateOnly start, DateOnly end)
    {
        var slot = new AdSlot
        {
            Id = Guid.NewGuid(), Code = slotCode,
            Name = slotCode, TimeZoneId = timeZone
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(), Code = contractCode, AdvertiserName = "测试客户",
            CommittedImpressions = committed, PeriodStart = start, PeriodEnd = end
        };
        Db.AdSlots.Add(slot);
        Db.Contracts.Add(contract);
        Db.ContractSlots.Add(new ContractSlot { ContractId = contract.Id, SlotId = slot.Id });
        await Db.SaveChangesAsync();
        return (contract, slot);
    }

    protected async Task<AdSlot> SeedPoolSlot(string code, string timeZone)
    {
        var slot = new AdSlot
        {
            Id = Guid.NewGuid(), Code = code, Name = code, TimeZoneId = timeZone
        };
        Db.AdSlots.Add(slot);
        await Db.SaveChangesAsync();
        return slot;
    }

    protected static DateTime Utc(int y, int m, int d, int h)
        => new(y, m, d, h, 0, 0, DateTimeKind.Utc);
}
