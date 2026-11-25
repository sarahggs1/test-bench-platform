import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService, BenchConfiguration, NewTest, TestDefinition, TestRun, errorMessage } from './api.service';

/** Test planning: create tests on a deployed configuration, start them, follow their runs. */
@Component({
  selector: 'app-tests-page',
  imports: [FormsModule, RouterLink],
  template: `
    <h1>Tests</h1>
    @if (error()) { <p class="alert">{{ error() }}</p> }

    <table>
      <thead>
        <tr><th>Planned</th><th>Name</th><th>Configuration</th><th class="num">Speed</th><th class="num">Load</th>
          <th class="num">Duration</th><th>Status</th><th>Last run</th><th></th></tr>
      </thead>
      <tbody>
        @for (t of tests(); track t.id) {
          <tr>
            <td>{{ t.plannedDate }}</td>
            <td>{{ t.name }}</td>
            <td>{{ configLabel(t.configurationId) }}</td>
            <td class="num">{{ t.speedRpm }} rpm</td>
            <td class="num">{{ t.loadNm }} N·m</td>
            <td class="num">{{ t.durationS }} s</td>
            <td><span class="badge" [class]="t.status">{{ t.status }}</span></td>
            <td>
              @if (lastRun(t.id); as r) {
                <a [routerLink]="['/runs', r.id]">#{{ r.id }} {{ r.verdict ?? r.status }}</a>
              }
            </td>
            <td><button [disabled]="t.status === 'running' || busy()" (click)="start(t)">Start</button></td>
          </tr>
        } @empty {
          <tr><td colspan="9" class="muted">No test planned yet.</td></tr>
        }
      </tbody>
    </table>

    <h2>Plan a test</h2>
    <form class="form" (ngSubmit)="create()">
      <label>Name <input name="name" [(ngModel)]="draft.name" required /></label>
      <label>Planned date <input type="date" name="date" [(ngModel)]="draft.plannedDate" required /></label>
      <label>Configuration
        <select name="config" [(ngModel)]="draft.configurationId">
          @for (c of usableConfigs(); track c.id) { <option [ngValue]="c.id">{{ c.version }} ({{ c.status }})</option> }
        </select>
      </label>
      <label>Speed setpoint (rpm) <input type="number" name="speed" min="0" max="7000" [(ngModel)]="draft.speedRpm" /></label>
      <label>Load setpoint (N·m) <input type="number" name="load" min="0" max="400" [(ngModel)]="draft.loadNm" /></label>
      <label>Duration (s) <input type="number" name="duration" min="1" max="3600" [(ngModel)]="draft.durationS" /></label>
      <label class="wide">Notes <input name="notes" [(ngModel)]="draft.notes" /></label>
      <button type="submit" class="primary">Add test</button>
    </form>
  `,
})
export class TestsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly tests = signal<TestDefinition[]>([]);
  readonly runs = signal<TestRun[]>([]);
  readonly configs = signal<BenchConfiguration[]>([]);
  readonly error = signal('');
  readonly busy = signal(false);
  readonly usableConfigs = computed(() => this.configs().filter((c) => c.status !== 'draft'));

  draft: NewTest = {
    name: 'Endurance 3000 rpm',
    benchId: 1,
    configurationId: null,
    plannedDate: new Date().toISOString().slice(0, 10),
    speedRpm: 3000,
    loadNm: 150,
    durationS: 30,
    notes: '',
  };

  ngOnInit(): void {
    this.load();
    this.api.configurations().subscribe((c) => {
      this.configs.set(c);
      this.draft.configurationId = c.find((x) => x.status === 'deployed')?.id ?? null;
    });
  }

  load(): void {
    this.api.tests().subscribe((t) => this.tests.set(t));
    this.api.runs().subscribe((r) => this.runs.set(r));
  }

  configLabel(id: number | null): string {
    return this.configs().find((c) => c.id === id)?.version ?? '–';
  }

  lastRun(testId: number): TestRun | undefined {
    return this.runs().find((r) => r.testId === testId); // runs are sorted newest first
  }

  create(): void {
    this.api.addTest(this.draft).subscribe({
      next: () => { this.error.set(''); this.load(); },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  start(test: TestDefinition): void {
    this.busy.set(true);
    this.api.startTest(test.id).subscribe({
      next: (run) => this.router.navigate(['/runs', run.id]),
      error: (e) => { this.error.set(errorMessage(e)); this.busy.set(false); },
    });
  }
}
