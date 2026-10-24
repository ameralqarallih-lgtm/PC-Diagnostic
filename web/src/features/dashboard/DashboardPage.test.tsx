import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfirmProvider } from '@/components/ui/confirm';
import { ToastProvider } from '@/components/ui/toast';
import { confirmIt } from '@/test/app';
import { createAppRouter } from '@/router';
import { agentStatus, emptyIncidents, FakeEventSource, liveMessage, liveSnapshot, metric, statusResponse } from '@/test/fixtures';

type Handler = (url: string, init?: RequestInit) => unknown;

function json(body: unknown, status = 200) {
  if (body === undefined) return Promise.resolve(new Response(null, { status: 204 }));
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));
}

function mockApi(handlers: Record<string, Handler>) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();
    const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`;
    const handler = handlers[key] ?? (key === 'GET /api/v1/dashboard' ? () => undefined : undefined);
    return handler ? json(handler(url, init)) : json({ title: 'not mocked: ' + key }, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

async function renderDashboard() {
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/'] }));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <ConfirmProvider>
          <RouterProvider router={router} />
        </ConfirmProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  await screen.findByRole('heading', { name: 'Dashboard', hidden: true });
  return FakeEventSource.instances.at(-1)!;
}

describe('DashboardPage', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows live values from the stream, with reasons for missing ones', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/incidents': () => emptyIncidents });
    const stream = await renderDashboard();
    expect(stream.url).toBe('/api/v1/live/stream');

    act(() => {
      stream.emit('open');
      stream.emit('live', liveMessage());
    });

    const gauges = screen.getByRole('region', { name: /live gauges/i });
    expect(within(gauges).getByText('CPU 38%')).toBeInTheDocument(); // 37.5 % CPU
    expect(within(gauges).getByText('GPU hotspot 84°C')).toBeInTheDocument();
    expect(within(gauges).getByText('CPU temp unavailable: Needs the sensor driver (PawnIO)')).toBeInTheDocument();
    const sensors = screen.getByRole('region', { name: /sensors/i });
    expect(within(sensors).getAllByText('Unavailable')[0]).toHaveAttribute('title', 'Needs the sensor driver (PawnIO)');
    expect(screen.getByRole('img', { name: 'Utilization over the last 5 minutes' })).toBeInTheDocument();
    expect(screen.getByText('game.exe')).toBeInTheDocument();
    expect(screen.getByText(/nvlddmkm stopped responding/)).toBeInTheDocument();
    expect(screen.getByText(/No incidents yet/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /capture incident/i })).toBeEnabled();
  });

  it('fills the chart from the trend, and a gauge focuses its line', async () => {
    const t0 = Date.now() - 60_000;
    const times = Array.from({ length: 60 }, (_, i) => t0 + i * 1000);
    const series = times.map(() => 42);
    const none = times.map(() => null);
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'GET /api/v1/live/trend': () => ({ times, cpu: series, memory: series, gpu: none, vram: none, disk: none, netReceive: none, netSend: none, cpuTemperature: none, gpuTemperature: series, gpuHotspot: none }),
    });
    const stream = await renderDashboard();

    await act(async () => {
      stream.emit('open');
      stream.emit('live', liveMessage());
    });

    const chart = await screen.findByRole('img', { name: 'Utilization over the last 5 minutes' });
    await waitFor(() => expect(JSON.parse(chart.getAttribute('data-option')!).series[0].data.length).toBe(61)); // 60 from the trend + 1 live

    await userEvent.click(screen.getByRole('button', { name: /GPU hotspot/ }));

    const temperatures = screen.getByRole('img', { name: 'Temperatures over the last 5 minutes' });
    const option = JSON.parse(temperatures.getAttribute('data-option')!);
    expect(option.series.find((s: { id: string }) => s.id === 'gpuHotspot').lineStyle.width).toBe(3);
    expect(screen.getByRole('button', { name: /GPU hotspot/ })).toHaveAttribute('aria-pressed', 'true');
  });

  it("keeps a chart's view and time window when the page is reloaded", async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/incidents': () => emptyIncidents });
    const first = await renderDashboard();
    act(() => first.emit('live', liveMessage()));
    const card = (await screen.findByRole('img', { name: 'Utilization over the last 5 minutes' })).closest('div.flex')!.parentElement!;
    await userEvent.click(within(card).getByRole('radio', { name: '10m' }));
    await userEvent.click(within(card).getByRole('radio', { name: 'Temperatures' }));
    expect(await screen.findByRole('img', { name: 'Temperatures over the last 10 minutes' })).toBeInTheDocument();

    cleanup(); // the page goes away, as on a reload
    const second = await renderDashboard();
    act(() => second.emit('live', liveMessage()));

    expect(await screen.findByRole('img', { name: 'Temperatures over the last 10 minutes' })).toBeInTheDocument();
  });

  it('freezes the view while data keeps arriving, and resumes', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/incidents': () => emptyIncidents });
    const stream = await renderDashboard();
    act(() => {
      stream.emit('open');
      stream.emit('live', liveMessage());
    });

    await userEvent.click(screen.getByRole('button', { name: /freeze view/i }));
    act(() => stream.emit('live', liveMessage({ live: liveSnapshot({ cpu: { ...liveSnapshot().cpu!, totalPercent: metric(91) } }) })));

    expect(screen.getByText(/Frozen at/)).toBeInTheDocument();
    expect(screen.getByText('CPU 38%')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /resume live/i }));
    expect(screen.getByText('CPU 91%')).toBeInTheDocument();
  });

  it('loads the saved dashboard with only the data the user included', async () => {
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'GET /api/v1/dashboard': () => ({
        version: 1,
        widgets: [{ id: 'g', type: 'gauges', settings: { metrics: ['cpu', 'gpuTemp'] } }],
        layouts: { lg: [{ i: 'g', x: 0, y: 0, w: 12, h: 5 }] },
      }),
    });
    const stream = await renderDashboard();
    act(() => stream.emit('live', liveMessage()));

    const gauges = await screen.findByRole('region', { name: /live gauges/i });
    expect(within(gauges).getAllByRole('button')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /capture incident/i })).not.toBeInTheDocument();
  });

  it('customizes: add a widget, change what one shows, remove one, then save', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'PUT /api/v1/dashboard': () => undefined,
    });
    const stream = await renderDashboard();
    act(() => stream.emit('live', liveMessage()));

    await userEvent.click(await screen.findByRole('button', { name: /customize/i }));
    expect(screen.getByRole('toolbar', { name: /customize the dashboard/i })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /add widget/i }));
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Add a widget' })).getByRole('button', { name: 'Add Single value' }));
    expect(await screen.findByText('Single value added')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Settings for Gauges' }));
    const settings = screen.getByRole('dialog', { name: 'Gauges settings' });
    await userEvent.click(within(settings).getByRole('button', { name: 'None' }));
    await userEvent.click(within(settings).getByRole('checkbox', { name: 'GPU' }));
    await userEvent.type(within(settings).getByRole('textbox', { name: 'Title' }), 'GPU only');
    await userEvent.click(within(settings).getByRole('button', { name: 'Apply' }));

    await userEvent.click(screen.getByRole('button', { name: 'Remove Displays' }));
    await confirmIt();
    await userEvent.click(screen.getByRole('button', { name: /done/i }));

    await screen.findByText('Dashboard saved');
    const put = fetchMock.mock.calls.find(([url, init]) => String(url) === '/api/v1/dashboard' && init?.method === 'PUT');
    const saved = JSON.parse(String(put?.[1]?.body));
    expect(saved.widgets.some((w: { type: string }) => w.type === 'metric')).toBe(true);
    expect(saved.widgets.some((w: { id: string }) => w.id === 'displays')).toBe(false);
    const gauges = saved.widgets.find((w: { id: string }) => w.id === 'gauges');
    expect(gauges.settings.metrics).toEqual(['gpu']);
    expect(gauges.title).toBe('GPU only');
    expect(within(screen.getByRole('region', { name: /live gauges/i })).getAllByRole('button')).toHaveLength(1);
  });

  it('builds a widget from a design and any value, keeps it in My widgets and saves it', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'PUT /api/v1/dashboard': () => undefined,
      'GET /api/v1/history/catalog': () => [
        { id: 'cpu', label: 'CPU', unit: '%', group: 'System', device: null, longTerm: true },
        { id: 'sensor.4', label: 'NVIDIA GeForce RTX 4090 / GPU Fan 1', unit: 'RPM', group: 'Fan sensors', device: 'NVIDIA GeForce RTX 4090', longTerm: false, key: 'lhm/gpu-nvidia/0/fan/1' },
      ],
      'GET /api/v1/history/series': () => ({ from: 0, to: 0, bucketMs: 1000, series: [{ id: 'sensor.4', points: [[Date.now() - 2000, 1200, 1250, 1300]] }] }),
    });
    const stream = await renderDashboard();
    act(() => stream.emit('live', liveMessage()));

    await userEvent.click(await screen.findByRole('button', { name: /customize/i }));
    await userEvent.click(screen.getByRole('button', { name: /add widget/i }));
    const library = screen.getByRole('dialog', { name: 'Add a widget' });
    expect(within(library).getByRole('list', { name: 'Preconfigured widgets' })).toBeInTheDocument();
    await userEvent.click(within(library).getByRole('tab', { name: /build your own/i }));
    await userEvent.click(within(library).getByRole('button', { name: 'Build a Number widget' }));

    const builder = await screen.findByRole('dialog', { name: 'Build a widget' });
    await userEvent.type(within(builder).getByRole('searchbox', { name: 'Find a value' }), 'fan');
    await userEvent.click(await within(builder).findByRole('checkbox', { name: /GPU Fan 1/ }));
    await userEvent.click(within(builder).getByRole('tab', { name: 'Look' }));
    await userEvent.type(within(builder).getByRole('textbox', { name: 'Title' }), 'Cooling');
    // The preview shows the live CPU value and the recorded fan speed.
    const preview = within(builder).getByRole('region', { name: 'Preview' });
    expect(await within(preview).findByText('38%')).toBeInTheDocument();
    expect(await within(preview).findByText('1,250 RPM')).toBeInTheDocument();
    await userEvent.click(within(builder).getByRole('button', { name: 'Add to the dashboard' }));
    expect(await screen.findByText('Cooling added')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /done/i }));
    await screen.findByText('Dashboard saved');
    const put = fetchMock.mock.calls.find(([url, init]) => String(url) === '/api/v1/dashboard' && init?.method === 'PUT');
    const saved = JSON.parse(String(put?.[1]?.body));
    const custom = saved.widgets.find((w: { type: string }) => w.type === 'custom');
    expect(custom.title).toBe('Cooling');
    expect(custom.settings).toMatchObject({ design: 'value', sources: ['cpu', 'sensor:lhm/gpu-nvidia/0/fan/1'] });
    expect(saved.library).toHaveLength(1);
    expect(saved.library[0].settings.sources).toEqual(custom.settings.sources);
    expect(saved.layouts.lg.find((e: { i: string }) => e.i === custom.id)).toMatchObject({ w: 3, h: 4 });
  });

  it('sets a widget to a quick width while customizing', async () => {
    const fetchMock = mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/incidents': () => emptyIncidents, 'PUT /api/v1/dashboard': () => undefined });
    await renderDashboard();

    await userEvent.click(await screen.findByRole('button', { name: /customize/i }));
    const half = screen.getByRole('radio', { name: 'Recent events: half width' });
    await userEvent.click(half);
    expect(half).toHaveAttribute('aria-checked', 'true');
    await userEvent.click(screen.getByRole('button', { name: /done/i }));

    await screen.findByText('Dashboard saved');
    const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT');
    const saved = JSON.parse(String(put?.[1]?.body));
    expect(saved.layouts.lg.find((e: { i: string }) => e.i === 'events').w).toBe(6);
  });

  it('cancelling customization keeps the dashboard as it was', async () => {
    const fetchMock = mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/incidents': () => emptyIncidents });
    await renderDashboard();

    await userEvent.click(await screen.findByRole('button', { name: /customize/i }));
    await userEvent.click(screen.getByRole('button', { name: 'Remove Recorder controls' }));
    await confirmIt();
    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(screen.getByRole('region', { name: 'Recorder' })).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);
  });

  it('captures an incident with the chosen type and confirms it', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'POST /api/v1/incidents/capture': () => ({
        capture: { incidentKey: 'INC-20261006-00003', merged: false, snapshotOnly: false, recordingUntil: '2026-10-06T12:02:00Z' },
        status: agentStatus({ activeIncident: 'INC-20261006-00003' }),
      }),
    });
    const stream = await renderDashboard();
    act(() => stream.emit('live', liveMessage()));

    await userEvent.click(screen.getByRole('button', { name: 'Screen Flicker' }));

    expect(await screen.findByText('INC-20261006-00003 captured')).toBeInTheDocument();
    const call = fetchMock.mock.calls.find(([url, init]) => String(url) === '/api/v1/incidents/capture' && init?.method === 'POST');
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({ type: 'ScreenFlicker', note: null });
  });

  it('pauses the recorder from the controls', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/incidents': () => emptyIncidents,
      'POST /api/v1/recorder/pause': () => agentStatus({ state: 'Paused' }),
    });
    const stream = await renderDashboard();
    act(() => stream.emit('live', liveMessage()));

    await userEvent.click(within(screen.getByRole('group', { name: 'Pause recording' })).getByRole('button', { name: /15 min/ }));

    expect(await screen.findByText('Recording paused')).toBeInTheDocument();
    const call = fetchMock.mock.calls.find(([url]) => String(url) === '/api/v1/recorder/pause');
    expect(JSON.parse(String(call?.[1]?.body))).toEqual({ minutes: 15 });
  });

  it('explains when the Agent is not running and disables capture', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(false), 'GET /api/v1/incidents': () => emptyIncidents });
    const stream = await renderDashboard();

    act(() => {
      stream.emit('open');
      stream.emit('live', { connected: false, detail: 'The recorder Agent is not running.', status: null, live: null });
    });

    expect(screen.getByRole('button', { name: /capture incident/i })).toBeDisabled();
    expect(screen.getAllByText(/Agent is not running/).length).toBeGreaterThan(0);
  });

  it('tells the user to reopen the dashboard when the session has ended', async () => {
    mockApi({ 'GET /api/v1/incidents': () => emptyIncidents });
    vi.stubGlobal(
      'fetch',
      vi.fn(() => json({ title: 'Not signed in' }, 401)),
    );
    const stream = await renderDashboard();

    await act(async () => {
      stream.readyState = FakeEventSource.CLOSED;
      stream.emit('error');
    });

    expect(await screen.findByText(/session ended/)).toBeInTheDocument();
  });
});
