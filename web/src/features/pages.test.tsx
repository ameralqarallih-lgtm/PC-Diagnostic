import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DiagnosticJob, FindingEntry, IncidentDetail, IncidentSummary, LanStatus, PeriodReport, SettingsResponse, SignatureStatus, SystemOverview, TimelineEntry, TriggerRuleInfo } from '@/lib/api/types';
import { bodyOf, confirmIt, mockApi, Problem, queryOf, renderApp } from '@/test/app';
import { FakeEventSource, statusResponse } from '@/test/fixtures';

const T0 = Date.parse('2026-10-06T12:00:00Z');

const summary: IncidentSummary = {
  key: 'INC-20261006-00007',
  createdAt: '2026-10-06T12:00:00Z',
  centerAt: '2026-10-06T12:00:00Z',
  preSeconds: 600,
  postSeconds: 120,
  state: 'Complete',
  source: 'Hotkey',
  userType: 'ScreenFlicker',
  title: 'Screen Flicker',
  notes: null,
  severity: 'Medium',
  snapshotOnly: false,
  pinned: false,
  triggerCount: 1,
};

const detail: IncidentDetail = {
  summary,
  from: T0 - 600_000,
  to: T0 + 120_000,
  triggers: [{ ts: T0, source: 'Hotkey', userType: 'ScreenFlicker', ruleId: null, detail: null, note: 'left monitor' }],
  info: { title: 'Screen Flicker' },
  hasData: true,
};

const timeline: TimelineEntry[] = [
  { ts: T0 - 20_000, kind: 'windows-event', title: 'Display driver nvlddmkm stopped responding', importance: 3, detail: { provider: 'nvlddmkm', eventId: 153 }, sourceId: 1 },
  { ts: T0 - 5_000, kind: 'display', title: 'DELL U2723QE: refresh rate changed (144 Hz → 60 Hz)', importance: 1, detail: null, sourceId: 2 },
];

const emptySystem: SystemOverview = {
  information: { 'cpu.name': 'AMD Ryzen 9 7950X' },
  devices: [],
  driversTs: T0,
  drivers: [{ deviceClass: 'Display', deviceName: 'NVIDIA GeForce RTX 4090', hardwareId: null, provider: 'NVIDIA', version: '32.0.15.7000', date: '2026-09-01', inf: null, signer: null }],
  displayTs: null,
  display: null,
  hardwareTs: null,
  hardware: null,
  availability: [{ series: 'cpu/package_temp', availability: 'RequiresElevation', reason: null, since: T0 }],
  sensors: [],
};

const settings: SettingsResponse = {
  file: 'C:\\Users\\me\\AppData\\Local\\PcDiagnostic\\settings.json',
  settings: {
    recorder: { mode: 'Manual', schedule: [], startAgentAtLogon: false, preIncidentMinutes: 10, postIncidentSeconds: 120, ringBufferMaxMegabytes: 64 },
    sampling: { systemIntervalMs: 1000, sensorIntervalMs: 1000, processIntervalMs: 2000, inventoryIntervalSeconds: 10, topProcessCount: 25, cpuBudgetPercent: 2 },
    storage: { telemetryRetentionHours: 24, rollupRetentionDays: 30, writeBehindSeconds: 5, maxIncidents: 0, dataLimitGb: 10, keepFreeGb: 5 },
    events: { backfillMinutes: 30, bootBackfillMaxHours: 24, maxPerRulePerMinute: 20, maxMessageLength: 2000 },
    privacy: { captureCommandLines: false, captureUserNames: false, captureWifiNetworkNames: false, collectWindowsEvents: true, collectNetworkDetails: true },
    agent: { showTrayIcon: true, captureHotkey: 'Ctrl+Alt+Shift+I', notifyOnCapture: true, webHostPath: null },
  },
};

const common = {
  'GET /api/v1/status': () => statusResponse(),
  'GET /api/v1/history/catalog': () => [
    { id: 'cpu', label: 'CPU', unit: '%', group: 'System', device: null, longTerm: true },
    { id: 'cpu.temp', label: 'CPU package temperature', unit: '°C', group: 'CPU', device: null, longTerm: true },
  ],
};

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
});

afterEach(() => vi.unstubAllGlobals());

