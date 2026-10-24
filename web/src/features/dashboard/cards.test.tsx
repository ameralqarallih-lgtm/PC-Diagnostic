import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ToastProvider } from '@/components/ui/toast';
import { RecorderCard } from '@/features/recorder/RecorderCard';
import type { AnomalyInfo } from '@/lib/api/types';
import { mockApi, renderInRouter } from '@/test/app';
import { agentStatus, liveSnapshot } from '@/test/fixtures';
import { AnomaliesCard } from './AnomaliesCard';
import { gaugeSpecs } from './GaugeGrid';
import { GpuCard, TemperaturesCard } from './HealthCards';
import { TopProcessesCard } from './TopProcessesCard';

/** The recorder card asks the API for the catalog and the last custom selection. */
const withQueries = {
  wrapper: ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ToastProvider>{children}</ToastProvider>
    </QueryClientProvider>
  ),
};

afterEach(() => vi.unstubAllGlobals());

describe('RecorderCard', () => {
  it.each([
    ['Idle', /record everything/i, 'start'],
    ['Recording', /stop/i, 'stop'],
    ['Paused', /resume/i, 'resume'],
  ] as const)('offers the right main action when %s', async (state, name, kind) => {
    const onCommand = vi.fn();
    render(<RecorderCard status={agentStatus({ state })} onCommand={onCommand} />, withQueries);

    await userEvent.click(screen.getByRole('button', { name }));

    expect(onCommand).toHaveBeenCalledWith({ kind });
  });

  it('pauses until resumed with a null duration and hides pausing while paused', async () => {
    const onCommand = vi.fn();
    const { rerender } = render(<RecorderCard status={agentStatus()} onCommand={onCommand} />, withQueries);

    await userEvent.click(screen.getByRole('button', { name: 'Until resumed' }));
    expect(onCommand).toHaveBeenCalledWith({ kind: 'pause', minutes: null });

    rerender(<RecorderCard status={agentStatus({ state: 'Paused', pausedUntil: '9999-12-31T23:59:59.9999999+00:00' })} onCommand={onCommand} />);
    expect(screen.queryByRole('group', { name: 'Pause recording' })).not.toBeInTheDocument();
    expect(screen.getByText('Until you resume')).toBeInTheDocument();
  });

  it('starts a custom recording of the picked sources, starting from the last selection', async () => {
    mockApi({
      'GET /api/v1/settings': () => ({ file: 'settings.json', settings: { recorder: { customSelection: { items: ['cpu.usage', 'cpu.thermal'] } } } }),
      'GET /api/v1/recorder/catalog': () => ({
        sources: [
          { id: 'cpu', label: 'CPU', kind: 'cpu', items: [{ id: 'cpu.usage', label: 'Utilization' }, { id: 'cpu.thermal', label: 'Package temperature and power' }] },
          { id: 'gpu:gpu-a', label: 'NVIDIA GeForce RTX 4090', kind: 'gpu', items: [{ id: 'gpu:gpu-a.usage', label: 'Utilization (all engines)' }, { id: 'gpu:gpu-a.thermal', label: 'Temperatures and fan' }] },
          { id: 'sensors:NZXT Kraken X3', label: 'NZXT Kraken X3', kind: 'sensors', items: [{ id: 'sensor:lhm/usbhid/1/temperature/0', label: 'Liquid', detail: 'Temperature, °C' }] },
        ],
      }),
    });
    const onCommand = vi.fn();
    render(<RecorderCard status={agentStatus({ state: 'Idle' })} onCommand={onCommand} />, withQueries);

    await userEvent.click(screen.getByRole('button', { name: /custom recording/i }));
    const dialog = await screen.findByRole('dialog', { name: 'Custom recording' });
    await waitFor(() => expect(within(dialog).getByText('2 of 5 items selected')).toBeInTheDocument());
    expect(within(dialog).getByRole('checkbox', { name: 'CPU' })).toBeChecked();

    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'NVIDIA GeForce RTX 4090' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Show the items of CPU' }));
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Package temperature and power' }));
    expect(within(dialog).getByRole('checkbox', { name: 'CPU' })).toHaveProperty('indeterminate', true);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Start custom recording' }));

    expect(onCommand).toHaveBeenCalledWith({ kind: 'start', selection: ['cpu.usage', 'gpu:gpu-a.usage', 'gpu:gpu-a.thermal'] });
  });

  it('says what a custom recording records', () => {
    render(<RecorderCard status={agentStatus({ state: 'Recording', recordingItems: 14 })} onCommand={() => {}} />, withQueries);

    expect(screen.getByText('A custom selection (14 items)')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /change what is recorded/i })).toBeInTheDocument();
  });

  it('explains why there is no status', () => {
    render(<RecorderCard status={null} unavailable="The recorder Agent is not running." onCommand={() => {}} />, withQueries);

    expect(screen.getByText('The recorder Agent is not running.')).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('TopProcessesCard', () => {
  it('switches between CPU, memory, GPU and disk lists', async () => {
    render(<TopProcessesCard processes={liveSnapshot().processes} />, withQueries);

    expect(screen.getByRole('tab', { name: 'CPU' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('25.3%')).toBeInTheDocument();
    // Every measure shows in every tab, not only the tab's own.
    for (const name of ['PID', 'CPU', 'Memory', 'GPU', 'GPU memory', 'Disk I/O', 'Network']) expect(screen.getByRole('columnheader', { name: new RegExp(`^${name}$`) })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('tab', { name: 'Disk I/O' }));

    expect(screen.getByText('backup.exe')).toBeInTheDocument();
    expect(screen.getByText('Not responding')).toBeInTheDocument();
    expect(screen.getByText('47.7 MB/s')).toBeInTheDocument();
    // Network is always offered; without per-process I/O it says how to get it.
    await userEvent.click(screen.getByRole('tab', { name: 'Network' }));
    expect(screen.getByText(/Per-process network is measured with per-process I\/O/)).toBeInTheDocument();
  });

  it('lists processes by GPU memory', async () => {
    const processes = liveSnapshot().processes!;
    const game = { ...processes.byCpu[0]!, name: 'game.exe', gpuMemoryBytes: 8 * 1024 ** 3 };
    render(<TopProcessesCard processes={{ ...processes, byGpuMemory: [game] }} initialTab="vram" />, withQueries);

    expect(screen.getByRole('tab', { name: 'GPU memory' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('game.exe')).toBeInTheDocument();
    expect(screen.getByText('8.0 GB')).toBeInTheDocument();
  });

  it('adds a network list and the disk share in Enhanced mode', async () => {
    const processes = liveSnapshot().processes!;
    const backup = { ...processes.byIo[0]!, diskBytesPerSec: 4e7, netBytesPerSec: 1e7 };
    render(<TopProcessesCard processes={{ ...processes, byIo: [backup], byNetwork: [backup] }} initialTab="io" />, withQueries);

    expect(screen.getByText('disk 38.1 MB/s')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: 'Network' }));
    expect(screen.getByText('9.5 MB/s')).toBeInTheDocument();
  });
});

describe('Enhanced mode status', () => {
  it('shows what Enhanced mode is doing on the recorder card', () => {
    render(<RecorderCard status={agentStatus({ state: 'Recording', enhanced: 'flight recorder on; per-process I/O: Needs the recorder to run as administrator.' })} onCommand={() => {}} />, withQueries);

    expect(screen.getByText(/per-process I\/O: Needs the recorder to run as administrator/)).toBeInTheDocument();
  });
});

describe('Readings by kind', () => {
  it('draws temperatures as heat bars and GPU usage and VRAM as load and capacity bars', () => {
    const live = liveSnapshot();
    render(
      <>
        <TemperaturesCard live={live} />
        <GpuCard live={live} />
      </>,
    );

    const gpu = live.gpus[0]!;
    expect(screen.getByRole('meter', { name: `${gpu.name} hotspot temperature` })).toHaveAttribute('aria-valuenow', String(gpu.hotspotC.value));
    expect(screen.getByRole('meter', { name: `${gpu.name} utilization` })).toHaveAttribute('aria-valuemax', '100');
    expect(screen.getByRole('meter', { name: `${gpu.name} VRAM in use` })).toBeInTheDocument();
    // No bar for a missing reading: the row says why instead.
    expect(screen.queryByRole('meter', { name: 'CPU package temperature' })).not.toBeInTheDocument();
  });

  it('gives each gauge the shape of its reading', () => {
    const kinds = Object.fromEntries(gaugeSpecs(liveSnapshot()).map((g) => [g.id, g.kind]));
    expect(kinds).toEqual({
      cpu: 'load',
      gpu: 'load',
      disk: 'load',
      memory: 'capacity',
      vram: 'capacity',
      network: 'throughput',
      cpuTemp: 'temperature',
      gpuTemp: 'temperature',
    });
  });
});

describe('AnomaliesCard', () => {
  const running: AnomalyInfo = {
    id: 12,
    ruleId: 'gpu-baseline',
    classification: 'GPU utilization anomaly',
    series: 'gpu.util',
    device: 'NVIDIA GeForce RTX 4090',
    startedAt: Date.parse('2026-10-06T12:30:42Z'),
    endedAt: null,
    observed: 99,
    unit: '%',
    severity: 'Low',
    rule: 'GPU utilization more than 3 σ above its 30 min baseline for at least 15 s',
    baseline: '40–70 % (mean 55, sd 8, 30 min)',
    current: '99 % (z = 5.5)',
    durationSeconds: 18,
    incidentKey: 'INC-20261006-00007',
    summary: '…',
  };

  it('explains each anomaly with its normal range, value, duration and rule, and links its incident', async () => {
    renderInRouter(<AnomaliesCard anomalies={[running, { ...running, id: 13, endedAt: running.startedAt + 18_000, severity: 'Medium', incidentKey: null }]} recording />);

    expect(await screen.findByText('1 going on')).toBeInTheDocument();
    expect(screen.getAllByText('GPU utilization anomaly')).toHaveLength(2);
    expect(screen.getAllByText('Normally 40–70 % (mean 55, sd 8, 30 min)')).toHaveLength(2);
    expect(screen.getByText(/going on for 18 s/)).toBeInTheDocument();
    expect(screen.getByText(/lasted 18 s/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Incident INC-20261006-00007' })).toHaveAttribute('href', '/incidents/INC-20261006-00007');
  });

  it('filters by severity and says when the rules are not running', async () => {
    const { unmount } = renderInRouter(<AnomaliesCard anomalies={[running]} recording minimum="High" />);
    expect(await screen.findByText(/Nothing unusual/)).toBeInTheDocument();
    unmount();

    renderInRouter(<AnomaliesCard anomalies={[]} recording={false} />);
    expect(await screen.findByText(/run while the recorder is recording/)).toBeInTheDocument();
  });
});
