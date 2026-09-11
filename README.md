# 包量投放曝光核算与补量系统（AdRecon）

广告代理公司包量投放的对账底座：解决**日志补传导致缺口算错**的核心痛点。
Angular 钻取界面 + ASP.NET Core 核算服务 + PostgreSQL（原始批次 / 去重键 / 计算快照）。

## 核算口径

```
已交付 Delivered = 已确认台账 Confirmed + 未确认在途 Unconfirmed + 迟到差额 LateAdjustments
剩余缺口 Gap     = 承诺量 Committed - Delivered - 补量占用 MakeGood(Active) - 折让当量 Discount
```

## 业务规则 → 实现映射

| 规则 | 实现 |
|---|---|
| 曝光按广告位所属时区归日 | `ServiceDateCalculator`（NodaTime 自带 tzdb），导入时算出 `service_date` 冗余落库 |
| 同一导入批次重传不得叠加 | 批次级：`import_batches.batch_key` 唯一，同键直接返回首次结果；行级：`exposure_facts.dedup_key`（来源\|广告位\|UTC小时）唯一，重复行吞掉计数；唯一索引兜底并发 |
| 迟到数据生成差额而非覆盖账期 | 归日已冻结（`daily_ledgers` 有记录）→ 只写 `adjustments`，台账行永不更新 |
| 补量曝光不能同时冲抵两个合同 | 资源池 = 未挂任何生效合同的广告位曝光；同一 (广告位,归日) 的 Active 占用总量 ≤ 池内曝光量（`POOL_EXHAUSTED`）；挂在生效合同下的广告位禁止挪用（`SLOT_NOT_POOL`） |
| 撤销补量释放占用 | 计划 `Cancelled` + 划拨 `Released`，池内可用量即时回升 |
| 缺口可回查 | 每次导入/确认/补量/撤销/折让都写 `calc_snapshots`（含 jsonb 明细载荷） |

## 角色与页面

- **客户经理** · 合同总览：承诺 vs 已交付构成、补量/折让、剩余缺口、完成度
- **投放人员** · 曝光导入：按小时汇总 JSON 导入，实时反馈接受/去重/迟到差额笔数
- **结算人员** · 结算工作台：确认账期（冻结台账）→ 对缺口发起补量（勾选资源池）或折让，可撤销
- **钻取**：合同 → 广告位 → 导入批次 → 小时行（UTC 小时与归日并列，跨时区行高亮）
- **快照回查**：每个核算版本的缺口构成与 jsonb 明细

## 快速开始

```bash
bash scripts/start.sh          # 启动 PostgreSQL(5432) + API(5080)，自动建表
curl -X POST http://127.0.0.1:5080/api/seed/demo   # 灌入演示数据
# 打开 http://127.0.0.1:5080
```

开发模式前端热更新：`cd web && npm install && ./node_modules/.bin/ng serve`

## 演示脚本与样本

```bash
bash scripts/demo.sh           # 端到端演示全部场景（自动重置数据）
```

`samples/` 下的导入样本：

| 文件 | 场景 |
|---|---|
| `import-normal.json` | 正常导入（第二日志管线 SSP-LOG） |
| `import-duplicate.json` | 同 batchKey 整批重传 → 幂等 |
| `import-duplicate-rows.json` | 换批次键、相同行 → 行级去重 |
| `import-late.json` | 迟到补传（落在已确认账期 → 只生成差额） |

种子数据内置三类问题样本：**跨时区**（纽约 UTC 02:00 归前一日；凌晨 UTC 小时归上海次日）、
**迟到日志**（8/3 分片补传 → 差额）、**重复导入**（整批重传 + 行级重复）。

## 测试

```bash
dotnet test tests/AdRecon.Tests     # 16 项：时区归日 / 幂等 / 去重 / 迟到差额 / 补量独占与释放 / 快照
```

测试直连本机真实 PostgreSQL（每测试类独立建库），保证唯一约束、事务、jsonb 行为与生产一致。

## 目录结构

```
src/AdRecon.Api/        ASP.NET Core 8 核算服务（Minimal API + EF Core/Npgsql）
  Domain/               实体：合同/广告位/批次/曝光事实/台账/差额/补量/折让/快照
  Services/             ImportService(幂等导入) AccountingService(核算+快照) SettlementService(结算)
  Seed/DemoSeeder.cs    演示场景编排（跨时区/迟到/重复/补量/撤销）
tests/AdRecon.Tests/    xUnit × 16（真实 PostgreSQL）
web/                    Angular 17（standalone 组件，构建产物由 API 托管）
samples/                导入样本 JSON
scripts/demo.sh         端到端演示脚本
scripts/start.sh        一键启动
```

## API 一览

```
GET  /api/contracts                                  合同列表（含缺口核算）
GET  /api/contracts/{id}/daily|slots|adjustments|snapshots|makegoods|discounts
GET  /api/contracts/{id}/slots/{slotId}/batches      钻取：广告位 → 批次
GET  /api/batches/{id}/rows                          钻取：批次 → 小时行
GET  /api/snapshots/{id}                             快照明细（jsonb 载荷）
POST /api/imports                                    幂等导入
POST /api/contracts/{id}/close-period                确认账期（冻结台账）
POST /api/contracts/{id}/makegoods                   创建补量（占用资源池）
POST /api/makegoods/{planId}/cancel                  撤销补量（释放占用）
POST /api/contracts/{id}/discounts                   登记折让
GET  /api/makegood-pool                              补量资源池（总量/占用/可用）
POST /api/seed/demo                                  重置并灌入演示数据
```

## 已知简化（后续路线）

- 建表用 `EnsureCreated`，生产应切换 EF Migrations 做版本化演进
- 补量容量校验在事务内完成，未加数据库排他约束；高并发划拨建议引入序列化或悲观锁
- 无认证鉴权；角色（客户经理/投放/结算）目前只是界面视角
