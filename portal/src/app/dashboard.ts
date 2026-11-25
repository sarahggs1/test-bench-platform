import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, BENCH_STATES, BenchStatus, MessageDefinition, errorMessage } from './api.service';

/** Live supervision of the bench: Modbus status, CAN live values, active run. */
@Component({
  selector: 'app-dashboard',
  imports: [DecimalPipe, RouterLink],
  template: `
    <h1>Bench BANC-01</h1>
    @if (error()) { <p class="alert">{{ error() }}</p> }

    @if (status(); as s) {
      <section class="cards">
        <div class="card">
          <div class="label">Bench state</div>
          <div class="value" [class.ok]="s.modbus.state === 1" [class.bad]="s.modbus.state === 2">
            {{ s.modbus.connected ? states[s.modbus.state] : 'Offline' }}
          </div>
          @if (s.modbus.state === 2) { <button (click)="reset()">Reset fault</button> }
        </div>
        <div class="card">
          <div class="label">Modbus TCP</div>
          <div class="value" [class.ok]="s.modbus.connected" [class.bad]="!s.modbus.connected">
            {{ s.modbus.connected ? 'Connected' : 'Not reachable' }}
          </div>
          <div class="muted">Setpoints: {{ s.modbus.speedSetpoint }} rpm / {{ s.modbus.loadSetpoint }} N·m</div>
        </div>
        <div class="card">
          <div class="label">CAN acquisition</div>
          <div class="value" [class.ok]="s.canReceiving" [class.bad]="!s.canReceiving">
            {{ s.canReceiving ? 'Receiving' : 'No frames' }}
          </div>
          <div class="muted">{{ s.canFrames | number }} frames received</div>
        </div>
        <div class="card">
          <div class="label">Alarms</div>
          <div class="value" [class.ok]="!s.modbus.alarmNames.length" [class.bad]="s.modbus.alarmNames.length">
            {{ s.modbus.alarmNames.length ? s.modbus.alarmNames.join(', ') : 'None' }}
          </div>
        </div>
      </section>

      @if (s.activeRun; as run) {
        <section class="panel">
          <h2>Run {{ run.runId }} in progress</h2>
          <progress [value]="run.elapsedS" [max]="run.durationS"></progress>
          <p>{{ run.elapsedS | number: '1.0-0' }} / {{ run.durationS }} s · {{ run.samplesRecorded | number }} samples recorded</p>
          <button class="danger" (click)="stop(run.runId)">Stop run</button>
          <a [routerLink]="['/runs', run.runId]">Open run</a>
        </section>
      }
    }

    <h2>Live values</h2>
    <table>
      <thead><tr><th>Message</th><th>Signal</th><th class="num">Value</th><th>Unit</th></tr></thead>
      <tbody>
        @for (m of messages(); track m.id) {
          @for (sig of m.signals; track sig.name) {
            <tr>
              <td class="muted">{{ m.name }} (0x{{ m.id.toString(16).toUpperCase() }})</td>
              <td>{{ sig.name }}</td>
              <td class="num">{{ live()[sig.name] === undefined ? '–' : (live()[sig.name] | number: '1.0-2') }}</td>
              <td class="muted">{{ sig.unit }}</td>
            </tr>
          }
        }
      </tbody>
    </table>
  `,
})
export class Dashboard implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private timer?: ReturnType<typeof setInterval>;

  readonly states = BENCH_STATES;
  readonly status = signal<BenchStatus | null>(null);
  readonly live = signal<Record<string, number>>({});
  readonly messages = signal<MessageDefinition[]>([]);
  readonly error = signal('');

  ngOnInit(): void {
    this.api.signals().subscribe((m) => this.messages.set(m));
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 1000);
  }

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }

  refresh(): void {
    this.api.benchStatus().subscribe({
      next: (s) => { this.status.set(s); this.error.set(''); },
      error: (e) => this.error.set(`API not reachable: ${errorMessage(e)}`),
    });
    this.api.live().subscribe((v) => this.live.set(v));
  }

  reset(): void {
    this.api.resetFault().subscribe({ next: () => this.refresh(), error: (e) => this.error.set(errorMessage(e)) });
  }

  stop(runId: number): void {
    this.api.stopRun(runId).subscribe({ error: (e) => this.error.set(errorMessage(e)) });
  }
}
