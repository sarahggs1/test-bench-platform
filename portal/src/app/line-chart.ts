import { Component, computed, input } from '@angular/core';
import { Measurement } from './api.service';

/** Lightweight SVG line chart (no chart library), with optional horizontal limit lines. */
@Component({
  selector: 'app-line-chart',
  template: `
    @if (points().length > 1) {
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" class="chart" role="img" [attr.aria-label]="label()">
        @for (tick of yTicks(); track tick.value) {
          <line class="grid" [attr.x1]="pad.left" [attr.x2]="width - pad.right" [attr.y1]="tick.y" [attr.y2]="tick.y" />
          <text class="tick" [attr.x]="pad.left - 6" [attr.y]="tick.y + 4" text-anchor="end">{{ tick.value }}</text>
        }
        @for (tick of xTicks(); track tick.value) {
          <text class="tick" [attr.x]="tick.x" [attr.y]="height - pad.bottom + 16" text-anchor="middle">{{ tick.value }} s</text>
        }
        @for (limit of limitLines(); track limit.label) {
          <line class="limit" [attr.x1]="pad.left" [attr.x2]="width - pad.right" [attr.y1]="limit.y" [attr.y2]="limit.y" />
          <text class="limit-label" [attr.x]="width - pad.right" [attr.y]="limit.y - 4" text-anchor="end">{{ limit.label }}</text>
        }
        <polyline class="line" [attr.points]="path()" />
        <text class="axis-label" [attr.x]="pad.left" y="14">{{ label() }}</text>
      </svg>
    } @else {
      <p class="muted">No data for this signal yet.</p>
    }
  `,
  styles: `
    .chart { width: 100%; height: auto; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; }
    .line { fill: none; stroke: var(--accent); stroke-width: 1.8; }
    .grid { stroke: var(--border); stroke-width: 1; }
    .tick, .axis-label, .limit-label { font-size: 11px; fill: var(--muted); }
    .axis-label { font-weight: 600; fill: var(--text); }
    .limit { stroke: var(--danger); stroke-dasharray: 5 4; }
    .limit-label { fill: var(--danger); }
  `,
})
export class LineChart {
  readonly data = input<Measurement[]>([]);
  readonly label = input('');
  readonly min = input<number | null>(null);
  readonly max = input<number | null>(null);

  readonly width = 760;
  readonly height = 280;
  readonly pad = { left: 56, right: 16, top: 24, bottom: 28 };

  readonly points = computed(() => this.data());

  private readonly domain = computed(() => {
    const d = this.data();
    const values = d.map((p) => p.value);
    for (const l of [this.min(), this.max()]) if (l !== null) values.push(l);
    let lo = Math.min(...values), hi = Math.max(...values);
    if (lo === hi) { lo -= 1; hi += 1; }
    const margin = (hi - lo) * 0.08;
    return { t0: d[0]?.timeS ?? 0, t1: d[d.length - 1]?.timeS ?? 1, lo: lo - margin, hi: hi + margin };
  });

  private x(t: number): number {
    const { t0, t1 } = this.domain();
    return this.pad.left + ((t - t0) / (t1 - t0 || 1)) * (this.width - this.pad.left - this.pad.right);
  }

  private y(v: number): number {
    const { lo, hi } = this.domain();
    return this.height - this.pad.bottom - ((v - lo) / (hi - lo)) * (this.height - this.pad.top - this.pad.bottom);
  }

  readonly path = computed(() => this.data().map((p) => `${this.x(p.timeS).toFixed(1)},${this.y(p.value).toFixed(1)}`).join(' '));

  readonly yTicks = computed(() => {
    const { lo, hi } = this.domain();
    return Array.from({ length: 5 }, (_, i) => {
      const value = lo + ((hi - lo) * i) / 4;
      return { value: Number(value.toPrecision(3)), y: this.y(value) };
    });
  });

  readonly xTicks = computed(() => {
    const { t0, t1 } = this.domain();
    return Array.from({ length: 6 }, (_, i) => {
      const t = t0 + ((t1 - t0) * i) / 5;
      return { value: Math.round(t * 10) / 10, x: this.x(t) };
    });
  });

  readonly limitLines = computed(() => {
    const lines: { y: number; label: string }[] = [];
    if (this.max() !== null) lines.push({ y: this.y(this.max()!), label: `max ${this.max()}` });
    if (this.min() !== null) lines.push({ y: this.y(this.min()!), label: `min ${this.min()}` });
    return lines;
  });
}
