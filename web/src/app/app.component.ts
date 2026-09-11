import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="topbar">
      <div class="brand">
        <span class="logo">◔</span>
        <div>
          <div class="brand-name">包量投放核算</div>
          <div class="brand-sub">曝光归集 · 账期确认 · 补量与折让</div>
        </div>
      </div>
      <nav>
        <a routerLink="/contracts" routerLinkActive="active">合同总览</a>
        <a routerLink="/import" routerLinkActive="active">曝光导入</a>
        <a routerLink="/settlement" routerLinkActive="active">结算工作台</a>
        <a routerLink="/snapshots" routerLinkActive="active">快照回查</a>
      </nav>
    </header>
    <main>
      <router-outlet />
    </main>
    <footer class="footnote">
      曝光按广告位时区归日 · 批次幂等 · 迟到数据只记差额 · 补量曝光独占 · 全程快照留痕
    </footer>
  `
})
export class AppComponent {}
