import { Component, OnInit, inject, signal } from '@angular/core';
import { DecimalPipe, PercentPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService } from './api.service';
import { Rollup } from './models';

@Component({
  selector: 'app-contracts',
  standalone: true,
  imports: [RouterLink, DecimalPipe, PercentPipe],
  template: `
    <div class="page-head">
      <div>
        <h1>合同总览</h1>
        <p class="muted">客户经理视角：承诺量 vs 已交付（含迟到差额）与补量/折让后的剩余缺口</p>
      </div>
      <button class="btn ghost" (click)="reseed()" [disabled]="reseeding()">
        {{ reseeding() ? '正在重置…' : '重置演示数据' }}
      </button>
    </div>

    @if (error()) {
      <div class="alert error">{{ error() }}</div>
    }

    <div class="card">
      <table>
        <thead>
          <tr>
            <th>合同号</th><th>广告主</th><th>账期</th>
            <th class="num">承诺量</th><th class="num">已交付</th>
            <th class="num">其中迟到差额</th><th class="num">补量</th><th class="num">折让</th>
            <th class="num">剩余缺口</th><th style="width:180px">完成度</th><th></th>
          </tr>
        </thead>
        <tbody>
          @for (c of contracts(); track c.contractId) {
            <tr>
              <td class="mono">{{ c.code }}</td>
              <td>{{ c.advertiserName }}</td>
              <td class="muted">{{ c.periodStart }} ~ {{ c.periodEnd }}</td>
              <td class="num">{{ c.committed | number }}</td>
              <td class="num">{{ c.delivered | number }}</td>
              <td class="num">
                @if (c.lateAdjustments > 0) {
                  <span class="badge warn" title="账期确认后补传的迟到数据，以差额计入">+{{ c.lateAdjustments | number }}</span>
                } @else { <span class="muted">—</span> }
              </td>
              <td class="num">
                @if (c.makeGood > 0) { <span class="badge info">+{{ c.makeGood | number }}</span> }
                @else { <span class="muted">—</span> }
              </td>
              <td class="num">
                @if (c.discount > 0) { <span class="badge neutral">+{{ c.discount | number }}</span> }
                @else { <span class="muted">—</span> }
              </td>
              <td class="num">
                @if (c.gap > 0) { <strong class="danger">{{ c.gap | number }}</strong> }
                @else { <span class="ok">已达成</span> }
              </td>
              <td>
                <div class="progress">
                  <div class="bar delivered" [style.width.%]="barWidth(c.delivered, c.committed)"></div>
                  <div class="bar makegood" [style.width.%]="barWidth(c.makeGood + c.discount, c.committed)"></div>
                </div>
                <small class="muted">{{ c.progress | percent:'1.0-1' }}</small>
              </td>
              <td><a class="btn small" [routerLink]="['/contracts', c.contractId]">钻取</a></td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <div class="legend">
      <span><i class="sw delivered"></i>已交付（确认+未确认+迟到差额）</span>
      <span><i class="sw makegood"></i>补量/折让冲抵</span>
    </div>
  `
})
export class ContractsComponent implements OnInit {
  private api = inject(ApiService);

  contracts = signal<Rollup[]>([]);
  error = signal('');
  reseeding = signal(false);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    try {
      this.contracts.set(await this.api.contracts());
    } catch (e) {
      this.error.set((e as { message?: string }).message ?? '加载失败');
    }
  }

  async reseed(): Promise<void> {
    this.reseeding.set(true);
    this.error.set('');
    try {
      await this.api.seedDemo();
      await this.load();
    } catch (e) {
      this.error.set((e as { message?: string }).message ?? '重置失败');
    } finally {
      this.reseeding.set(false);
    }
  }

  barWidth(part: number, whole: number): number {
    if (whole <= 0) return 0;
    return Math.min(100, Math.max(0, (part / whole) * 100));
  }
}
