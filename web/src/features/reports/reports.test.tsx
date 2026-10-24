import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProblemReport, ReportsOverview, SettingsResponse } from '@/lib/api/types';
import { bodyOf, mockApi, renderApp } from '@/test/app';
import { FakeEventSource, statusResponse } from '@/test/fixtures';

const overview = (extra: Partial<ReportsOverview> = {}): ReportsOverview => ({ serviceConfigured: true, automatic: false, askedAt: null, contact: null, items: [], ...extra });

const preview: ProblemReport = {
  id: 'a'.repeat(32),
  kind: 'manual',
  at: '2026-10-08T12:00:00Z',
  component: 'user',
  summary: 'The chart stays empty',
  description: null,
  contact: null,
  cause: null,
  product: { name: 'PC Diagnostic', version: '1.0', app: 'web', elevated: false, installId: 'b'.repeat(32) },
  environment: { os: 'Microsoft Windows 10.0.26200', architecture: 'X64', processors: 24, memoryBytes: 68e9, runtime: '.NET 10', language: 'en-US', hardware: ['Processor: Intel Core Ultra 9 285K'] },
  log: [],
  context: { page: '/' },
  fingerprint: 'a'.repeat(32),
  occurrences: 1,
  firstSeen: '2026-10-08T12:00:00Z',
};

const settings: SettingsResponse = {
  file: 'C:\\data\\settings.json',
  settings: {
    recorder: { mode: 'Manual', schedule: [], startAgentAtLogon: false, preIncidentMinutes: 10, postIncidentSeconds: 120, ringBufferMaxMegabytes: 64 },
    sampling: { systemIntervalMs: 1000, sensorIntervalMs: 1000, processIntervalMs: 2000, inventoryIntervalSeconds: 10, topProcessCount: 25, cpuBudgetPercent: 2 },
    storage: { telemetryRetentionHours: 24, rollupRetentionDays: 30, writeBehindSeconds: 5, maxIncidents: 0, dataLimitGb: 10, keepFreeGb: 5 },
    events: { backfillMinutes: 30, bootBackfillMaxHours: 24, maxPerRulePerMinute: 20, maxMessageLength: 2000 },
    privacy: { captureCommandLines: false, captureUserNames: false, captureWifiNetworkNames: false, collectWindowsEvents: true, collectNetworkDetails: true },
    agent: { showTrayIcon: true, captureHotkey: 'Ctrl+Alt+Shift+I', notifyOnCapture: true, webHostPath: null },
  },
};

describe('Problem reports', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('previews exactly what is sent, then sends the report', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
      'GET /api/v1/reports': () => overview({ askedAt: '2026-10-08T00:00:00Z' }),
      'POST /api/v1/reports/preview': () => preview,
      'POST /api/v1/reports': () => ({ report: { id: preview.id }, outcome: { sent: true, reference: 'Bug 1234', problem: null } }),
    });
    renderApp('/incidents');

    await userEvent.click(await screen.findByRole('button', { name: 'Report a problem' }));
    const dialog = await screen.findByRole('dialog', { name: 'Report a problem with PC Diagnostic' });
    const send = within(dialog).getByRole('button', { name: 'Send report' });
    expect(send).toBeDisabled();
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'What went wrong?' }), 'The chart stays empty');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Preview' }));
    expect(await within(dialog).findByText(/"hardware": \[/)).toBeInTheDocument();

    await userEvent.click(send);
    expect(await screen.findByText('Report sent. Thank you!')).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/reports')).toMatchObject({ summary: 'The chart stays empty', includeLog: true, includeSystem: true, page: '/incidents' });
  });

  it('asks once whether reports may be sent automatically', async () => {
    let current = overview();
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
      'GET /api/v1/reports': () => current,
      'PUT /api/v1/reports/consent': (_url, init) => {
        current = overview({ automatic: (JSON.parse(String(init!.body)) as { automatic: boolean }).automatic, askedAt: '2026-10-08T12:00:00Z' });
        return current;
      },
    });
    renderApp('/incidents');

    const banner = await screen.findByRole('region', { name: 'Problem reports' });
    await userEvent.click(within(banner).getByRole('button', { name: 'What is sent?' }));
    expect(within(banner).getByText(/Never: what the recorder recorded about your PC/)).toBeInTheDocument();
    await userEvent.click(within(banner).getByRole('button', { name: 'Send reports automatically' }));
    expect(bodyOf(fetchMock, 'PUT', '/api/v1/reports/consent')).toEqual({ automatic: true });
    expect(screen.queryByRole('region', { name: 'Problem reports' })).not.toBeInTheDocument();
  });

  it('lists the reports in Settings, kept here while automatic reports are off', async () => {
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/settings': () => settings,
      'GET /api/v1/reports': () =>
        overview({
          askedAt: '2026-10-08T00:00:00Z',
          items: [{ id: 'c'.repeat(16), kind: 'automatic', component: 'agent/GpuCollector', summary: 'agent/GpuCollector: IOException: it broke', at: '2026-10-08T11:00:00Z', occurrences: 3, status: 'local', sentAt: null, reference: null, lastError: null }],
        }),
    });
    renderApp('/settings#reports');

    expect(await screen.findByText('agent/GpuCollector: IOException: it broke')).toBeInTheDocument();
    expect(screen.getByText('Kept on this PC')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Send problem reports automatically' })).not.toBeChecked();
  });
});
