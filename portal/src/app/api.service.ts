import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface BenchStatus {
  modbus: {
    connected: boolean;
    state: number; // 0 stopped, 1 running, 2 fault
    alarms: number;
    alarmNames: string[];
    speedSetpoint: number;
    loadSetpoint: number;
    runTimeS: number;
    speedRpm: number;
    error: string | null;
  };
  canReceiving: boolean;
  canFrames: number;
  activeRun: { runId: number; testId: number; elapsedS: number; durationS: number; samplesRecorded: number } | null;
}

export interface BenchConfiguration {
  id: number;
  benchId: number;
  version: string;
  description: string;
  status: 'draft' | 'validated' | 'deployed';
  createdAt: string;
  deployedAt: string | null;
}

export interface TestDefinition {
  id: number;
  name: string;
  benchId: number;
  configurationId: number | null;
  plannedDate: string;
  speedRpm: number;
  loadNm: number;
  durationS: number;
  status: string;
  notes: string;
}

export type NewTest = Omit<TestDefinition, 'id' | 'status'>;

export interface TestRun {
  id: number;
  testId: number;
  startedAt: string;
  endedAt: string | null;
  status: string;
  samples: number;
  verdict: 'pass' | 'fail' | null;
  summaryJson: string | null;
}

export interface RunSummary {
  endReason: string;
  signals: { signal: string; count: number; min: number; max: number; mean: number }[];
  checks: { signal: string; min: number | null; max: number | null; observed: number; passed: boolean; firstViolationS: number | null }[];
  alarms: string[];
}

export interface Measurement {
  timeS: number;
  signal: string;
  value: number;
}

export interface ReleaseNote {
  id: number;
  version: string;
  date: string;
  component: string;
  notes: string;
}

export interface MessageDefinition {
  id: number;
  name: string;
  signals: { name: string; unit: string }[];
}

/** Typed client of the BenchPortal API (served under /api). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  benchStatus(): Observable<BenchStatus> { return this.http.get<BenchStatus>('/api/bench/status'); }
  resetFault(): Observable<void> { return this.http.post<void>('/api/bench/reset', {}); }
  live(): Observable<Record<string, number>> { return this.http.get<Record<string, number>>('/api/live'); }
  signals(): Observable<MessageDefinition[]> { return this.http.get<MessageDefinition[]>('/api/signals'); }
  limits(): Observable<{ ignoreFirstS: number; limits: Record<string, { min: number | null; max: number | null }> }> {
    return this.http.get<{ ignoreFirstS: number; limits: Record<string, { min: number | null; max: number | null }> }>('/api/limits');
  }

  configurations(): Observable<BenchConfiguration[]> { return this.http.get<BenchConfiguration[]>('/api/configurations'); }
  addConfiguration(body: { benchId: number; version: string; description: string }): Observable<BenchConfiguration> {
    return this.http.post<BenchConfiguration>('/api/configurations', body);
  }
  setConfigurationStatus(id: number, status: string): Observable<BenchConfiguration> {
    return this.http.post<BenchConfiguration>(`/api/configurations/${id}/status`, { status });
  }

  tests(): Observable<TestDefinition[]> { return this.http.get<TestDefinition[]>('/api/tests'); }
  addTest(body: NewTest): Observable<TestDefinition> { return this.http.post<TestDefinition>('/api/tests', body); }
  startTest(id: number): Observable<TestRun> { return this.http.post<TestRun>(`/api/tests/${id}/start`, {}); }

  runs(testId?: number): Observable<TestRun[]> {
    return this.http.get<TestRun[]>('/api/runs', { params: testId ? { testId } : {} });
  }
  run(id: number): Observable<TestRun> { return this.http.get<TestRun>(`/api/runs/${id}`); }
  stopRun(id: number): Observable<void> { return this.http.post<void>(`/api/runs/${id}/stop`, {}); }
  measurements(runId: number, signal: string): Observable<Measurement[]> {
    return this.http.get<Measurement[]>(`/api/runs/${runId}/measurements`, { params: { signal, maxPoints: 1500 } });
  }

  releaseNotes(): Observable<ReleaseNote[]> { return this.http.get<ReleaseNote[]>('/api/release-notes'); }
  addReleaseNote(body: { version: string; component: string; notes: string }): Observable<ReleaseNote> {
    return this.http.post<ReleaseNote>('/api/release-notes', body);
  }
}

/** Readable message from an API error response. */
export function errorMessage(err: unknown): string {
  const e = err as { error?: { error?: string }; message?: string };
  return e?.error?.error ?? e?.message ?? 'Unexpected error';
}

export const BENCH_STATES = ['Stopped', 'Running', 'Fault'];
