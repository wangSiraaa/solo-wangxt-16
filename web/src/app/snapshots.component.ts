import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from './api.service';
import { Rollup, Snapshot, SnapshotDetail } from './models';

@Component({
  selector: 'app-snapshots',
  standalone: true,
  imports: [FormsModule, DecimalPipe, DatePipe, JsonPipe],
  template: `
    <div class="page-head">
      <div>
        <h1>快照回查</h1>
        <p class="muted">每次导入 / 确认账期 / 补量 / 撤销 / 折让都会落一份计算快照 —— 补量前后的缺口有据可查</p>
      </div>
      <select class="select" [ngModel]="contractId()" (ngModelChange)="pickContract($event)">
        @for (c of contracts(); track c.contractId) {
          <option [value]="c.contractId">{{ c.code }} · {{ c.advertiserName }}</option>
        }
      </select>
    </div>

    <div class="two-col left-narrow">
      <div class="card">
        <h3>核算历史</h3>
        <table>
          <thead><tr><th>版本</th><th>触发动作</th><th class="num">缺口</th><th>时间</th></tr></thead>
          <tbody>
            @for (s of snapshots(); track s.id) {
              <tr [class.sel]="detail()?.id === s.id" (click)="pick(s)">
                <td class="mono">v{{ s.version }}</td>
                <td class="mono small-text">{{ s.trigger }}</td>
                <td class="num"><b>{{ s.gap | number }}</b></td>
                <td class="muted small-text">{{ s.asOfUtc | date:'MM-dd HH:mm:ss' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      <div class="card">
        @if (detail(); as d) {
          <h3>v{{ d.version }} · {{ d.trigger }}</h3>
          <div class="stat-grid small">
            <div class="stat"><label>承诺</label><b>{{ d.committed | number }}</b></div>
            <div class="stat"><label>已确认</label><b>{{ d.confirmed | number }}</b></div>
            <div class="stat"><label>未确认</label><b>{{ d.unconfirmed | number }}</b></div>
            <div class="stat"><label>迟到差额</label><b>{{ d.lateAdjustments | number }}</b></div>
            <div class="stat"><label>补量</label><b>{{ d.makeGood | number }}</b></div>
            <div class="stat"><label>折让</label><b>{{ d.discount | number }}</b></div>
            <div class="stat gap"><label>当时缺口</label><b>{{ d.gap | number }}</b></div>
          </div>
          <h4>明细载荷（jsonb）</h4>
          <pre class="json">{{ d.payload | json }}</pre>
        } @else {
          <p class="muted pad">← 选择一个快照版本查看当时的缺口构成与明细</p>
        }
      </div>
    </div>
  `
})
export class SnapshotsComponent implements OnInit {
  private api = inject(ApiService);

  contracts = signal<Rollup[]>([]);
  contractId = signal('');
  snapshots = signal<Snapshot[]>([]);
  detail = signal<SnapshotDetail | null>(null);

  async ngOnInit(): Promise<void> {
    const contracts = await this.api.contracts();
    this.contracts.set(contracts);
    if (contracts.length > 0) {
      await this.pickContract(contracts[0].contractId);
    }
  }

  async pickContract(id: string): Promise<void> {
    this.contractId.set(id);
    this.detail.set(null);
    this.snapshots.set(await this.api.snapshots(id));
  }

  async pick(s: Snapshot): Promise<void> {
    this.detail.set(await this.api.snapshotDetail(s.id));
  }
}
