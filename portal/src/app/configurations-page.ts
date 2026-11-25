import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, BenchConfiguration, ReleaseNote, errorMessage } from './api.service';

/** Bench software configurations (draft -> validated -> deployed) and release notes. */
@Component({
  selector: 'app-configurations-page',
  imports: [FormsModule],
  template: `
    <h1>Configurations</h1>
    <p class="muted">A configuration must be validated (non-regression tests passed) before it can be deployed on the bench.
      Deploying a configuration retires the one previously deployed. Tests can only run on the deployed configuration.</p>
    @if (error()) { <p class="alert">{{ error() }}</p> }

    <table>
      <thead><tr><th>Version</th><th>Description</th><th>Status</th><th>Created</th><th>Deployed</th><th></th></tr></thead>
      <tbody>
        @for (c of configs(); track c.id) {
          <tr>
            <td><strong>{{ c.version }}</strong></td>
            <td>{{ c.description }}</td>
            <td><span class="badge" [class]="c.status">{{ c.status }}</span></td>
            <td>{{ c.createdAt }}</td>
            <td>{{ c.deployedAt ?? '–' }}</td>
            <td>
              @if (c.status === 'draft') { <button (click)="move(c, 'validated')">Mark validated</button> }
              @if (c.status === 'validated') { <button class="primary" (click)="move(c, 'deployed')">Deploy</button> }
            </td>
          </tr>
        }
      </tbody>
    </table>

    <form class="form" (ngSubmit)="addConfig()">
      <label>Version <input name="version" [(ngModel)]="newConfig.version" placeholder="1.1.0" required /></label>
      <label class="wide">Description <input name="description" [(ngModel)]="newConfig.description" required /></label>
      <button type="submit">Add configuration</button>
    </form>

    <h2>Release notes</h2>
    @for (n of notes(); track n.id) {
      <article class="note">
        <header><strong>{{ n.version }}</strong> · {{ n.component }} · <span class="muted">{{ n.date }}</span></header>
        <p>{{ n.notes }}</p>
      </article>
    }
    <form class="form" (ngSubmit)="addNote()">
      <label>Version <input name="nversion" [(ngModel)]="newNote.version" required /></label>
      <label>Component
        <select name="component" [(ngModel)]="newNote.component">
          <option>Acquisition</option><option>Pilotage</option><option>Portal</option><option>Simulator</option>
        </select>
      </label>
      <label class="wide">Notes <input name="notes" [(ngModel)]="newNote.notes" required /></label>
      <button type="submit">Publish note</button>
    </form>
  `,
})
export class ConfigurationsPage implements OnInit {
  private readonly api = inject(ApiService);

  readonly configs = signal<BenchConfiguration[]>([]);
  readonly notes = signal<ReleaseNote[]>([]);
  readonly error = signal('');

  newConfig = { benchId: 1, version: '', description: '' };
  newNote = { version: '', component: 'Acquisition', notes: '' };

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.configurations().subscribe((c) => this.configs.set(c));
    this.api.releaseNotes().subscribe((n) => this.notes.set(n));
  }

  move(c: BenchConfiguration, status: string): void {
    this.api.setConfigurationStatus(c.id, status).subscribe({
      next: () => { this.error.set(''); this.load(); },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  addConfig(): void {
    this.api.addConfiguration(this.newConfig).subscribe({
      next: () => { this.newConfig = { benchId: 1, version: '', description: '' }; this.load(); },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  addNote(): void {
    this.api.addReleaseNote(this.newNote).subscribe({
      next: () => { this.newNote = { version: '', component: 'Acquisition', notes: '' }; this.load(); },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }
}
