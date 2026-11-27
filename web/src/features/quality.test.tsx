import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionResponse, GameSession, GameSessionResponse, HopMinute, LatencyResponse, SystemOverview } from '@/lib/api/types';
import { mockApi, renderApp } from '@/test/app';
import { FakeEventSource, statusResponse } from '@/test/fixtures';

const T0 = Date.parse('2026-11-26T20:00:00Z');
const iso = (ms: number) => new Date(ms).toISOString();
const common = { 'GET /api/v1/status': () => statusResponse() };
const system: SystemOverview = {
  information: { 'cpu.name': 'AMD Ryzen 9 7950X' },
  devices: [],
  driversTs: T0,
  drivers: [],
  displayTs: null,
  display: null,
  hardwareTs: null,
  hardware: null,
  availability: [],
  sensors: [],
};

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
});

afterEach(() => vi.unstubAllGlobals());

const session: GameSession = {
  id: 7,
  game: 'ArkAscended',
  start: iso(T0),
  end: iso(T0 + 3_600_000),
  seconds: 3600,
  fpsAvg: 96.4,
  fpsP1: 41,
  low1Fps: 52,
  stutters: 90,
  dropSeconds: 34,
  worstMs: 480,
  displayedFps: 92.1,
  dropped: 3600,
  displayLatencyMs: 21,
  gpuBusyPercent: 96,
  cpuBusyPercent: 40,
  presentMode: 'Composed: Flip',
  windowedPercent: 100,
  recognized: 'library',
};

