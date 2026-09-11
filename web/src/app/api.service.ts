import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  Adjustment, ApiError, BatchInfo, BatchRollup, DailyRow, Discount, FactRow,
  ImportResult, MakeGoodPlan, PoolRow, Rollup, SlotRollup, Snapshot, SnapshotDetail
} from './models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private base = '/api';

  // ---------- 查询 ----------
  contracts(): Promise<Rollup[]> {
    return this.get(`${this.base}/contracts`);
  }
  contract(id: string): Promise<Rollup> {
    return this.get(`${this.base}/contracts/${id}`);
  }
  daily(id: string): Promise<DailyRow[]> {
    return this.get(`${this.base}/contracts/${id}/daily`);
  }
  slots(id: string): Promise<SlotRollup[]> {
    return this.get(`${this.base}/contracts/${id}/slots`);
  }
  slotBatches(contractId: string, slotId: string): Promise<BatchRollup[]> {
    return this.get(`${this.base}/contracts/${contractId}/slots/${slotId}/batches`);
  }
  batchRows(batchId: string): Promise<FactRow[]> {
    return this.get(`${this.base}/batches/${batchId}/rows`);
  }
  batches(): Promise<BatchInfo[]> {
    return this.get(`${this.base}/batches`);
  }
  adjustments(id: string): Promise<Adjustment[]> {
    return this.get(`${this.base}/contracts/${id}/adjustments`);
  }
  snapshots(id: string): Promise<Snapshot[]> {
    return this.get(`${this.base}/contracts/${id}/snapshots`);
  }
  snapshotDetail(id: string): Promise<SnapshotDetail> {
    return this.get(`${this.base}/snapshots/${id}`);
  }
  makeGoods(id: string): Promise<MakeGoodPlan[]> {
    return this.get(`${this.base}/contracts/${id}/makegoods`);
  }
  discounts(id: string): Promise<Discount[]> {
    return this.get(`${this.base}/contracts/${id}/discounts`);
  }
  pool(): Promise<PoolRow[]> {
    return this.get(`${this.base}/makegood-pool`);
  }

  // ---------- 动作 ----------
  import(payload: unknown): Promise<ImportResult> {
    return this.post(`${this.base}/imports`, payload);
  }
  closePeriod(id: string, throughDate: string | null, operatorName: string): Promise<{ closedDays: number }> {
    return this.post(`${this.base}/contracts/${id}/close-period`, { throughDate, operatorName });
  }
  createMakeGood(id: string, reason: string, createdBy: string,
                 allocations: { sourceSlotCode: string; sourceServiceDate: string; impressions: number }[]): Promise<MakeGoodPlan> {
    return this.post(`${this.base}/contracts/${id}/makegoods`, { reason, createdBy, allocations });
  }
  cancelMakeGood(planId: string): Promise<MakeGoodPlan> {
    return this.post(`${this.base}/makegoods/${planId}/cancel`, {});
  }
  createDiscount(id: string, impressions: number, amount: number | null,
                 reason: string, createdBy: string): Promise<Discount> {
    return this.post(`${this.base}/contracts/${id}/discounts`, { impressions, amount, reason, createdBy });
  }
  seedDemo(): Promise<{ seeded: boolean }> {
    return this.post(`${this.base}/seed/demo`, {});
  }

  private async get<T>(url: string): Promise<T> {
    try {
      return await firstValueFrom(this.http.get<T>(url));
    } catch (e) {
      throw toApiError(e);
    }
  }

  private async post<T>(url: string, body: unknown): Promise<T> {
    try {
      return await firstValueFrom(this.http.post<T>(url, body));
    } catch (e) {
      throw toApiError(e);
    }
  }
}

export function toApiError(e: unknown): ApiError {
  const err = e as HttpErrorResponse;
  if (err?.error && typeof err.error === 'object' && 'code' in err.error) {
    return err.error as ApiError;
  }
  return { code: 'NETWORK', message: err?.message ?? '请求失败' };
}
