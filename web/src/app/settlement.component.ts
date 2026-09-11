import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from './api.service';
import { ApiError, MakeGoodPlan, PoolRow, Rollup } from './models';

@Component({
  selector: 'app-settlement',
  standalone: true,
  imports: [FormsModule, DecimalPipe, DatePipe],
  template: `
    <div class="page-head">
      <div>
        <h1>结算工作台</h1>
        <p class="muted">结算人员：确认账期（冻结台账）→ 对缺口决定补量或折让 · 撤销补量即释放占用</p>
      </div>
      <select class="select" [ngModel]="contractId()" (ngModelChange)="pickContract($event)">
        @for (c of contracts(); track c.contractId) {
          <option [value]="c.contractId">{{ c.code }} · {{ c.advertiserName }}</option>
        }
      </select>
    </div>

    @if (notice()) { <div class="alert ok-box">{{ notice() }}</div> }
    @if (error(); as e) { <div class="alert error"><b>{{ e.code }}</b>：{{ e.message }}</div> }

    @if (rollup(); as r) {
      <div class="stat-grid">
        <div class="stat"><label>承诺量</label><b>{{ r.committed | number }}</b></div>
        <div class="stat"><label>已交付</label><b>{{ r.delivered | number }}</b></div>
        <div class="stat"><label>补量占用</label><b class="info-text">+{{ r.makeGood | number }}</b></div>
        <div class="stat"><label>折让当量</label><b>+{{ r.discount | number }}</b></div>
        <div class="stat gap"><label>剩余缺口</label><b>{{ r.gap | number }}</b></div>
      </div>

      <div class="two-col">
        <div>
          <div class="card">
            <h3>① 确认账期</h3>
            <p class="hint">把截止日（含）前的归日冻结进台账；之后迟到的数据只生成差额。</p>
            <div class="form-row">
              <label>确认截止日</label>
              <input type="date" [(ngModel)]="closeThrough" [min]="r.periodStart" [max]="r.periodEnd">
              <button class="btn primary" (click)="closePeriod()" [disabled]="busy()">确认账期</button>
            </div>
          </div>

          <div class="card">
            <h3>② 发起补量（占用资源池曝光）</h3>
            <p class="hint">同一笔池内曝光不能被两个合同同时占用；超量申请会被拒绝。</p>
            <table>
              <thead><tr><th></th><th>资源池</th><th>归日</th><th class="num">可用</th><th class="num">本次划拨</th></tr></thead>
              <tbody>
                @for (p of pool(); track p.slotCode + p.serviceDate) {
                  <tr>
                    <td><input type="checkbox" [checked]="isPicked(p)" (change)="togglePick(p)"></td>
                    <td><b class="mono">{{ p.slotCode }}</b><br><small class="muted">{{ p.slotName }}</small></td>
                    <td class="mono">{{ p.serviceDate }}</td>
                    <td class="num">{{ p.availableImpressions | number }}</td>
                    <td class="num">
                      <input class="num-input" type="number" min="0" [max]="p.availableImpressions"
                             [disabled]="!isPicked(p)"
                             [ngModel]="pickAmount(p)"
                             (ngModelChange)="setPickAmount(p, $event)">
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="muted">资源池为空（未挂在生效合同下的广告位曝光才会入池）</td></tr>
                }
              </tbody>
            </table>
            <div class="form-row">
              <input class="grow" placeholder="补量事由" [(ngModel)]="makeGoodReason">
              <button class="btn primary" (click)="createMakeGood()"
                      [disabled]="busy() || totalPicked() === 0">
                创建计划（共 {{ totalPicked() | number }}）
              </button>
            </div>
          </div>

          <div class="card">
            <h3>③ 或者折让了结</h3>
            <div class="form-row">
              <input class="num-input" type="number" min="1" placeholder="折让当量(曝光)" [(ngModel)]="discountImpressions">
              <input class="num-input" type="number" min="0" placeholder="金额(可选)" [(ngModel)]="discountAmount">
              <input class="grow" placeholder="折让说明" [(ngModel)]="discountReason">
              <button class="btn primary" (click)="createDiscount()" [disabled]="busy()">登记折让</button>
            </div>
          </div>
        </div>

        <div class="card">
          <h3>补量计划（{{ plans().length }}）</h3>
          @for (p of plans(); track p.id) {
            <div class="plan" [class.cancelled]="p.status === 'Cancelled'">
              <div class="plan-head">
                <b>{{ p.plannedImpressions | number }} 次曝光</b>
                @if (p.status === 'Active') { <span class="badge info">生效中</span> }
                @else { <span class="badge neutral">已撤销 · 占用已释放</span> }
                <span class="spacer"></span>
                @if (p.status === 'Active') {
                  <button class="btn small danger-btn" (click)="cancelPlan(p)" [disabled]="busy()">撤销</button>
                }
              </div>
              <div class="muted small-text">{{ p.reason }} · {{ p.createdBy }} · {{ p.createdAtUtc | date:'yyyy-MM-dd HH:mm' }}</div>
              @for (a of p.allocations; track a.id) {
                <div class="alloc">
                  {{ a.sourceSlotCode }} · {{ a.sourceServiceDate }} · {{ a.allocatedImpressions | number }}
                  @if (a.status === 'Released') { <span class="badge neutral">已释放</span> }
                </div>
              }
            </div>
          } @empty {
            <p class="muted">暂无补量计划</p>
          }
        </div>
      </div>
    }
  `
})
export class SettlementComponent implements OnInit {
  private api = inject(ApiService);