describe('Gaming page', () => {
  it('lists the sessions and explains a drop of the one picked', async () => {
    const detail: GameSessionResponse = {
      session,
      seconds: [
        { at: iso(T0), fps: 120, worstMs: 9, stutters: 0, usualFps: 120 },
        { at: iso(T0 + 1000), fps: 48, worstMs: 60, stutters: 2, usualFps: 120 },
      ],
      minutes: [],
      drops: [
        {
          from: iso(T0 + 1000),
          to: iso(T0 + 1000),
          lowestFps: 48,
          usualFps: 120,
          worstMs: 60,
          stutters: 2,
          findings: [{ kind: 'gpu', severity: 2, text: 'The GPU was 99 % busy during the drop: the graphics card may be the limit in this scene.' }],
        },
      ],
      patterns: [{ kind: 'gpu', drops: 1 }],
      incidents: [{ key: 'INC-20261126-00003', title: 'Game frame rate drop', at: iso(T0 + 2000) }],
      findings: [{ kind: 'display', severity: 1, text: "Its frames went through Windows' desktop compositor (Composed: Flip) most of the time, as a game in a window does." }],
    };
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/gaming/sessions': () => ({ sessions: [session], now: iso(T0 + 3_650_000) }),
      'GET /api/v1/gaming/sessions/7': () => detail,
      'GET /api/v1/settings': () => ({ file: 'settings.json', settings: { recorder: { games: ['MyIndieGame'], notGames: [] } } }),
      'POST /api/v1/gaming/classify': () => ({ games: ['MyIndieGame'], notGames: ['ArkAscended'] }),
    });
    const router = renderApp('/gaming');

    const row = await screen.findByRole('button', { name: /ArkAscended/ });
    expect(row).toHaveTextContent('96 fps');
    expect(row).toHaveTextContent('34 s of drops');
    expect(row).toHaveTextContent('1.5 stutters/min');
    expect(row).toHaveTextContent('92 shown');
    expect(row).toHaveTextContent('windowed');
    await userEvent.click(row);

    await waitFor(() => expect(router.state.location.search).toEqual({ session: 7 }));
    expect(await screen.findByText(/The GPU was 99 % busy during the drop/)).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Of its 1 drop, 1 coincided with the GPU at its limit.');
    expect(screen.getByRole('link', { name: /Game frame rate drop/ })).toHaveAttribute('href', '/incidents/INC-20261126-00003');
    expect(screen.getByText(/went through Windows' desktop compositor/)).toBeInTheDocument();
    expect(screen.getByText(/Counted because it is installed in a game library; played in a window; frames shown as "Composed: Flip"/)).toBeInTheDocument();
    expect(screen.getByText('On the screen').parentElement).toHaveTextContent('92 fps1.0 a second never shown');
    expect(await screen.findByText(/MyIndieGame counts/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Not a game' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url) === '/api/v1/gaming/classify' && init?.method === 'POST')).toBe(true));
    const call = fetchMock.mock.calls.find(([url]) => String(url) === '/api/v1/gaming/classify')!;
    expect(JSON.parse(String(call[1]?.body))).toEqual({ game: 'ArkAscended', isGame: false });
  });

  it('says how frame rates are counted when there is no session, and when the recorder measures nothing by itself', async () => {
    const status = statusResponse();
    status.agent.status = { ...status.agent.status!, state: 'Idle', watching: false };
    mockApi({ 'GET /api/v1/status': () => status, 'GET /api/v1/gaming/sessions': () => ({ sessions: [], now: iso(T0) }) });
    renderApp('/gaming');

    expect(await screen.findByText(/No game sessions in the last 30 days/)).toBeInTheDocument();
    expect(await screen.findByText(/neither recording nor watching for problems, so games are measured only while this dashboard is open/)).toBeInTheDocument();
  });
});

describe('Network page', () => {
  const hop = (ms: number, loss: number): HopMinute => ({ ms, max: ms * 2, loss });

  it('shows where the losses are, hop by hop, and the programs using the network', async () => {
    const response: ConnectionResponse = {
      from: T0 - 86_400_000,
      to: T0,
      report: {
        minutes: 2,
        router: { ms: 1.2, p95Ms: 3, loss: 0, sent: 2 },
        provider: { ms: 9, p95Ms: 14, loss: 4.5, sent: 2 },
        internet: { ms: 21, p95Ms: 40, loss: 5, sent: 2 },
        jitterMs: 2.5,
        dnsMs: 18,
        dnsChecks: 12,
        dnsFailed: 1,
        wifiMin: null,
        outages: [],
        findings: [{ kind: 'provider', severity: 3, text: "4.5 % of the pings to the provider's first hop were lost while the router answered 100 % of them." }],
      },
      minutes: [
        { at: iso(T0 - 120_000), router: hop(1, 0), provider: hop(9, 9), internet: hop(20, 10), jitterMs: 2, dnsMs: 18, dnsFailed: 0, wifiMin: null, netBps: 1000 },
        { at: iso(T0 - 60_000), router: hop(1.4, 0), provider: hop(9, 0), internet: hop(22, 0), jitterMs: 3, dnsMs: 18, dnsFailed: 1, wifiMin: null, netBps: 1000 },
      ],
      programs: [{ name: 'steam', receiveBps: 5_242_880, sendBps: 10_240, peakBps: 9_000_000, retransmitsPerSec: 2.5 }],
    };
    const fetchMock = mockApi({ ...common, 'GET /api/v1/connection': () => response });
    renderApp('/network');

    expect(await screen.findByText(/were lost while the router answered 100 % of them/)).toBeInTheDocument();
    const provider = screen.getByText('Provider').closest('div') as HTMLElement;
    expect(provider).toHaveTextContent('9 ms average');
    expect(within(provider).getByText('4.5 % lost')).toHaveClass('text-danger');
    expect(screen.getByRole('row', { name: /steam/ })).toHaveTextContent('2.5/s');

    await userEvent.click(screen.getByRole('button', { name: 'Last hour' }));
    await waitFor(() => expect(fetchMock.mock.calls.filter(([url]) => String(url).startsWith('/api/v1/connection')).length).toBe(2));
  });
});

describe('System › Latency', () => {
  it('lists the measurements with the slow driver named and measures on request', async () => {
    const latency: LatencyResponse = {
      from: T0 - 86_400_000,
      to: T0,
      minutes: [{ at: iso(T0 - 60_000), dpcCoreMax: 14, isrCoreMax: 2, dpcAvg: 0.6 }],
      measurements: [
        {
          at: iso(T0),
          from: iso(T0 - 10_000),
          seconds: 10,
          reason: 'stutter',
          problem: null,
          drivers: [
            {
              latency: { driver: 'nvlddmkm.sys', dpcs: 4000, dpcMaxUs: 1400, dpcTotalUs: 90_000, isrs: 0, isrMaxUs: 0, isrTotalUs: 0, over500Us: 12, over1Ms: 3 },
              what: 'the NVIDIA graphics driver',
              try: 'a clean install of the newest (or the previous) NVIDIA driver',
            },
            { latency: { driver: 'ndis.sys', dpcs: 300, dpcMaxUs: 90, dpcTotalUs: 4000, isrs: 0, isrMaxUs: 0, isrTotalUs: 0, over500Us: 0, over1Ms: 0 }, what: null, try: null },
          ],
        },
      ],
      findings: [{ kind: 'driver', severity: 3, text: 'The measurement at 20:59:50 found nvlddmkm.sys (the NVIDIA graphics driver) running up to 1400 µs at a time.' }],
      status: { measuring: false, problem: null, last: null },
      problem: null,
    };
    const fetchMock = mockApi({ ...common, 'GET /api/v1/system': () => system, 'GET /api/v1/latency': () => latency, 'POST /api/v1/latency/measure': () => ({ status: { measuring: true, problem: null, last: null }, problem: null }) });
    renderApp('/system?tab=latency');

    expect(await screen.findByText(/running up to 1400 µs at a time/)).toBeInTheDocument();
    expect(screen.getByText(/is the NVIDIA graphics driver; what could help: a clean install/)).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /nvlddmkm\.sys/ })).toHaveTextContent('1.4 ms');

    await userEvent.click(screen.getByRole('button', { name: 'Measure now (10 s)' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url) === '/api/v1/latency/measure' && init?.method === 'POST')).toBe(true));
  });
});
