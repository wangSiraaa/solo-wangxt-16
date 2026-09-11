import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from './api.service';
import { BatchInfo, ImportResult } from './models';

const SAMPLE = `{
  "batchKey": "SEP-W01-DEMO",
  "source": "ADX-LOG",
  "importedBy": "ops.zhang",
  "note": "手工导入示例",
  "rows": [
    { "slotCode": "SH-OPENSCREEN", "hourUtc": "2026-08-03T02:00:00Z", "validImpressions": 612 },
    { "slotCode": "NY-FEED",       "hourUtc": "2026-08-10T14:00:00Z", "validImpressions": 480 },
    { "slotCode": "LDN-BANNER",    "hourUtc": "2026-08-12T08:00:00Z", "validImpressions": 530 }
  ]
}`;

@Component({
  selector: 'app-import',
  standalone: true,
  imports: [FormsModule, DecimalPipe, DatePipe],
  template: `
    <div class="page-head">
      <div>
        <h1>曝光导入</h1>
        <p class="muted">投放人员：导入按小时汇总的有效曝光（UTC 小时，落库时按广告位时区归日）</p>
      </div>
    </div>

    <div class="two-col">
      <div class="card">
        <h3>导入载荷（JSON）</h3>
        <textarea class="payload" [(ngModel)]="payload" rows="18" spellcheck="false"></textarea>
        <div class="actions">
          <button class="btn primary" (click)="doImport()" [disabled]="busy()">导入</button>
          <button class="btn ghost" (click)="payload = sample">填入示例</button>
          <button class="btn ghost" (click)="payload = duplicateSample()">填入“重复批次”示例</button>
        </div>
        @if (parseError()) { <div class="alert error">{{ parseError() }}</div> }

        @if (result(); as r) {
          <div class="result" [class.dup]="r.alreadyExisted">
            <h4>{{ r.alreadyExisted ? '⚠ 整批重复：已按首次导入结果返回，未叠加' : '✓ 导入完成' }}</h4>
            <div class="stat-grid small">
              <div class="stat"><label>总行数</label><b>{{ r.rowCount }}</b></div>
              <div class="stat"><label>接受</label><b class="ok">{{ r.acceptedCount }}</b></div>
              <div class="stat"><label>行级去重</label><b class="warn-text">{{ r.duplicateCount }}</b></div>
              <div class="stat"><label>迟到差额</label><b class="warn-text">{{ r.lateAdjustmentsGenerated }}</b></div>
            </div>
            @if (r.lateAdjustmentsGenerated > 0) {
              <p class="hint">有 {{ r.lateAdjustmentsGenerated }} 行落在已确认账期 → 已生成差额（见合同详情 · 迟到差额），台账未被改动。</p>
            }
          </div>
        }
      </div>

      <div class="card">
        <h3>最近导入批次</h3>
        <table>
          <thead><tr><th>批次键</th><th class="num">行</th><th class="num">接受</th><th class="num">去重</th><th>导入人</th><th>时间</th></tr></thead>
          <tbody>
            @for (b of batches(); track b.id) {
              <tr>
                <td><b class="mono">{{ b.batchKey }}</b><br><small class="muted">{{ b.note }}</small></td>
                <td class="num">{{ b.rowCount }}</td>
                <td class="num ok">{{ b.acceptedCount }}</td>
                <td class="num">{{ b.duplicateCount > 0 ? b.duplicateCount : '—' }}</td>
                <td>{{ b.importedBy }}</td>
                <td class="muted">{{ b.importedAtUtc | date:'MM-dd HH:mm' }}</td>
              </tr>
            }
          </tbody>
        </table>
        <p class="hint">
          幂等规则：① 相同 batchKey 整批重传 → 直接返回首次结果；
          ② 不同批次含相同 (来源|广告位|小时) 行 → 该行被去重计数，不叠加。
        </p>
      </div>
    </div>
  `
})
export class ImportComponent implements OnInit {
  private api = inject(ApiService);

  payload = SAMPLE;
  readonly sample = SAMPLE;
  batches = signal<BatchInfo[]>([]);
  result = signal<ImportResult | null>(null);
  parseError = signal('');
  busy = signal(false);

  ngOnInit(): void {
    void this.refreshBatches();
  }

  duplicateSample(): string {
    const parsed = JSON.parse(SAMPLE) as Record<string, unknown>;
    parsed['note'] = '同一 batchKey 重传 —— 应被幂等吞掉';
    return JSON.stringify(parsed, null, 2);
  }

  async doImport(): Promise<void> {
    this.parseError.set('');
    this.result.set(null);
    let body: unknown;
    try {
      body = JSON.parse(this.payload);
    } catch {
      this.parseError.set('JSON 解析失败，请检查格式');
      return;
    }
    this.busy.set(true);
    try {
      const r = await this.api.import(body);
      this.result.set(r);
      await this.refreshBatches();
    } catch (e) {
      this.parseError.set((e as { message?: string }).message ?? '导入失败');
    } finally {
      this.busy.set(false);
    }
  }

  private async refreshBatches(): Promise<void> {
    this.batches.set(await this.api.batches());
  }
}