  contracts = signal<Rollup[]>([]);
  contractId = signal('');
  rollup = signal<Rollup | null>(null);
  pool = signal<PoolRow[]>([]);
  plans = signal<MakeGoodPlan[]>([]);
  error = signal<ApiError | null>(null);
  notice = signal('');
  busy = signal(false);

  closeThrough = '';
  makeGoodReason = '账期缺口补量';
  discountImpressions: number | null = null;
  discountAmount: number | null = null;
  discountReason = '客户接受折让了结缺口';

  private picks = signal(new Map<string, number>());

  totalPicked = computed(() => {
    let sum = 0;
    for (const v of this.picks().values()) sum += v;
    return sum;
  });

  async ngOnInit(): Promise<void> {
    const contracts = await this.api.contracts();
    this.contracts.set(contracts);
    if (contracts.length > 0) {
      await this.pickContract(contracts[0].contractId);
    }
  }

  async pickContract(id: string): Promise<void> {
    this.contractId.set(id);
    this.picks.set(new Map());
    await this.refresh();
    const r = this.rollup();
    this.closeThrough = r?.periodEnd ?? '';
  }

  async refresh(): Promise<void> {
    const id = this.contractId();
    if (!id) return;
    const [rollup, pool, plans] = await Promise.all([
      this.api.contract(id), this.api.pool(), this.api.makeGoods(id)
    ]);
    this.rollup.set(rollup);
    this.pool.set(pool.filter(p => p.availableImpressions > 0 || this.isPicked(p)));
    this.plans.set(plans);
  }

  // ---------- 勾选划拨 ----------
  key(p: PoolRow): string {
    return `${p.slotCode}|${p.serviceDate}`;
  }
  isPicked(p: PoolRow): boolean {
    return this.picks().has(this.key(p));
  }
  pickAmount(p: PoolRow): number {
    return this.picks().get(this.key(p)) ?? 0;
  }
  togglePick(p: PoolRow): void {
    const m = new Map(this.picks());
    if (m.has(this.key(p))) m.delete(this.key(p));
    else m.set(this.key(p), p.availableImpressions);
    this.picks.set(m);
  }
  setPickAmount(p: PoolRow, v: number): void {
    const m = new Map(this.picks());
    m.set(this.key(p), Math.max(0, Math.min(p.availableImpressions, Number(v) || 0)));
    this.picks.set(m);
  }

  // ---------- 动作 ----------
  private async run(action: () => Promise<string>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set('');
    try {
      this.notice.set(await action());
      await this.refresh();
    } catch (e) {
      this.error.set(e as ApiError);
    } finally {
      this.busy.set(false);
    }
  }

  closePeriod(): Promise<void> {
    return this.run(async () => {
      const r = await this.api.closePeriod(this.contractId(), this.closeThrough || null, 'settle.li');
      return `已确认 ${r.closedDays} 个归日的台账（快照已留痕）`;
    });
  }

  createMakeGood(): Promise<void> {
    return this.run(async () => {
      const allocations = this.pool()
        .filter(p => this.isPicked(p) && this.pickAmount(p) > 0)
        .map(p => ({
          sourceSlotCode: p.slotCode,
          sourceServiceDate: p.serviceDate,
          impressions: this.pickAmount(p)
        }));
      const plan = await this.api.createMakeGood(
        this.contractId(), this.makeGoodReason, 'settle.li', allocations);
      this.picks.set(new Map());
      return `补量计划已创建：${plan.plannedImpressions.toLocaleString()} 次曝光（占用资源池）`;
    });
  }

  cancelPlan(p: MakeGoodPlan): Promise<void> {
    return this.run(async () => {
      await this.api.cancelMakeGood(p.id);
      return `计划已撤销，${p.plannedImpressions.toLocaleString()} 次曝光的占用已释放回资源池`;
    });
  }

  createDiscount(): Promise<void> {
    return this.run(async () => {
      if (!this.discountImpressions || this.discountImpressions <= 0) {
        throw { code: 'INVALID', message: '请填写折让当量（正数）' } satisfies ApiError;
      }
      await this.api.createDiscount(
        this.contractId(), this.discountImpressions, this.discountAmount,
        this.discountReason, 'settle.li');
      this.discountImpressions = null;
      this.discountAmount = null;
      return '折让已登记（快照已留痕）';
    });
  }
}