describe('History page', () => {
  it('charts the series in the URL and lists events; picking series updates the URL', async () => {
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/history/series': () => ({ from: T0 - 3_600_000, to: T0, bucketMs: 4000, series: [{ id: 'cpu', points: [[T0 - 1000, 10, 20, 30]] }] }),
      'GET /api/v1/history/events': () => [timeline[0]],
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
    });
    const router = renderApp('/history?range=1h&series=cpu');

    expect(await screen.findByRole('img', { name: 'History of 1 series' })).toBeInTheDocument();
    expect(await screen.findByText('Display driver nvlddmkm stopped responding')).toBeInTheDocument();
    expect(queryOf(fetchMock, '/api/v1/history/series')!.get('ids')).toBe('cpu');

    // Groups start folded, with how many of theirs are chosen; unfolding one shows its series.
    const picker = within(screen.getAllByRole('group', { name: 'Quick sets' })[0]!.parentElement!);
    expect(picker.queryByRole('checkbox', { name: /CPU package temperature/ })).not.toBeInTheDocument();
    await userEvent.click(within(picker.getByRole('region', { name: 'CPU' })).getByRole('button', { expanded: false }));
    await userEvent.click(picker.getByRole('checkbox', { name: /CPU package temperature/ }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ series: 'cpu,cpu.temp' }));

    // Everything at once, then nothing.
    await userEvent.click(picker.getByRole('button', { name: 'Select none' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ series: '' }));
    await userEvent.click(picker.getByRole('button', { name: 'Select all' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ series: 'cpu,cpu.temp' }));

    await userEvent.click(screen.getByRole('radio', { name: '24h' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ range: '24h' }));
  });

  it('a picked moment shows its values and every process running then, with or without stored figures', async () => {
    const at = T0 - 60_000;
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/history/series': () => ({ from: T0 - 3_600_000, to: T0, bucketMs: 4000, series: [{ id: 'cpu', points: [[T0 - 1000, 10, 20, 30]] }] }),
      'GET /api/v1/history/events': () => [],
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
      'GET /api/v1/history/moment': () => ({
        at,
        recorded: true,
        values: [
          { id: 'cpu', label: 'CPU', unit: '%', group: 'System', value: 37 },
          { id: 'sensor.1', label: 'Intel Core Ultra 9 285K / CPU Package', unit: '°C', group: 'Hardware sensors', value: 61 },
        ],
        processSampleTs: at,
        processes: [
          { procKey: 1, pid: 100, name: 'chrome.exe', path: null, cpuPercent: 12, workingSetBytes: 1e9, privateBytes: 8e8, ioReadBytesPerSec: 0, ioWriteBytesPerSec: 0, gpuPercent: null, gpuMemoryBytes: null, threads: 40, handles: 900, state: 'Running', startTs: at - 1000, measured: true },
          { procKey: 2, pid: 200, name: 'notepad.exe', path: 'C:\\Windows\\notepad.exe', cpuPercent: null, workingSetBytes: null, privateBytes: null, ioReadBytesPerSec: null, ioWriteBytesPerSec: null, gpuPercent: null, gpuMemoryBytes: null, threads: null, handles: null, state: 'Running', startTs: at - 5000, measured: false },
          { procKey: 3, pid: 1300, name: 'svchost.exe', path: null, cpuPercent: 0, workingSetBytes: 9e6, privateBytes: 4e6, ioReadBytesPerSec: 0, ioWriteBytesPerSec: 0, gpuPercent: null, gpuMemoryBytes: null, threads: 9, handles: 300, state: 'Running', startTs: at - 9e6, measured: true, figuresTs: at - 20_000 },
        ],
        services: [
          { name: 'Dnscache', displayName: 'DNS Client', status: 'Running', startType: 'Automatic', pid: 1300, procKey: 3 },
          { name: 'nsi', displayName: 'Network Store Interface Service', status: 'Running', startType: 'Automatic', pid: 1300, procKey: 3 },
          { name: 'wuauserv', displayName: 'Windows Update', status: 'Stopped', startType: 'Manual', pid: null, procKey: null },
        ],
      }),
    });
    const router = renderApp(`/history?range=1h&series=cpu&end=${T0}&at=${at}`);

    expect(await screen.findByText('notepad.exe')).toBeInTheDocument();
    expect(queryOf(fetchMock, '/api/v1/history/moment')!.get('t')).toBe(String(at));
    expect(screen.getByText('chrome.exe')).toBeInTheDocument();
    expect(screen.getByText('running · no figures stored')).toBeInTheDocument();
    expect(screen.getByText('Intel Core Ultra 9 285K / CPU Package')).toBeInTheDocument();
    expect(screen.getByText(/3 processes were running; 2 with figures/)).toBeInTheDocument();

    // Figures from the full process list carry their time.
    expect(screen.getByText(/as of/)).toBeInTheDocument();

    // The moment's sections are a key away: S for services, P for processes (and the bar lists them, with the chart).
    const bar = screen.getByRole('navigation', { name: 'Sections of this moment' });
    expect(within(bar).getAllByRole('button').map((b) => b.getAttribute('aria-keyshortcuts'))).toEqual(['V', 'P', 'S', 'C']);
    await userEvent.keyboard('s');
    expect(screen.getByRole('searchbox', { name: 'Filter services' })).toBeInTheDocument();
    await userEvent.keyboard('p');
    expect(screen.getByRole('searchbox', { name: 'Filter processes' })).toBeInTheDocument();
    // Typing in a filter is not a shortcut.
    await userEvent.type(screen.getByRole('searchbox', { name: 'Filter processes' }), 's');
    expect(screen.getByRole('searchbox', { name: 'Filter processes' })).toHaveValue('s');
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Filter processes' }));

    // A process's services lead to the Services tab, and a service back to its process.
    await userEvent.click(screen.getByRole('button', { name: '2 services: DNS Client, Network Store Interface Service' }));
    expect(screen.getByRole('searchbox', { name: 'Filter services' })).toHaveValue('1300');
    expect(screen.getByText('Network Store Interface Service')).toBeInTheDocument();
    expect(screen.queryByText('Windows Update')).not.toBeInTheDocument();
    await userEvent.click(screen.getAllByRole('button', { name: /svchost\.exe/ })[0]!);
    expect(screen.getByRole('searchbox', { name: 'Filter processes' })).toHaveValue('1300');
    expect(screen.queryByText('chrome.exe')).not.toBeInTheDocument();

    // Searching for a service finds the process it runs in.
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Filter processes' }));
    await userEvent.type(screen.getByRole('searchbox', { name: 'Filter processes' }), 'dns');
    expect(screen.getByText('svchost.exe')).toBeInTheDocument();
    expect(screen.queryByText('chrome.exe')).not.toBeInTheDocument();
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Filter processes' }));

    await userEvent.type(screen.getByRole('searchbox', { name: 'Filter processes' }), 'note');
    expect(screen.queryByText('chrome.exe')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Close this moment' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('at'));
  });

  it('a moment when nothing was recording says so', async () => {
    mockApi({
      ...common,
      'GET /api/v1/history/series': () => ({ from: T0 - 3_600_000, to: T0, bucketMs: 4000, series: [] }),
      'GET /api/v1/history/events': () => [],
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
      'GET /api/v1/history/moment': () => ({ at: T0 - 60_000, recorded: false, values: [], processSampleTs: null, processes: [] }),
    });
    renderApp(`/history?range=1h&series=cpu&end=${T0}&at=${T0 - 60_000}`);

    expect(await screen.findByText(/The recorder was not recording at this moment/)).toBeInTheDocument();
  });

  it('a reload keeps the zoomed part of the timeline, the band and the event filter', async () => {
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/history/series': () => ({ from: T0 - 3_600_000, to: T0, bucketMs: 4000, series: [{ id: 'cpu', points: [[T0 - 1000, 10, 20, 30]] }] }),
      'GET /api/v1/history/events': () => [],
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
    });
    const zoomFrom = T0 - 1_800_000;
    const zoomTo = T0 - 900_000;
    const router = renderApp(`/history?range=1h&series=cpu&end=${T0}&zoomFrom=${zoomFrom}&zoomTo=${zoomTo}&band=true&events=3`);

    const chart = await screen.findByRole('img', { name: 'History of 1 series' });
    await waitFor(() => expect(JSON.parse(chart.getAttribute('data-option')!).dataZoom[0]).toMatchObject({ startValue: zoomFrom, endValue: zoomTo }));
    expect(screen.getByRole('checkbox', { name: 'Min–max band' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Serious' })).toHaveAttribute('aria-checked', 'true');
    await waitFor(() => expect(queryOf(fetchMock, '/api/v1/history/events')!.get('min')).toBe('3'));

    // A new window starts unzoomed.
    await userEvent.click(screen.getByRole('radio', { name: '6h' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ range: '6h', series: 'cpu', end: T0, band: true, events: 3 }));
  });

  it('diagnoses the zoomed part of the window, or the whole window', async () => {
    mockApi({
      ...common,
      'GET /api/v1/history/series': () => ({ from: T0 - 3_600_000, to: T0, bucketMs: 4000, series: [{ id: 'cpu', points: [[T0 - 1000, 10, 20, 30]] }] }),
      'GET /api/v1/history/events': () => [],
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
      'POST /api/v1/diagnostics/jobs': () => new Problem(500, { title: 'not now' }),
    });
    const router = renderApp(`/history?range=1h&series=cpu&end=${T0}&zoomFrom=${T0 - 1_800_000}&zoomTo=${T0 - 900_000}`);

    await userEvent.click(await screen.findByRole('button', { name: /Diagnose the zoomed part/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/diagnostics'));
    expect(router.state.location.search).toEqual({ from: T0 - 1_800_000, to: T0 - 900_000 });
  });
});

const commitFinding = {
  severity: 'High',
  level: 'Observation',
  title: 'Commit near its limit',
  text: 'Committed memory at or above 95 % of the commit limit for 11 min in total (once).',
  from: new Date(T0 - 1_200_000).toISOString(),
  to: new Date(T0 - 540_000).toISOString(),
  seriesIds: ['memory.commit'],
  occurrences: 1,
  section: null,
  incidentKey: null,
} as const;

const periodReport: PeriodReport = {
  from: new Date(T0 - 3_600_000).toISOString(),
  to: new Date(T0).toISOString(),
  generatedAt: new Date(T0 + 5_000).toISOString(),
  summary: {
    periodMinutes: 60,
    recordedMinutes: 45,
    findingsBySeverity: { Critical: 0, High: 1, Medium: 1, Low: 0, Info: 1 },
    top: [{ ...commitFinding, seriesIds: ['memory.commit'], section: 'memory' }],
    note: 'Process details cover only the last day.',
  },
  sections: [
    {
      id: 'memory',
      title: 'Memory',
      stats: [
        { label: 'In use', value: 'average 52 %, highest 99 %', seriesId: 'memory' },
        { label: 'Most memory at the low point (11:40:00)', value: 'vmmemWSL 38.9 GB, pythonw.exe 3.4 GB', seriesId: null },
      ],
      findings: [{ ...commitFinding, seriesIds: ['memory.commit'] }],
      tables: [{ title: 'Most memory (private)', columns: ['Process', 'PID', 'Highest'], rows: [['vmmemWSL', '18940', '38.9 GB']] }],
    },
    {
      id: 'incidents',
      title: 'Incidents',
      stats: [{ label: 'Incidents', value: '1', seriesId: null }],
      findings: [
        {
          severity: 'Medium',
          level: 'Observation',
          title: 'Incident INC-20261006-00007',
          text: 'Incident INC-20261006-00007 at 12:00:00: Screen Flicker (captured by the hotkey).',
          from: new Date(T0).toISOString(),
          to: null,
          seriesIds: null,
          occurrences: 1,
          section: null,
          incidentKey: 'INC-20261006-00007',
        },
      ],
      tables: [],
    },
    { id: 'network', title: 'Network', stats: [], findings: [], tables: [] },
  ],
};

const JOB = '20261006-120005-a1b2c3';
const job = (state: DiagnosticJob['state'], extra: Partial<DiagnosticJob> = {}): DiagnosticJob => ({
  id: JOB,
  from: new Date(T0 - 3_600_000).toISOString(),
  to: new Date(T0).toISOString(),
  state,
  createdAt: new Date(T0 + 5_000).toISOString(),
  startedAt: new Date(T0 + 5_000).toISOString(),
  finishedAt: state === 'Completed' ? new Date(T0 + 8_000).toISOString() : null,
  error: null,
  findingsBySeverity: state === 'Completed' ? periodReport.summary.findingsBySeverity : null,
  running: state === 'Queued' || state === 'Reading' || state === 'Analyzing',
  ...extra,
});

describe('Period diagnostic', () => {
  it('diagnoses chosen incidents: their recordings make the period', async () => {
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/diagnostics/jobs': () => [],
      'GET /api/v1/incidents': () => ({ total: 1, items: [summary] }),
      'POST /api/v1/diagnostics/jobs': () => ({ id: '20261015-120000-abcdef', from: '2026-10-06T12:00:00Z', to: '2026-10-06T12:12:00Z', state: 'Queued', createdAt: '2026-10-06T12:30:00Z', startedAt: null, finishedAt: null, error: null, findingsBySeverity: null, incidents: [summary.key], running: true }),
    });
    const router = renderApp('/diagnostics');

    await userEvent.click(await screen.findByRole('radio', { name: 'Incidents' }));
    const list = await screen.findByRole('list', { name: 'Incidents to diagnose' });
    await userEvent.click(within(list).getByRole('checkbox'));
    await userEvent.click(screen.getByRole('button', { name: /Diagnose this incident/ }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/diagnostics/jobs')).toEqual({ incidents: [summary.key], fresh: false }));
    await waitFor(() => expect(router.state.location.search).toEqual({ job: '20261015-120000-abcdef' }));
  });

  it('runs in the background: starts for the period in the URL, shows its progress, then the report linked to the data', async () => {
    let polls = 0;
    const fetchMock = mockApi({
      ...common,
      'POST /api/v1/diagnostics/jobs': () => job('Queued'),
      'GET /api/v1/diagnostics/jobs': () => [job('Reading')],
      [`GET /api/v1/diagnostics/jobs/${JOB}`]: () => (++polls < 2 ? { job: job('Reading'), report: null } : { job: job('Completed'), report: periodReport }),
    });
    const router = renderApp(`/diagnostics?from=${T0 - 3_600_000}&to=${T0}`);

    // Started once, then followed by its ID (so leaving and coming back finds it).
    await waitFor(() => expect(router.state.location.search).toEqual({ job: JOB }));
    expect(bodyOf(fetchMock, 'POST', '/api/v1/diagnostics/jobs')).toEqual({ from: T0 - 3_600_000, to: T0, fresh: false });
    expect(await screen.findByText(/Reading everything recorded in the period/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Cancel/ })).toBeInTheDocument();

    expect(await screen.findByRole('heading', { name: 'Most important findings' }, { timeout: 4000 })).toBeInTheDocument();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
    const counts = screen.getByRole('list', { name: 'Findings by severity' });
    expect(within(counts).getByText('1 high')).toBeInTheDocument();
    expect(within(counts).getByText('0 critical')).toBeInTheDocument();
    expect(screen.getByText('Process details cover only the last day.')).toBeInTheDocument();

    // Every finding links to History around it with its series and the moment picked; an incident to its page.
    const [history] = screen.getAllByRole('link', { name: /Show in History/ }) as [HTMLElement];
    const target = new URL(history.getAttribute('href')!, 'http://x');
    expect(target.pathname).toBe('/history');
    expect(target.searchParams.get('series')).toContain('memory.commit');
    expect(target.searchParams.get('at')).toBe(String(T0 - 1_200_000));
    expect(screen.getByRole('link', { name: /Open INC-20261006-00007/ })).toHaveAttribute('href', '/incidents/INC-20261006-00007');

    // Sections: figures, tables (groupable and sortable), and a section where nothing stood out.
    const memory = document.getElementById('section-memory')!;
    // The figures and program names are marked inside the value (sizes bold, programs as code).
    const value = within(memory).getByText((_, el) => el?.tagName === 'DD' && el.textContent === 'vmmemWSL 38.9 GB, pythonw.exe 3.4 GB');
    expect(within(value).getByText('38.9 GB')).toHaveClass('font-semibold');
    expect(within(value).getByText('pythonw.exe').tagName).toBe('CODE');
    expect(within(memory).getByRole('columnheader', { name: /Highest/ })).toBeInTheDocument();
    expect(within(document.getElementById('section-network')!).getByText('nothing stood out')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Export/ }));
    expect(screen.getByRole('menuitem', { name: /Report \(PDF\)/ })).toHaveAttribute('href', `/api/v1/diagnostics/jobs/${JOB}/export?format=pdf`);

    // Run again analyzes afresh instead of reusing the report.
    await userEvent.click(screen.getByRole('button', { name: /Run again/ }));
    await waitFor(() => expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(2));
    expect(JSON.parse(String(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')[1]![1]!.body))).toMatchObject({ fresh: true });
  });

  it('a running diagnostic can be cancelled', async () => {
    let cancelled = false;
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/diagnostics/jobs': () => [job(cancelled ? 'Cancelled' : 'Queued')],
      [`GET /api/v1/diagnostics/jobs/${JOB}`]: () => ({ job: cancelled ? job('Cancelled', { error: 'Cancelled.' }) : job('Queued'), report: null }),
      [`DELETE /api/v1/diagnostics/jobs/${JOB}`]: () => {
        cancelled = true;
        return undefined;
      },
    });
    renderApp(`/diagnostics?job=${JOB}`);

    expect(await screen.findByText(/Waiting for another diagnostic to finish/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Cancel/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Cancelled');
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(true);
    expect(screen.getByRole('button', { name: /Run again/ })).toBeInTheDocument();
  });

  it('coming back lists the running and recent diagnostics, and a new one starts from the chooser', async () => {
    const done = job('Completed', { id: '20261006-110000-ffffff', from: new Date(T0 - 7_200_000).toISOString(), createdAt: new Date(T0 - 3_000_000).toISOString() });
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/diagnostics/jobs': () => [job('Analyzing'), done],
      'POST /api/v1/diagnostics/jobs': () => job('Queued'),
      [`GET /api/v1/diagnostics/jobs/${JOB}`]: () => ({ job: job('Analyzing'), report: null }),
    });
    const router = renderApp('/diagnostics');

    const recent = await screen.findByRole('table', { name: 'Recent diagnostics' });
    expect(within(recent).getAllByRole('row')).toHaveLength(3);
    expect(within(recent).getByText('1 high')).toBeInTheDocument();
    expect(within(recent).getByText('Analyzing')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Follow the running diagnostic' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ job: JOB }));
    expect(await screen.findByText(/^Analyzing…/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Last hour' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/diagnostics/jobs') && init?.method === 'POST')).toBe(true));
    const sent = bodyOf(fetchMock, 'POST', '/api/v1/diagnostics/jobs') as { from: number; to: number };
    expect(sent.to - sent.from).toBe(3_600_000);
  });

  it('shows why a period was refused', async () => {
    mockApi({
      ...common,
      'GET /api/v1/diagnostics/jobs': () => [],
      'POST /api/v1/diagnostics/jobs': () => new Problem(400, { title: 'Invalid period', detail: 'The period must be at least a minute long and not in the future.' }),
    });
    renderApp('/diagnostics');

    expect(await screen.findByRole('button', { name: /Run diagnostic/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Last hour' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('at least a minute long');
  });
});

describe('Incidents', () => {
  it('filters the list through the URL', async () => {
    const fetchMock = mockApi({ ...common, 'GET /api/v1/incidents': () => ({ total: 1, items: [summary] }) });
    const router = renderApp('/incidents');

    expect(await screen.findByText('Screen Flicker')).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'What happened' }), 'GameCrash');

    await waitFor(() => expect(router.state.location.search).toMatchObject({ type: 'GameCrash' }));
    await waitFor(() => expect(queryOf(fetchMock, '/api/v1/incidents')!.get('type')).toBe('GameCrash'));
  });

  it('investigates: a timeline click picks the moment, notes and pin are saved, delete asks first', async () => {
    const base = '/api/v1/incidents/INC-20261006-00007';
    const fetchMock = mockApi({
      ...common,
      [`GET ${base}`]: () => detail,
      [`GET ${base}/timeline`]: () => timeline,
      [`GET ${base}/catalog`]: () => common['GET /api/v1/history/catalog'](),
      [`GET ${base}/series`]: () => ({ from: detail.from, to: detail.to, bucketMs: 1000, series: [] }),
      [`GET ${base}/processes`]: () => ({ sampleTs: T0 - 20_000, processes: [] }),
      [`PATCH ${base}`]: () => undefined,
      [`DELETE ${base}`]: () => undefined,
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
    });
    const router = renderApp('/incidents/INC-20261006-00007');

    expect(await screen.findByRole('heading', { name: /Screen Flicker/ })).toBeInTheDocument();
    await userEvent.click((await screen.findAllByRole('button', { name: /nvlddmkm stopped responding/ }))[0]!);
    await waitFor(() => expect(router.state.location.search).toEqual({ t: T0 - 20_000 }));
    expect(screen.getByText(/±30 s/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('tab', { name: 'Processes' }));
    await waitFor(() => expect(queryOf(fetchMock, `${base}/processes`)!.get('t')).toBe(String(T0 - 20_000)));

    await userEvent.type(screen.getByRole('textbox', { name: 'Notes' }), 'happened after the driver update');
    await userEvent.click(screen.getByRole('button', { name: 'Save notes' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'PATCH', base)).toEqual({ notes: 'happened after the driver update' }));

    await userEvent.click(screen.getByRole('button', { name: /Delete/ }));
    const dialog = screen.getByRole('dialog', { name: 'Delete INC-20261006-00007?' });
    await userEvent.click(within(dialog).getByRole('button', { name: /Delete/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/incidents'));
    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === base && init?.method === 'DELETE')).toBe(true);
  });

  it('marks processes whose program is not signed or whose signature is not valid', async () => {
    const base = '/api/v1/incidents/INC-20261006-00007';
    const process = (procKey: number, name: string, signature: SignatureStatus, signer: string | null) => ({
      procKey, pid: procKey, name, path: `C:\\Apps\\${name}`, cpuPercent: 10, workingSetBytes: 1e8, privateBytes: 1e8, ioReadBytesPerSec: null,
      ioWriteBytesPerSec: null, gpuPercent: null, gpuMemoryBytes: null, threads: 10, handles: 100, state: 'Running', signer, signature,
    });
    mockApi({
      ...common,
      [`GET ${base}`]: () => detail,
      [`GET ${base}/timeline`]: () => timeline,
      [`GET ${base}/catalog`]: () => common['GET /api/v1/history/catalog'](),
      [`GET ${base}/series`]: () => ({ from: detail.from, to: detail.to, bucketMs: 1000, series: [] }),
      [`GET ${base}/processes`]: () => ({
        sampleTs: T0,
        processes: [process(1, 'game.exe', 'Embedded', 'Game Studio'), process(2, 'tool.exe', 'NotSigned', null), process(3, 'patched.exe', 'NotTrusted', 'Contoso')],
      }),
    });
    renderApp('/incidents/INC-20261006-00007');

    await userEvent.click(await screen.findByRole('tab', { name: 'Processes' }));

    expect(await screen.findByText('Not signed')).toBeInTheDocument();
    expect(screen.getByText('Signature not valid')).toBeInTheDocument();
    expect(screen.getByText('game.exe').closest('[title]')).toHaveAttribute('title', expect.stringContaining('Signed by Game Studio'));
  });

  it('shows the diagnostic summary in sections and follows evidence to the data', async () => {
    const base = '/api/v1/incidents/INC-20261006-00007';
    const findings: FindingEntry[] = [
      { ordinal: 1, level: 'Observation', text: 'The core clock dropped from 2050 MHz to 300 MHz.', evidence: [{ kind: 'metric', ts: T0 - 3_000, sourceId: 'gpu.3.core' }] },
      { ordinal: 2, level: 'ConfirmedEvent', text: 'Windows logged "Display driver nvlddmkm stopped responding".', evidence: [{ kind: 'event', ts: T0 - 2_000, sourceId: 'WindowsEvents:1' }] },
      { ordinal: 3, level: 'Observation', text: 'No significant RAM pressure was observed.', evidence: [] },
      { ordinal: 4, level: 'Correlation', text: 'GPU and display activity coincided with the reported screen flicker.', evidence: [] },
      { ordinal: 5, level: 'PossibleExplanation', text: 'It could be consistent with a display driver or GPU problem.', evidence: [] },
      { ordinal: 6, level: 'NotProven', text: 'The collected telemetry does not establish that the GPU caused the reported screen flicker.', evidence: [] },
    ];
    const fetchMock = mockApi({
      ...common,
      [`GET ${base}`]: () => ({ ...detail, info: { ...detail.info, analyzed_utc: '2026-10-06T12:03:00Z' } }),
      [`GET ${base}/timeline`]: () => timeline,
      [`GET ${base}/catalog`]: () => [...common['GET /api/v1/history/catalog'](), { id: 'gpu.3.core', label: 'RTX 4090 core clock', unit: 'MHz', group: 'GPU', device: 'RTX 4090', longTerm: false }],
      [`GET ${base}/series`]: () => ({ from: detail.from, to: detail.to, bucketMs: 1000, series: [] }),
      [`GET ${base}/findings`]: () => findings,
    });
    const router = renderApp('/incidents/INC-20261006-00007');

    await userEvent.click(await screen.findByRole('tab', { name: 'Findings' }));
    const observations = await screen.findByRole('region', { name: 'Observations' });
    expect(within(observations).getAllByRole('listitem')).toHaveLength(3);
    expect(within(observations).getByText('Confirmed event')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Possible correlations' })).getByText('Correlation detected')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Not proven' })).getByText(/does not establish/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /RTX 4090 core clock at/ }));
    await waitFor(() => expect(router.state.location.search).toEqual({ t: T0 - 3_000 }));
    await waitFor(() => expect(queryOf(fetchMock, `${base}/series`)!.get('ids')).toContain('gpu.3.core'));
  });

  it('offers the reports and data files as downloads', async () => {
    const base = '/api/v1/incidents/INC-20261006-00007';
    mockApi({
      ...common,
      [`GET ${base}`]: () => detail,
      [`GET ${base}/timeline`]: () => timeline,
      [`GET ${base}/catalog`]: () => common['GET /api/v1/history/catalog'](),
      [`GET ${base}/series`]: () => ({ from: detail.from, to: detail.to, bucketMs: 1000, series: [] }),
    });
    renderApp('/incidents/INC-20261006-00007');

    await userEvent.click(await screen.findByRole('button', { name: /Export/ }));
    const menu = screen.getByRole('menu', { name: 'Export' });
    expect(within(menu).getByRole('menuitem', { name: /Technical report \(PDF\)/ })).toHaveAttribute('href', `${base}/export?format=pdf&report=technical`);
    expect(within(menu).getByRole('menuitem', { name: /Everything \(ZIP\)/ })).toHaveAttribute('href', `${base}/export?format=zip`);
    expect(within(menu).getAllByRole('menuitem')).toHaveLength(10);

    // A copy can be kept in the storage locations too (CSV files are only downloaded).
    await userEvent.click(within(menu).getByRole('menuitemcheckbox', { name: /Keep a copy too/ }));
    expect(within(menu).getByRole('menuitem', { name: /Everything \(ZIP\)/ })).toHaveAttribute('href', `${base}/export?format=zip&keep=true`);
    expect(within(menu).getByRole('menuitem', { name: /Metrics \(CSV\)/ })).toHaveAttribute('href', `${base}/export?format=csv&file=metrics`);
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('explains an incident that does not exist', async () => {
    mockApi({ ...common, 'GET /api/v1/incidents/INC-20261006-00099': () => new Problem(404, { title: 'Incident not found' }) });
    renderApp('/incidents/INC-20261006-00099');

    expect(await screen.findByText(/does not exist/)).toBeInTheDocument();
  });
});

