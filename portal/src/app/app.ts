import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="topbar">
      <span class="brand">Test Bench Portal</span>
      <nav>
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Bench</a>
        <a routerLink="/tests" routerLinkActive="active">Tests</a>
        <a routerLink="/configurations" routerLinkActive="active">Configurations</a>
      </nav>
    </header>
    <main><router-outlet /></main>
  `,
})
export class App {}
