#!/usr/bin/env bash
# 端到端演示：跨时区归日 / 迟到日志差额 / 重复导入幂等 / 补量占用与释放
# 用法：API 运行在本机 5080 端口时执行  bash scripts/demo.sh
set -euo pipefail

API="${API:-http://127.0.0.1:5080/api}"
cd "$(dirname "$0")/.."

jqp() { python3 -c "import json,sys; d=json.load(sys.stdin); $1"; }

step() { printf '\n\033[1;36m━━ %s ━━\033[0m\n' "$1"; }

step "0. 重置并灌入演示数据（含跨时区/迟到/重复三类样本）"
curl -s -X POST "$API/seed/demo" > /dev/null && echo "seeded ✓"

step "1. 合同缺口基线（补量前）"
curl -s "$API/contracts" | jqp "
for c in d:
    print(f\"{c['code']} {c['advertiserName']}  承诺={c['committed']:,}  已交付={c['delivered']:,} (迟到差额 {c['lateAdjustments']:,})  补量={c['makeGood']:,}  折让={c['discount']:,}  缺口={c['gap']:,}\")"
A=$(curl -s "$API/contracts" | jqp "print([c['contractId'] for c in d if c['code']=='HT-2026-0801'][0])")
B=$(curl -s "$API/contracts" | jqp "print([c['contractId'] for c in d if c['code']=='HT-2026-0802'][0])")

step "2. 重复导入：同 batchKey 重传 → 幂等，不叠加"
curl -s -X POST "$API/imports" -H 'Content-Type: application/json' -d @samples/import-normal.json | jqp "
print(f\"首次导入: accepted={d['acceptedCount']} dup={d['duplicateCount']} alreadyExisted={d['alreadyExisted']}\")"
curl -s -X POST "$API/imports" -H 'Content-Type: application/json' -d @samples/import-duplicate.json | jqp "
print(f\"同键重传: accepted={d['acceptedCount']} dup={d['duplicateCount']} alreadyExisted={d['alreadyExisted']}  ← 整批幂等\")"

step "3. 重复导入：换批次键、相同行 → 行级去重"
curl -s -X POST "$API/imports" -H 'Content-Type: application/json' -d @samples/import-duplicate-rows.json | jqp "
print(f\"accepted={d['acceptedCount']} (仅新行)  dup={d['duplicateCount']} (被吞的重复行)\")"

step "4. 确认账期到 8/18（冻结台账）"
curl -s -X POST "$API/contracts/$A/close-period" -H 'Content-Type: application/json' \
  -d '{"throughDate":"2026-08-18","operatorName":"settle.li"}' | jqp "print('合同A 新冻结', d['closedDays'], '天')"
curl -s -X POST "$API/contracts/$B/close-period" -H 'Content-Type: application/json' \
  -d '{"throughDate":"2026-08-18","operatorName":"settle.li"}' | jqp "print('合同B 新冻结', d['closedDays'], '天')"

step "5. 迟到日志补传（8/12、8/15、8/18 落在已确认账期）→ 只生成差额"
curl -s -X POST "$API/imports" -H 'Content-Type: application/json' -d @samples/import-late.json | jqp "
print(f\"accepted={d['acceptedCount']}  生成迟到差额={d['lateAdjustmentsGenerated']} 笔  ← 台账未被覆盖\")"
echo "合同A 迟到差额明细："
curl -s "$API/contracts/$A/adjustments" | jqp "
for a in d: print(f\"  {a['serviceDate']}  +{a['deltaImpressions']:,}  来自批次 {a['batchKey']}\")"

step "6. 补量曝光不能同时冲抵两个合同"
echo "资源池现状："
curl -s "$API/makegood-pool" | jqp "
for p in d: print(f\"  {p['slotCode']} {p['serviceDate']}  总量={p['totalImpressions']:,}  已占用={p['occupiedImpressions']:,}  可用={p['availableImpressions']:,}\")"
echo "→ 合同B 申请 9/1 的 200,000（合同A 已占用 40,000，可用不足）："
curl -s -X POST "$API/contracts/$B/makegoods" -H 'Content-Type: application/json' \
  -d '{"reason":"试图超额占用","createdBy":"settle.li","allocations":[{"sourceSlotCode":"POOL-REMNANT","sourceServiceDate":"2026-09-01","impressions":200000}]}' | jqp "
print(f\"  拒绝 [{d.get('code','?')}] {d.get('message', d)}\")"

step "7. 合同B 正常补量 60,000 → 撤销 → 占用释放"
RESP=$(curl -s -X POST "$API/contracts/$B/makegoods" -H 'Content-Type: application/json' \
  -d '{"reason":"8月缺口补量","createdBy":"settle.li","allocations":[{"sourceSlotCode":"POOL-REMNANT","sourceServiceDate":"2026-09-02","impressions":60000}]}')
PLAN=$(printf '%s' "$RESP" | jqp "print(d.get('id',''))")
if [ -z "$PLAN" ]; then
  echo "创建补量计划失败：$RESP" >&2
  exit 1
fi
echo "创建计划 $PLAN（占用 9/2 的 60,000）"
curl -s "$API/makegood-pool" | jqp "
p=[x for x in d if x['serviceDate']=='2026-09-02'][0]
print(f\"  9/2 可用降至 {p['availableImpressions']:,}\")"
curl -s -X POST "$API/makegoods/$PLAN/cancel" > /dev/null
echo "撤销后："
curl -s "$API/makegood-pool" | jqp "
p=[x for x in d if x['serviceDate']=='2026-09-02'][0]
print(f\"  9/2 可用恢复 {p['availableImpressions']:,}  ← 占用已释放\")"

step "8. 合同A 快照轨迹（补量前后缺口可回查）"
curl -s "$API/contracts/$A/snapshots" | jqp "
for s in sorted(d, key=lambda x: x['version']):
    print(f\"  v{s['version']:>2} {s['trigger']:<30} 缺口={s['gap']:>9,}  补量={s['makeGood']:>7,}\")"

step "完成"
echo "浏览器打开 http://127.0.0.1:5080 查看钻取界面"