describe('The new look (phase 4)', () => {
  it('opens an incident in a drawer over the list, with the way into its investigation', async () => {
    mockApi({ ...common, 'GET /api/v1/incidents': () => ({ total: 1, items: [summary] }) });
    {
      renderApp('/incidents');
      await userEvent.click(await screen.findByRole('button', { name: /Screen Flicker/ }));
      const drawer = await screen.findByRole('dialog', { name: 'Screen Flicker' });
      expect(within(drawer).getByText(summary.key)).toBeInTheDocument();
      expect(within(drawer).getByRole('link', { name: /Investigate/ })).toHaveAttribute('href', `/incidents/${summary.key}`);
      await userEvent.keyboard('{Escape}');
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Screen Flicker' })).toBeNull());
    }
  });

  it('shows one settings topic at a time, chosen in the list', async () => {
    mockApi({ ...common, 'GET /api/v1/settings': () => settings, 'GET /api/v1/triggers': () => [] });
    {
      renderApp('/settings');
      const topics = await screen.findByRole('navigation', { name: 'Settings topics' });
      await userEvent.click(within(topics).getByRole('button', { name: /Sampling/ }));
      expect(await screen.findByRole('region', { name: 'Sampling' })).toBeInTheDocument();
      expect(screen.queryByRole('region', { name: 'Recording' })).toBeNull();
      expect(within(topics).getByRole('button', { name: /Sampling/ })).toHaveAttribute('aria-current', 'page');
    }
  });
});

