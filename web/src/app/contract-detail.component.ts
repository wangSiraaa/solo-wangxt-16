import { Component, Input, OnChanges, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, JsonPipe, PercentPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService } from './api.service';
import {
  Adjustment, BatchRollup, DailyRow, Discount, FactRow,
  MakeGoodPlan, Rollup, SlotRollup
} from './models';

type Tab = 'daily' | 'drill' | 'adjustments' | 'plans';

@Component({
  selector: 'app-contract-detail',
  standalone: true,
  imports: [RouterLink, DecimalPipe, PercentPipe, DatePipe, JsonPipe],
  template: `
    @if (rollup(); as r) {
      <div class="page-head">
        <div>
          <h1>{{ r.code }} <span class="muted">/ {{ r.advertiserName }}</span></h1>
          <p class="muted">账期 {{ r.periodStart }} ~ {{ r.periodEnd }} · 承诺 {{ r.committed | number }} 次曝光</p>
        </div>
        <a class="btn ghost" routerLink="/contracts">← 返回列表</a>
      </div>

      <div class="stat-grid">
        <div class="stat"><label>已确认（台账）</label><b>{{ r.confirmed | number }}</b></div>
        <div class="stat"><label>未确认（在途）</label><b>{{ r.unconfirmed | number }}</b></div>
        <div class="stat"><label>迟到差额</label><b class="warn-text">+{{ r.lateAdjustments | number }}</b></div>
        <div class="stat"><label>补量占用</label><b class="info-text">+{{ r.makeGood | number }}</b></div>
        <div class="stat"><label>折让当量</label><b>+{{ r.discount | number }}</b></div>
        <div class="stat gap"><label>剩余缺口</label><b>{{ r.gap | number }}</b></div>
      </div>

      <div class="tabs">
        <button [class.on]="tab() === 'daily'" (click)="tab.set('daily')">每日归集</button>
        <button [class.on]="tab() === 'drill'" (click)="tab.set('drill')">来源钻取</button>
        <button [class.on]="tab() === 'adjustments'" (click)="tab.set('adjustments')">
          迟到差额 @if (adjustments().length) { ({{ adjustments().length }}) }
        </button>
        <button [class.on]="tab() === 'plans'" (click)="tab.set('plans')">补量与折让</button>
      </div>

      @if (tab() === 'daily') {
        <div class="card">
          <table>
            <thead>
              <tr><th>归日（广告位时区）</th><th class="num">原始曝光</th><th class="num">已确认台账</th>
                  <th class="num">迟到差额</th><th>账期状态</th></tr>
            </thead>
            <tbody>
              @for (d of daily(); track d.serviceDate) {
                <tr>
                  <td class="mono">{{ d.serviceDate }}</td>
                  <td class="num">{{ d.rawImpressions | number }}</td>
                  <td class="num">{{ d.isConfirmed ? (d.confirmedImpressions | number) : '—' }}</td>
                  <td class="num">
                    @if (d.adjustmentImpressions > 0) { <span class="badge warn">+{{ d.adjustmentImpressions | number }}</span> }
                    @else { <span class="muted">—</span> }
                  </td>
                  <td>
                    @if (d.isConfirmed) { <span class="badge locked">已确认 · 冻结</span> }
                    @else { <span class="badge open">未确认</span> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (tab() === 'drill') {
        <div class="drill">
          <div class="card drill-col">
            <h3>① 广告位</h3>
            <table>
              <thead><tr><th>广告位</th><th>时区</th><th class="num">曝光</th></tr></thead>
              <tbody>
                @for (s of slots(); track s.slotId) {
                  <tr [class.sel]="selectedSlot()?.slotId === s.slotId" (click)="pickSlot(s)">
                    <td><b>{{ s.code }}</b><br><small class="muted">{{ s.name }}</small></td>
                    <td class="mono small-text">{{ s.timeZoneId }}</td>
                    <td class="num">{{ s.impressions | number }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <div class="card drill-col">
            <h3>② 导入批次</h3>
            @if (!selectedSlot()) { <p class="muted pad">← 先选一个广告位</p> }
            @else {
              <table>
                <thead><tr><th>批次</th><th class="num">行数</th><th class="num">曝光</th></tr></thead>
                <tbody>
                  @for (b of batches(); track b.batchId) {
                    <tr [class.sel]="selectedBatch()?.batchId === b.batchId" (click)="pickBatch(b)">
                      <td><b class="mono">{{ b.batchKey }}</b><br>
                          <small class="muted">{{ b.importedBy }} · {{ b.importedAtUtc | date:'MM-dd HH:mm' }}</small></td>
                      <td class="num">{{ b.rows }}</td>
                      <td class="num">{{ b.impressions | number }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
          <div class="card drill-col">
            <h3>③ 小时行（UTC → 归日）</h3>
            @if (!selectedBatch()) { <p class="muted pad">← 再选一个批次</p> }
            @else {
              <table>
                <thead><tr><th>UTC 小时</th><th>归日</th><th class="num">有效曝光</th></tr></thead>
                <tbody>
                  @for (f of facts(); track f.dedupKey) {
                    <tr [class.tz-shift]="f.hourUtc.slice(0, 10) !== f.serviceDate">
                      <td class="mono">{{ f.hourUtc | date:'yyyy-MM-dd HH:mm':'UTC' }}</td>
                      <td class="mono">{{ f.serviceDate }}</td>
                      <td class="num">{{ f.validImpressions | number }}</td>
                    </tr>
                  }
                </tbody>
              </table>
              <p class="hint">高亮行：UTC 日期与归日不同 —— 广告位时区换算的结果。</p>
            }
          </div>
        </div>
      }

      @if (tab() === 'adjustments') {
        <div class="card">
          <p class="hint">账期确认后补传的迟到数据：只记差额，不回写已冻结台账。</p>
          <table>
            <thead><tr><th>归日</th><th class="num">差额</th><th>原因</th><th>来源批次</th><th>入账时间</th></tr></thead>
            <tbody>
              @for (a of adjustments(); track a.id) {
                <tr>
                  <td class="mono">{{ a.serviceDate }}</td>
                  <td class="num warn-text">+{{ a.deltaImpressions | number }}</td>
                  <td><span class="badge warn">迟到补传</span></td>
                  <td class="mono">{{ a.batchKey }}</td>
                  <td class="muted">{{ a.createdAtUtc | date:'yyyy-MM-dd HH:mm:ss' }}</td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="muted">暂无迟到差额</td></tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (tab() === 'plans') {
        <div class="card">
          <h3>补量计划</h3>
          <table>
            <thead><tr><th>计划</th><th class="num">补量</th><th>状态</th><th>划拨明细</th><th>时间</th></tr></thead>
            <tbody>
              @for (p of plans(); track p.id) {
                <tr>
                  <td>{{ p.reason }}<br><small class="muted">{{ p.createdBy }}</small></td>
                  <td class="num">{{ p.plannedImpressions | number }}</td>
                  <td>
                    @if (p.status === 'Active') { <span class="badge info">生效中</span> }
                    @else if (p.status === 'Cancelled') { <span class="badge neutral">已撤销</span> }
                    @else { <span class="badge neutral">{{ p.status }}</span> }
                  </td>
                  <td>
                    @for (a of p.allocations; track a.id) {
                      <div class="alloc">
                        {{ a.sourceSlotCode }} · {{ a.sourceServiceDate }} · {{ a.allocatedImpressions | number }}
                        @if (a.status === 'Released') { <span class="badge neutral">已释放</span> }
                      </div>
                    }
                  </td>
                  <td class="muted small-text">
                    建 {{ p.createdAtUtc | date:'MM-dd HH:mm' }}
                    @if (p.cancelledAtUtc) { <br>撤 {{ p.cancelledAtUtc | date:'MM-dd HH:mm' }} }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="muted">暂无补量计划</td></tr>
              }
            </tbody>
          </table>

          <h3>折让</h3>
          <table>
            <thead><tr><th class="num">折让当量</th><th class="num">金额</th><th>说明</th><th>时间</th></tr></thead>
            <tbody>
              @for (d of discounts(); track d.id) {
                <tr>
                  <td class="num">{{ d.impressions | number }}</td>
                  <td class="num">{{ d.amount != null ? (d.amount | number) : '—' }}</td>
                  <td>{{ d.reason }}<br><small class="muted">{{ d.createdBy }}</small></td>
                  <td class="muted">{{ d.createdAtUtc | date:'MM-dd HH:mm' }}</td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="muted">暂无折让</td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    } @else {
      <p class="muted">加载中…</p>
    }
  `
})
export class ContractDetailComponent implements OnChanges {
  @Input({ required: true }) id!: string;

