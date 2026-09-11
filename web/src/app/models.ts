// 与后端 DTO 对应的视图模型

export interface Rollup {
  contractId: string;
  code: string;
  advertiserName: string;
  periodStart: string;
  periodEnd: string;
  status: string;
  committed: number;
  confirmed: number;
  unconfirmed: number;
  lateAdjustments: number;
  makeGood: number;
  discount: number;
  delivered: number;
  gap: number;
  overDelivered: number;
  progress: number;
}

export interface DailyRow {
  serviceDate: string;
  rawImpressions: number;
  confirmedImpressions: number;
  adjustmentImpressions: number;
  isConfirmed: boolean;
}

export interface SlotRollup {
  slotId: string;
  code: string;
  name: string;
  timeZoneId: string;
  impressions: number;
}

export interface BatchRollup {
  batchId: string;
  batchKey: string;
  source: string;
  importedBy: string;
  importedAtUtc: string;
  rows: number;
  impressions: number;
}

export interface FactRow {
  hourUtc: string;
  serviceDate: string;
  validImpressions: number;
  dedupKey: string;
}

export interface Adjustment {
  id: string;
  serviceDate: string;
  deltaImpressions: number;
  reason: string;
  batchId: string;
  batchKey: string;
  createdAtUtc: string;
}

export interface Snapshot {
  id: string;
  version: number;
  asOfUtc: string;
  trigger: string;
  committed: number;
  confirmed: number;
  unconfirmed: number;
  lateAdjustments: number;
  makeGood: number;
  discount: number;
  delivered: number;
  gap: number;
}

export interface SnapshotDetail extends Snapshot {
  payload: unknown;
}

export interface MakeGoodPlan {
  id: string;
  contractId: string;
  contractCode: string;
  plannedImpressions: number;
  status: string;
  reason: string;
  createdBy: string;
  createdAtUtc: string;
  cancelledAtUtc: string | null;
  allocations: MakeGoodAllocation[];
}

export interface MakeGoodAllocation {
  id: string;
  sourceSlotCode: string;
  sourceSlotName: string;
  sourceServiceDate: string;
  allocatedImpressions: number;
  status: string;
}

export interface PoolRow {
  slotId: string;
  slotCode: string;
  slotName: string;
  serviceDate: string;
  totalImpressions: number;
  occupiedImpressions: number;
  availableImpressions: number;
}

export interface Discount {
  id: string;
  impressions: number;
  amount: number | null;
  reason: string;
  createdBy: string;
  createdAtUtc: string;
}

export interface ImportResult {
  batchId: string;
  batchKey: string;
  status: string;
  alreadyExisted: boolean;
  rowCount: number;
  acceptedCount: number;
  duplicateCount: number;
  lateAdjustmentsGenerated: number;
  affectedContractIds: string[];
}

export interface BatchInfo {
  id: string;
  batchKey: string;
  source: string;
  importedBy: string;
  importedAtUtc: string;
  rowCount: number;
  acceptedCount: number;
  duplicateCount: number;
  status: string;
  note: string | null;
}

export interface ApiError {
  code: string;
  message: string;
}
