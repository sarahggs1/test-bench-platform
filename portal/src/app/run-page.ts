import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, Measurement, MessageDefinition, RunSummary, TestRun, errorMessage } from './api.service';
import { LineChart } from './line-chart';

/** One run: status, verdict, acceptance checks and signal plots. Refreshes while the run is in progress. */
@Component({
  selector: 'app-run-page',
  imports: [DecimalPipe, FormsModule, LineChart],
  template: `
    @if (run(); as r) {
      <h1>Run #{{ r.id }}
        @if (r.verdict) { <span class="badge" [class]="r.verdict">{{ r.verdict }}</span> }
      </h1>
      <p class="muted">Test #{{ r.testId }} · started {{ r.startedAt }} · {{ r.endedAt ? 'ended ' + r.endedAt : 'in progress' }}
        · status <strong>{{ r.status }}</strong> · {{ r.samples }} samples</p>
      @if (r.status === 'running') { <button class="danger" (click)="stop()">Stop run</button> }
    }
    @if (error()) { <p class="alert">{{ error() }}</p> }

    <section class="panel">
      <label>Signal
        <select [ngModel]="selected()" (ngModelChange)="select($event)">
          @for (s of signalNames(); track s) { <option [value]="s">{{ s }}</option> }
        </select>
      </label>
      <app-line-chart [data]="points()" [label]="selected()" [min]="limitFor(selected())?.min ?? null"
        [max]="limitFor(selected())?.max ?? null" />
    </section>

    @if (summary(); as s) {
      <h2>Acceptance checks</h2>
      @if (s.alarms.length) { <p class="alert">Bench alarms: {{ s.alarms.join(', ') }}</p> }
      <table>
        <thead><tr><th>Signal</th><th class="num">Min</th><th class="num">Max</th><th class="num">Observed</th><th>Result</th><th class="num">First violation</th></tr></thead>
        <tbody>
          @for (c of s.checks; track c.signal) {
            <tr>
              <td>{{ c.signal }}</td><td class="num">{{ c.min ?? '–' }}</td><td class="num">{{ c.max ?? '–' }}</td>
              <td class="num">{{ c.observed }}</td>
              <td><span class="badge" [class]="c.passed ? 'pass' : 'fail'">{{ c.passed ? 'PASS' : 'FAIL' }}</span></td>
              <td class="num">{{ c.firstViolationS === null ? '–' : (c.firstViolationS | number: '1.1-1') + ' s' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="muted">No check: the run ended during warm-up.</td></tr>
          }
        </tbody>
      </table>

      <h2>Statistics</h2>
      <table>
        <thead><tr><th>Signal</th><th class="num">Samples</th><th class="num">Min</th><th class="num">Mean</th><th class="num">Max</th></tr></thead>
        <tbody>
          @for (sig of s.signals; track sig.signal) {
            <tr><td>{{ sig.signal }}</td><td class="num">{{ sig.count }}</td><td class="num">{{ sig.min | number: '1.0-2' }}</td>
              <td class="num">{{ sig.mean | number: '1.0-2' }}</td><td class="num">{{ sig.max | number: '1.0-2' }}</td></tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class RunPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private timer?: ReturnType<typeof setInterval>;

  readonly id = input.required<string>(); // route parameter
  readonly run = signal<TestRun | null>(null);
  readonly points = signal<Measurement[]>([]);
  readonly messages = signal<MessageDefinition[]>([]);
  readonly selected = signal('speed_rpm');
  readonly error = signal('');
  private readonly limits = signal<Record<string, { min: number | null; max: number | null }>>({});

  readonly signalNames = computed(() => this.messages().flatMap((m) => m.signals.map((s) => s.name)));
  readonly summary = computed<RunSummary | null>(() => {
    const json = this.run()?.summaryJson;
    return json ? (JSON.parse(json) as RunSummary) : null;
  });

  ngOnInit(): void {
    this.api.signals().subscribe((m) => this.messages.set(m));
    this.api.limits().subscribe((l) => this.limits.set(l.limits));
    this.refresh();
    this.timer = setInterval(() => {
      if (this.run()?.status === 'running') this.refresh();
    }, 1000);
  }

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }

  limitFor(signal: string): { min: number | null; max: number | null } | undefined {
    const check = this.summary()?.checks.find((c) => c.signal === signal);
    return check ? { min: check.min, max: check.max } : this.limits()[signal];
  }

  select(signal: string): void {
    this.selected.set(signal);
    this.loadPoints();
  }

  refresh(): void {
    this.api.run(+this.id()).subscribe({
      next: (r) => { this.run.set(r); this.loadPoints(); },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  private loadPoints(): void {
    this.api.measurements(+this.id(), this.selected()).subscribe((p) => this.points.set(p));
  }

  stop(): void {
    this.api.stopRun(+this.id()).subscribe({ error: (e) => this.error.set(errorMessage(e)) });
  }
}