  private api = inject(ApiService);

  rollup = signal<Rollup | null>(null);
  daily = signal<DailyRow[]>([]);
  slots = signal<SlotRollup[]>([]);
  batches = signal<BatchRollup[]>([]);
  facts = signal<FactRow[]>([]);
  adjustments = signal<Adjustment[]>([]);
  plans = signal<MakeGoodPlan[]>([]);
  discounts = signal<Discount[]>([]);

  tab = signal<Tab>('daily');
  selectedSlot = signal<SlotRollup | null>(null);
  selectedBatch = signal<BatchRollup | null>(null);

  async ngOnChanges(): Promise<void> {
    const id = this.id;
    const [rollup, daily, slots, adjustments, plans, discounts] = await Promise.all([
      this.api.contract(id), this.api.daily(id), this.api.slots(id),
      this.api.adjustments(id), this.api.makeGoods(id), this.api.discounts(id)
    ]);
    this.rollup.set(rollup);
    this.daily.set(daily);
    this.slots.set(slots);
    this.adjustments.set(adjustments);
    this.plans.set(plans);
    this.discounts.set(discounts);
  }

  async pickSlot(slot: SlotRollup): Promise<void> {
    this.selectedSlot.set(slot);
    this.selectedBatch.set(null);
    this.facts.set([]);
    this.batches.set(await this.api.slotBatches(this.id, slot.slotId));
  }

  async pickBatch(batch: BatchRollup): Promise<void> {
    this.selectedBatch.set(batch);
    this.facts.set(await this.api.batchRows(batch.batchId));
  }
}
