import { Routes } from '@angular/router';
import { ContractsComponent } from './contracts.component';
import { ContractDetailComponent } from './contract-detail.component';
import { ImportComponent } from './import.component';
import { SettlementComponent } from './settlement.component';
import { SnapshotsComponent } from './snapshots.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'contracts' },
  { path: 'contracts', component: ContractsComponent, title: '合同总览' },
  { path: 'contracts/:id', component: ContractDetailComponent, title: '合同详情' },
  { path: 'import', component: ImportComponent, title: '曝光导入' },
  { path: 'settlement', component: SettlementComponent, title: '结算工作台' },
  { path: 'snapshots', component: SnapshotsComponent, title: '快照回查' },
  { path: '**', redirectTo: 'contracts' }
];