/** Opens a rule category (they start closed). */
async function openCategory(name: string) {
  await userEvent.click(await screen.findByRole('button', { name: new RegExp(`^${name}\\s*·`) }));
}

/** Opens a settings topic (one is shown at a time). */
async function openTopic(name: RegExp) {
  const topics = await screen.findByRole('navigation', { name: 'Settings topics' });
  await userEvent.click(within(topics).getByRole('button', { name }));
}

describe('Trigger settings', () => {
  const rules: TriggerRuleInfo[] = [
    {
      id: 'cpu-high', name: 'High CPU load', classification: 'CPU saturation', kind: 'ThresholdDuration', metric: 'CPU utilization', perDevice: false,
      description: 'CPU utilization above 95 % for at least 30 s', note: 'Full CPU load is normal while compiling.', severity: 'Medium', unit: '%',
      thresholdMin: 1, thresholdMax: 100, usesThreshold: true, usesDuration: true, usesWindow: false,
      defaults: { enabled: true, createIncident: false, threshold: 95, durationSeconds: 30, windowSeconds: 0, cooldownMinutes: 30 },
      defaultCategory: 'processor',
    },
    {
      id: 'gpu-reset', name: 'GPU reset (TDR)', classification: 'GPU reset', kind: 'Event', metric: null, perDevice: false,
      description: 'Windows logs that the display driver stopped responding and was reset', note: null, severity: 'High', unit: '',
      thresholdMin: 0, thresholdMax: 0, usesThreshold: false, usesDuration: false, usesWindow: false,
      defaults: { enabled: true, createIncident: true, threshold: 0, durationSeconds: 0, windowSeconds: 0, cooldownMinutes: 30 },
      defaultCategory: 'events',
    },
  ];

  it('saves only what differs from the shipped rules', async () => {
    const withTriggers = { ...settings, settings: { ...settings.settings, triggers: { automaticIncidents: true, maxIncidentsPerHour: 4, rules: {} } } };
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => withTriggers,
      'GET /api/v1/triggers': () => rules,
      'PUT /api/v1/settings': (_url, init) => ({ ...withTriggers, settings: JSON.parse(String(init!.body)) as SettingsResponse['settings'] }),
    });
    renderApp('/settings');
    await openTopic(/Automatic incidents/);
    expect(await screen.findByRole('button', { name: /^Processors*·/ })).toHaveAttribute('aria-expanded', 'false');
    await openCategory('Processor');
    await openCategory('Events');

    const section = await screen.findByRole('region', { name: 'Processor' });
    await userEvent.click(within(section).getByRole('checkbox', { name: 'High CPU load: creates an incident' }));
    const threshold = within(section).getByRole('spinbutton', { name: 'Threshold' });
    await userEvent.clear(threshold);
    await userEvent.type(threshold, '90');
    await userEvent.click(within(screen.getByRole('region', { name: 'Events' })).getByRole('checkbox', { name: 'GPU reset (TDR): on' }));
    // Changing a value back to the shipped one drops it.
    const cooldown = within(section).getByRole('spinbutton', { name: 'Cooldown' });
    await userEvent.clear(cooldown);
    await userEvent.type(cooldown, '30');
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await screen.findByText('Settings saved');
    const sent = bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings'];
    expect(sent.triggers!.rules).toEqual({ 'cpu-high': { createIncident: true, threshold: 90 }, 'gpu-reset': { enabled: false } });
  });

  it('adds a rule of your own and deletes a shipped one after confirming', async () => {
    const withTriggers = { ...settings, settings: { ...settings.settings, triggers: { automaticIncidents: true, maxIncidentsPerHour: 4, rules: {} } } };
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => withTriggers,
      'GET /api/v1/triggers': () => rules,
      'GET /api/v1/triggers/choices': () => ({
        metrics: [{ id: 'gpu.vram', label: 'VRAM in use', unit: '%', perDevice: true, min: 1, max: 100 }],
        processEvents: ['Crashed', 'Hung'],
        displayChanges: ['Disconnected'],
        windowsEvents: [{ id: 'gpu.tdr', title: 'Display driver stopped responding and recovered (TDR)' }],
        categories: [
          { id: 'processor', name: 'Processor' },
          { id: 'graphics', name: 'Graphics' },
          { id: 'events', name: 'Events' },
        ],
      }),
      'GET /api/v1/triggers/readings': () => [{ id: 'sensor/gpu/0/temperature/1', label: 'GPU Hot Spot', group: 'NVIDIA GeForce RTX 4090', unit: '°C' }],
      'PUT /api/v1/settings': (_url, init) => ({ ...withTriggers, settings: JSON.parse(String(init!.body)) as SettingsResponse['settings'] }),
    });
    renderApp('/settings');
    await openTopic(/Automatic incidents/);

    await userEvent.click(await screen.findByRole('button', { name: 'Add a rule' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add a rule' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'VRAM full while gaming');
    const duration = within(dialog).getByRole('spinbutton', { name: 'For at least (s)' });
    await userEvent.clear(duration);
    await userEvent.type(duration, '150');
    expect(within(dialog).getByText('VRAM in use above 90 % for at least 150 s.')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add the rule' }));
    // Its category opens and the new rule has the focus.
    expect(screen.getByRole('button', { name: /^Graphicss*·/ })).toHaveAttribute('aria-expanded', 'true');
    expect(within(screen.getByRole('region', { name: 'Graphics' })).getByText('VRAM full while gaming')).toBeInTheDocument();
    await waitFor(() => expect(document.activeElement?.id).toBe('rule-my-rule-1'));

    // A shipped rule is deleted only after confirming, and can be brought back.
    await openCategory('Events');
    await userEvent.click(within(screen.getByRole('region', { name: 'Events' })).getByRole('button', { name: 'Delete GPU reset (TDR)' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Delete GPU reset (TDR)?' })).getByRole('button', { name: 'Delete' }));
    expect(within(await screen.findByRole('region', { name: 'Deleted rules' })).getByRole('button', { name: /Bring back/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/settings')).toBeTruthy());
    const sent = bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings'];
    expect(sent.triggers!.removed).toEqual(['gpu-reset']);
    expect(sent.triggers!.custom).toEqual([
      expect.objectContaining({ id: 'my-rule-1', name: 'VRAM full while gaming', kind: 'ThresholdDuration', metric: 'gpu.vram', threshold: 90, durationSeconds: 150, createIncident: true }),
    ]);
  });
});

describe('Rule categories and readings', () => {
  it('manages categories, moves a rule, and builds rules on any reading and on events of its own', async () => {
    const shippedRule: TriggerRuleInfo = {
      id: 'cpu-high', name: 'High CPU load', classification: 'CPU saturation', kind: 'ThresholdDuration', metric: 'CPU utilization', perDevice: false,
      description: 'CPU utilization above 95 % for at least 30 s', note: null, severity: 'Medium', unit: '%',
      thresholdMin: 1, thresholdMax: 100, usesThreshold: true, usesDuration: true, usesWindow: false,
      defaults: { enabled: true, createIncident: false, threshold: 95, durationSeconds: 30, windowSeconds: 0, cooldownMinutes: 30 },
      defaultCategory: 'processor',
    };
    const withTriggers = { ...settings, settings: { ...settings.settings, triggers: { automaticIncidents: true, maxIncidentsPerHour: 4, rules: {} } } };
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => withTriggers,
      'GET /api/v1/triggers': () => [shippedRule],
      'GET /api/v1/triggers/choices': () => ({
        metrics: [{ id: 'gpu.vram', label: 'VRAM in use', unit: '%', perDevice: true, min: 1, max: 100 }],
        processEvents: ['Crashed', 'Hung'],
        displayChanges: ['Disconnected'],
        windowsEvents: [{ id: 'gpu.tdr', title: 'Display driver stopped responding and recovered (TDR)' }],
        categories: [
          { id: 'processor', name: 'Processor' },
          { id: 'sensors', name: 'Sensors and other readings' },
          { id: 'events', name: 'Events' },
        ],
      }),
      'GET /api/v1/triggers/readings': () => [
        { id: 'sensor.lhm/gpu-nvidia/0/temperature/1', label: 'GPU Hot Spot', group: 'NVIDIA GeForce RTX 4090', unit: '°C' },
        { id: 'sensor.lhm/lpc/nct6701d/0/fan/1', label: 'CPU fan', group: 'Nuvoton NCT6701D', unit: 'RPM' },
      ],
      'GET /api/v1/triggers/suggestions': () => ({ processes: ['game.exe', 'chrome.exe'], displays: ['PG32UCDP'], channels: ['System', 'Application'], providers: ['Microsoft-Windows-Kernel-Power'] }),
      'PUT /api/v1/settings': (_url, init) => ({ ...withTriggers, settings: JSON.parse(String(init!.body)) as SettingsResponse['settings'] }),
    });
    renderApp('/settings');
    await openTopic(/Automatic incidents/);

    // A category of its own, and the shipped rule moved into it; an empty built-in one deleted after confirming.
    await userEvent.click(await screen.findByRole('button', { name: 'Categories' }));
    const manager = screen.getByRole('region', { name: 'Categories' });
    await userEvent.click(within(manager).getByRole('button', { name: 'Add a category' }));
    const name = within(manager).getByRole('textbox', { name: 'Name of New category' });
    await userEvent.clear(name);
    await userEvent.type(name, 'Gaming');
    await openCategory('Processor');
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Category of High CPU load' }), 'Gaming');
    await openCategory('Gaming');
    expect(within(screen.getByRole('region', { name: 'Gaming' })).getByText('High CPU load')).toBeInTheDocument();
    // A category moves among the others from its own header.
    await userEvent.click(within(screen.getByRole('region', { name: 'Gaming' })).getByRole('button', { name: 'Move Gaming up' }));
    await userEvent.click(within(manager).getByRole('button', { name: 'Delete the category Events' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Delete the category Events?' })).getByRole('button', { name: 'Delete' }));

    // A rule on one of the system's readings, found by searching.
    await userEvent.click(screen.getByRole('button', { name: 'Add a rule' }));
    let dialog = await screen.findByRole('dialog', { name: 'Add a rule' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Fan stopped');
    const readings = within(dialog).getByRole('listbox', { name: 'Readings' });
    // The groups start closed; a search opens those it finds.
    expect(within(readings).queryByRole('option')).toBeNull();
    await userEvent.click(within(readings).getByRole('button', { name: /^NVIDIA GeForce RTX 4090/ }));
    expect(within(readings).getByRole('option', { name: /GPU Hot Spot/ })).toBeInTheDocument();
    await userEvent.type(within(dialog).getByRole('searchbox'), 'fan');
    expect(within(readings).queryByText('GPU Hot Spot')).not.toBeInTheDocument();
    await userEvent.click(within(readings).getByRole('option', { name: /CPU fan/ }));
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Direction' }), 'Below');
    const value = within(dialog).getByRole('spinbutton', { name: 'Value (RPM)' });
    await userEvent.clear(value);
    await userEvent.type(value, '200');
    expect(within(dialog).getByText('Nuvoton NCT6701D › CPU fan below 200 RPM for at least 300 s.')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add the rule' }));

    // An event rule narrowed to one program, with a Windows event of its own.
    await userEvent.click(screen.getByRole('button', { name: 'Add a rule' }));
    dialog = await screen.findByRole('dialog', { name: 'Add a rule' });
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Game trouble');
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'It watches' }), 'event');
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Crashed' }));
    await userEvent.type(within(dialog).getByRole('combobox', { name: 'Only these programs' }), 'game.exe{Enter}');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add a Windows event' }));
    const row = within(dialog).getByRole('group', { name: 'Windows event 1' });
    await userEvent.type(within(row).getByRole('combobox', { name: 'Source' }), 'Microsoft-Windows-Kernel-Power');
    await userEvent.type(within(row).getByRole('textbox', { name: 'Event IDs' }), '41');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add the rule' }));
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/settings')).toBeTruthy());
    const sent = (bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings']).triggers!;
    // Gaming was moved above Events, which was then deleted.
    expect(sent.categories!.map((c) => c.name)).toEqual(['Processor', 'Sensors and other readings', 'Gaming']);
    expect(sent.ruleCategories).toEqual({ 'cpu-high': 'new-category' }); // the ID is kept when renamed
    expect(sent.custom).toEqual([
      // The category followed the measurement (a fan goes with the sensors).
      expect.objectContaining({ name: 'Fan stopped', metric: 'reading:sensor.lhm/lpc/nct6701d/0/fan/1', category: 'sensors', readingLabel: 'Nuvoton NCT6701D › CPU fan', readingUnit: 'RPM', direction: 'Below', threshold: 200 }),
      expect.objectContaining({
        name: 'Game trouble', kind: 'Event', processEvents: ['Crashed'], processNames: ['game.exe'],
        windowsEvents: [{ channel: 'System', provider: 'Microsoft-Windows-Kernel-Power', eventIds: [41], levels: [] }],
      }),
    ]);
  });
});

describe('Enhanced mode settings', () => {
  it('turns on the flight recorder and keeps per-process I/O off', async () => {
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => settings,
      'GET /api/v1/triggers': () => [],
      'PUT /api/v1/settings': (_url, init) => ({ ...settings, settings: JSON.parse(String(init!.body)) as SettingsResponse['settings'] }),
    });
    renderApp('/settings');
    await openTopic(/Enhanced mode/);

    await userEvent.click(await screen.findByRole('checkbox', { name: /Flight recorder trace with each incident/ }));
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await screen.findByText('Settings saved');
    const sent = bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings'];
    expect(sent.enhanced).toEqual({ flightRecorder: true, processIo: false });
  });
});

describe('Scheduled recording', () => {
  it('lets a time window record only a chosen selection', async () => {
    const scheduled = {
      ...settings,
      settings: { ...settings.settings, recorder: { ...settings.settings.recorder, mode: 'Scheduled' as const, schedule: [{ days: ['Monday'], start: '09:00:00', end: '18:00:00' }] } },
    };
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => scheduled,
      'GET /api/v1/triggers': () => [],
      'GET /api/v1/recorder/catalog': () => ({
        sources: [
          { id: 'cpu', label: 'CPU', kind: 'cpu', items: [{ id: 'cpu.usage', label: 'Utilization' }, { id: 'cpu.thermal', label: 'Package temperature and power' }] },
          { id: 'memory', label: 'Memory', kind: 'memory', items: [{ id: 'memory.usage', label: 'Usage' }] },
        ],
      }),
      'PUT /api/v1/settings': (_url, init) => ({ ...scheduled, settings: JSON.parse(String(init!.body)) as SettingsResponse['settings'] }),
    });
    renderApp('/settings');
    await openTopic(/^Recording/);

    expect(await screen.findByText('everything')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Choose what window 1 records' }));
    const dialog = await screen.findByRole('dialog', { name: 'What window 1 records' });
    await waitFor(() => expect(within(dialog).getByText('Everything selected')).toBeInTheDocument());
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Memory' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Use this selection' }));
    expect(screen.getByText('a custom selection (2 items)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));

    await screen.findByText('Settings saved');
    const sent = bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings'];
    expect(sent.recorder.schedule[0]!.selection).toEqual({ items: ['cpu.usage', 'cpu.thermal'] });
  });
});

describe('Access from other devices', () => {
  const lan: LanStatus = {
    supported: true,
    unsupported: null,
    enabled: true,
    listening: true,
    port: 47443,
    urls: ['https://192.168.1.151:47443/', 'https://gaming-pc:47443/'],
    fingerprint: 'AB:CD:EF',
    problem: null,
    devices: [{ id: 'a1b2c3d4', name: 'Phone', pairedAt: '2026-10-06T12:00:00Z', lastSeenAt: '2026-10-06T12:30:00Z', lastAddress: '192.168.1.42' }],
    pairing: null,
    installId: '0123456789abcdef',
    apkUrl: '/app/pcdiagnostic.apk',
    discoveryPort: 47444,
  };

  it('shows the addresses and fingerprint, pairs with a code and removes devices', async () => {
    let status = lan;
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => settings,
      'GET /api/v1/lan': () => status,
      'POST /api/v1/lan/pairing-code': () => {
        const link = 'pcdiag://pair?host=192.168.1.151&port=47443&code=12345678&id=0123456789abcdef&fp=ABCDEF';
        status = { ...status, pairing: { code: '1234-5678', expiresAt: new Date(Date.now() + 300_000).toISOString(), link } };
        return { code: '1234-5678', expiresAt: status.pairing!.expiresAt };
      },
      'DELETE /api/v1/lan/devices/a1b2c3d4': () => undefined,
    });
    renderApp('/settings');
    await openTopic(/Other devices/);

    expect(await screen.findByText('https://192.168.1.151:47443/')).toBeInTheDocument();
    expect(screen.getByText('AB:CD:EF')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Show pairing code/ }));
    expect(await screen.findByText('1234-5678')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'QR code to pair the Android app' })).toHaveAttribute('data-qr', expect.stringContaining('pcdiag://pair?host=192.168.1.151'));
    expect(screen.getByRole('img', { name: 'QR code to download the Android app' })).toHaveAttribute('data-qr', 'https://192.168.1.151:47443/app/pcdiagnostic.apk');
    await userEvent.click(screen.getByRole('button', { name: 'Remove Phone' }));
    await confirmIt();
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/lan/devices/a1b2c3d4') && init?.method === 'DELETE')).toBe(true));
  });

  it('tells a paired device that network access is managed on the PC', async () => {
    mockApi({ ...common, 'GET /api/v1/settings': () => settings, 'GET /api/v1/lan': () => new Problem(403, { title: 'Only on the PC itself' }) });
    renderApp('/settings');
    await openTopic(/Other devices/);

    expect(await screen.findByText(/You are on a paired device/)).toBeInTheDocument();
  });
});

