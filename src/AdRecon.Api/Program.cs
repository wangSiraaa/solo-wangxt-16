using AdRecon.Api.Contracts;
using AdRecon.Api.Data;
using AdRecon.Api.Seed;
using AdRecon.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("AdRecon")
    ?? "Host=127.0.0.1;Port=5432;Database=adrecon;Username=postgres";

builder.Services.AddDbContext<AdReconDbContext>(o =>
    o.UseNpgsql(connStr).UseSnakeCaseNamingConvention());
builder.Services.AddScoped<AccountingService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// 启动时建表（演示环境用 EnsureCreated；生产应迁移到 EF Migrations）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdReconDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// 统一业务异常 → JSON 错误
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ApiException ex)
    {
        ctx.Response.StatusCode = ex.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message));
    }
});

var api = app.MapGroup("/api");

// ---------- 合同与核算 ----------
api.MapGet("/contracts", async (AccountingService svc, CancellationToken ct)
    => Results.Ok(await svc.GetRollups(ct)));

api.MapGet("/contracts/{id:guid}", async (Guid id, AccountingService svc, CancellationToken ct)
    => Results.Ok(await svc.GetRollup(id, ct)));

api.MapGet("/contracts/{id:guid}/daily", async (Guid id, AccountingService svc, CancellationToken ct)
    => Results.Ok(await svc.GetDaily(id, ct)));

// ---------- 钻取：合同 → 广告位 → 批次 → 小时行 ----------
api.MapGet("/contracts/{id:guid}/slots", async (Guid id, AccountingService svc, CancellationToken ct)
    => Results.Ok(await svc.GetSlotRollup(id, ct)));

api.MapGet("/contracts/{id:guid}/slots/{slotId:guid}/batches",
    async (Guid id, Guid slotId, AccountingService svc, CancellationToken ct)
        => Results.Ok(await svc.GetSlotBatches(id, slotId, ct)));

api.MapGet("/batches/{batchId:guid}/rows",
    async (Guid batchId, AccountingService svc, CancellationToken ct)
        => Results.Ok(await svc.GetBatchRows(batchId, ct)));

api.MapGet("/batches", async (AdReconDbContext db, CancellationToken ct)
    => Results.Ok(await db.ImportBatches
        .OrderByDescending(b => b.ImportedAtUtc)
        .Select(b => new
        {
            b.Id, b.BatchKey, b.Source, b.ImportedBy, b.ImportedAtUtc,
            b.RowCount, b.AcceptedCount, b.DuplicateCount,
            Status = b.Status.ToString(), b.Note
        })
        .ToListAsync(ct)));

// ---------- 快照（可回查依据） ----------
api.MapGet("/contracts/{id:guid}/snapshots",
    async (Guid id, AdReconDbContext db, CancellationToken ct)
        => Results.Ok(await db.CalcSnapshots
            .Where(s => s.ContractId == id)
            .OrderByDescending(s => s.Version)
            .Select(s => new SnapshotDto(
                s.Id, s.Version, s.AsOfUtc, s.Trigger,
                s.CommittedImpressions, s.ConfirmedImpressions, s.UnconfirmedImpressions,
                s.AdjustmentImpressions, s.MakeGoodImpressions, s.DiscountImpressions,
                s.DeliveredImpressions, s.GapImpressions))
            .ToListAsync(ct)));

api.MapGet("/snapshots/{snapshotId:guid}",
    async (Guid snapshotId, AdReconDbContext db, CancellationToken ct) =>
    {
        var s = await db.CalcSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == snapshotId, ct);
        if (s is null) return Results.NotFound(new ApiError("SNAPSHOT_NOT_FOUND", "快照不存在"));
        var payload = System.Text.Json.JsonSerializer.Deserialize<object>(s.PayloadJson);
        return Results.Ok(new SnapshotDetailDto(
            s.Id, s.Version, s.AsOfUtc, s.Trigger,
            s.CommittedImpressions, s.ConfirmedImpressions, s.UnconfirmedImpressions,
            s.AdjustmentImpressions, s.MakeGoodImpressions, s.DiscountImpressions,
            s.DeliveredImpressions, s.GapImpressions, payload!));
    });

// ---------- 迟到差额 ----------
api.MapGet("/contracts/{id:guid}/adjustments",
    async (Guid id, AdReconDbContext db, CancellationToken ct)
        => Results.Ok(await db.Adjustments
            .Where(a => a.ContractId == id)
            .OrderBy(a => a.ServiceDate)
            .Select(a => new AdjustmentDto(
                a.Id, a.ServiceDate, a.DeltaImpressions, a.Reason,
                a.BatchId, a.Batch.BatchKey, a.CreatedAtUtc))
            .ToListAsync(ct)));

// ---------- 导入（幂等） ----------
api.MapPost("/imports", async (ImportRequest req, ImportService svc, CancellationToken ct)
    => Results.Ok(await svc.ImportAsync(req, ct)));

// ---------- 结算动作 ----------
api.MapPost("/contracts/{id:guid}/close-period",
    async (Guid id, ClosePeriodRequest req, SettlementService svc, CancellationToken ct) =>
    {
        var closed = await svc.ClosePeriodAsync(id, req, ct);
        return Results.Ok(new { closedDays = closed });
    });

api.MapGet("/contracts/{id:guid}/makegoods",
    async (Guid id, SettlementService svc, CancellationToken ct)
        => Results.Ok(await svc.GetPlansAsync(id, ct)));

api.MapPost("/contracts/{id:guid}/makegoods",
    async (Guid id, CreateMakeGoodRequest req, SettlementService svc, CancellationToken ct)
        => Results.Ok(await svc.CreateMakeGoodAsync(id, req, ct)));

api.MapPost("/makegoods/{planId:guid}/cancel",
    async (Guid planId, SettlementService svc, CancellationToken ct)
        => Results.Ok(await svc.CancelMakeGoodAsync(planId, ct)));

api.MapGet("/makegood-pool",
    async (SettlementService svc, CancellationToken ct)
        => Results.Ok(await svc.GetPoolAsync(ct)));

api.MapGet("/contracts/{id:guid}/discounts",
    async (Guid id, AdReconDbContext db, CancellationToken ct)
        => Results.Ok(await db.Discounts
            .Where(d => d.ContractId == id)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Select(d => new DiscountDto(d.Id, d.Impressions, d.Amount, d.Reason, d.CreatedBy, d.CreatedAtUtc))
            .ToListAsync(ct)));

api.MapPost("/contracts/{id:guid}/discounts",
    async (Guid id, CreateDiscountRequest req, SettlementService svc, CancellationToken ct)
        => Results.Ok(await svc.CreateDiscountAsync(id, req, ct)));

// ---------- 广告位与演示数据 ----------
api.MapGet("/slots", async (AdReconDbContext db, CancellationToken ct)
    => Results.Ok(await db.AdSlots
        .OrderBy(s => s.Code)
        .Select(s => new { s.Id, s.Code, s.Name, s.TimeZoneId })
        .ToListAsync(ct)));

api.MapPost("/seed/demo", async (DemoSeeder seeder, CancellationToken ct) =>
{
    await seeder.ResetAndSeedAsync(ct);
    return Results.Ok(new { seeded = true });
});

// ---------- 托管 Angular 构建产物 ----------
app.UseDefaultFiles();
app.UseStaticFiles();
var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
if (File.Exists(indexPath))
    app.MapFallbackToFile("index.html");

app.Run();

// 供集成测试引用
public partial class Program;