describe('System and Settings', () => {
  it('shows inventory, drivers and why readings are unavailable', async () => {
    mockApi({ ...common, 'GET /api/v1/system': () => emptySystem });
    const router = renderApp('/system');

    expect(await screen.findByText('AMD Ryzen 9 7950X')).toBeInTheDocument();
    // The overview says what needs attention, and each topic has its own tab (kept in the address).
    expect(within(screen.getByRole('region', { name: 'Needs attention' })).getByText('1 reading is unavailable on this PC')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: /Drivers/ }));
    expect(screen.getByText('32.0.15.7000')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: /Readings/ }));
    expect(screen.getByText('Needs the recorder to run as administrator')).toBeInTheDocument();
    expect(screen.getByText('Run the recorder as administrator; the installed recorder service does.')).toBeInTheDocument();
    expect(router.state.location.search).toEqual({ tab: 'readings' });
  });

  it('finds a setting by name and marks the topics with unsaved changes', async () => {
    mockApi({ ...common, 'GET /api/v1/settings': () => settings, 'GET /api/v1/triggers': () => [] });
    renderApp('/settings');

    const topics = await screen.findByRole('navigation', { name: 'Settings topics' });
    await userEvent.type(within(topics).getByRole('searchbox', { name: 'Find a setting' }), 'hotkey');
    expect(within(topics).getAllByRole('button').map((b) => b.textContent)).toEqual(['Recorder app']);
    expect(screen.getByRole('textbox', { name: 'Capture hotkey' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Storage' })).not.toBeInTheDocument();

    await userEvent.clear(within(topics).getByRole('searchbox', { name: 'Find a setting' }));
    await openTopic(/Privacy/);
    await userEvent.click(screen.getByRole('checkbox', { name: /Record user names/ }));
    expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes in Privacy');
    expect(within(topics).getByRole('button', { name: /Privacy/ })).toContainElement(within(topics).getByLabelText('unsaved changes'));
    expect(within(screen.getByRole('region', { name: /Privacy/ })).getByText('Unsaved')).toBeInTheDocument();
  });

  it('saves changed settings and shows the problems the server reports', async () => {
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/settings': () => settings,
      'PUT /api/v1/settings': (_url, init) => {
        const body = JSON.parse(String(init!.body)) as SettingsResponse['settings'];
        return body.recorder.mode === 'Scheduled' && body.recorder.schedule.length === 0
          ? new Problem(400, { title: 'Some settings are not valid', errors: { settings: ['Recorder:Schedule must contain at least one window when Recorder:Mode is Scheduled.'] } })
          : { ...settings, settings: body };
      },
    });
    renderApp('/settings');

    const save = await screen.findByRole('button', { name: /^Save$/ });
    expect(save).toBeDisabled();
    await openTopic(/^Recording/);
    await userEvent.click(screen.getByRole('radio', { name: /Always/ }));
    await openTopic(/Privacy/);
    await userEvent.click(screen.getByRole('checkbox', { name: /Record process command lines/ }));
    await userEvent.click(save);
    await screen.findByText('Settings saved');
    const sent = bodyOf(fetchMock, 'PUT', '/api/v1/settings') as SettingsResponse['settings'];
    expect(sent.recorder.mode).toBe('Always');
    expect(sent.privacy.captureCommandLines).toBe(true);

    // Out-of-range numbers are flagged in the form (and the browser blocks submitting them).
    await openTopic(/^Storage/);
    const retention = screen.getByRole('spinbutton', { name: 'Full detail kept for' });
    await userEvent.clear(retention);
    await userEvent.type(retention, '99');
    expect(retention).toHaveAttribute('aria-invalid', 'true');
    await userEvent.clear(retention);
    await userEvent.type(retention, '48');

    // Rules only the server knows are reported from its answer.
    await openTopic(/^Recording/);
    await userEvent.click(screen.getByRole('radio', { name: /Scheduled/ }));
    expect(screen.getByText('Add at least one time window.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^Save$/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Schedule must contain at least one window');
  });
});

