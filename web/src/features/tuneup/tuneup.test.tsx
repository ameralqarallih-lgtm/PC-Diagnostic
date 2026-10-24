import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ControlProcess, ControlService, TuneUpReport } from '@/lib/api/types';
import { bodyOf, mockApi, renderApp } from '@/test/app';
import { FakeEventSource, liveSnapshot, metric, statusResponse } from '@/test/fixtures';
import { recommend } from './recommend';
import { setSafeMode } from './safeMode';

const GB = 1024 ** 3;

function process(overrides: Partial<ControlProcess> = {}): ControlProcess {
  return {
    pid: 1000,
    startTicks: 638_000_000_000_000_000,
    parentPid: 4,
    name: 'app.exe',
    path: 'C:\\Program Files\\App\\app.exe',
    publisher: 'App Ltd',
    kind: 'app',
    protection: 'None',
    protectionReason: null,
    cpuPercent: 1,
    workingSetBytes: 200 * 1024 ** 2,
    privateBytes: 150 * 1024 ** 2,
    threads: 10,
    handles: 100,
    priority: 'Normal',
    notResponding: false,
    sessionId: 1,
    services: [],
    startedAt: '2026-10-08T10:00:00Z',
    ...overrides,
  };
}

function report(overrides: Partial<TuneUpReport['system']> = {}, extra: Partial<TuneUpReport> = {}): TuneUpReport {
  return {
    at: '2026-10-08T12:00:00Z',
    cleanup: [
      { id: 'user-temp', name: 'Your temporary files', description: 'Files programs left.', bytes: 1.5 * GB, files: 1200, recommended: true, caution: null },
      { id: 'shader-cache', name: 'Graphics shader caches', description: 'Compiled shaders.', bytes: 0.8 * GB, files: 300, recommended: false, caution: 'Games compile their shaders again.' },
    ],
    system: {
      powerPlans: [
        { id: 'a1', name: 'Balanced', active: false },
        { id: 'b2', name: 'Power saver', active: true },
      ],
      hasBattery: false,
      hibernationEnabled: true,
      hibernationFileBytes: 26 * GB,
      gameMode: false,
      gpuScheduling: true,
      standbyBytes: 10 * GB,
      freeBytes: 4 * GB,
      wslRunning: true,
      wslBytes: 12 * GB,
      volumes: [{ name: 'C:', label: null, totalBytes: 1000 * GB, freeBytes: 50 * GB, system: true }],
      ...overrides,
    },
    startup: [],
    ...extra,
  };
}

describe('tune-up advice', () => {
  it('says what would help most, with why, and how', () => {
    const live = liveSnapshot({ memory: { ...liveSnapshot().memory!, usedPercent: metric(94) } });
    const processes = [process({ name: 'chrome.exe', workingSetBytes: 6 * GB }), process({ pid: 2, name: 'hung.exe', notResponding: true }), process({ pid: 3, name: 'indexer.exe', kind: 'background', cpuPercent: 25 })];
    const advice = recommend(report(), processes, [], live);
    const ids = advice.map((a) => a.id);

    expect(ids).toEqual(expect.arrayContaining(['memory', 'wsl', 'hung', 'busy-background', 'volume-C:', 'cleanup', 'hibernation', 'power-plan', 'game-mode']));
    expect(advice[0]!.level).toBe('high');
    expect(advice.find((a) => a.id === 'memory')!.detail).toContain('chrome.exe (6.0 GB)');
    expect(advice.find((a) => a.id === 'hung')!.action).toMatchObject({ kind: 'apply', action: 'end-processes', items: ['2:638000000000000000'] });
    expect(advice.find((a) => a.id === 'power-plan')!.action).toMatchObject({ kind: 'apply', action: 'power-plan', value: 'a1' });
    // Only the recommended clean-up counts: 1.5 GB.
    expect(advice.find((a) => a.id === 'cleanup')!.title).toContain('1.5 GB');
  });

  it('has nothing to say about a PC in good shape', () => {
    const fine = report({ powerPlans: [{ id: 'a1', name: 'Balanced', active: true }], hibernationEnabled: false, gameMode: true, wslRunning: false, wslBytes: null, volumes: [] }, { cleanup: [] });
    expect(recommend(fine, [process()], [], liveSnapshot())).toEqual([]);
  });
});

describe('Tune-up page', () => {
  const services: ControlService[] = [
    { name: 'AppUpdater', displayName: 'App Updater', description: 'Keeps App up to date.', status: 'Running', startType: 'Automatic', processId: 3000, path: 'C:\\Program Files\\App\\updater.exe', publisher: 'App Ltd', windows: false, canStop: true, protection: 'None', protectionReason: null },
    { name: 'RpcSs', displayName: 'Remote Procedure Call (RPC)', description: null, status: 'Running', startType: 'Automatic', processId: 900, path: 'C:\\Windows\\system32\\svchost.exe', publisher: 'Microsoft Corporation', windows: true, canStop: false, protection: 'Protected', protectionReason: "Windows' core communication; nothing works without it." },
  ];
  const processes = [
    process({ pid: 4242, name: 'game.exe', cpuPercent: 30 }),
    process({ pid: 600, name: 'explorer.exe', kind: 'app', path: 'C:\\Windows\\explorer.exe', publisher: 'Microsoft Windows', protection: 'Caution', protectionReason: 'The taskbar, desktop and File Explorer windows close and come back.' }),
    process({ pid: 700, name: 'lsass.exe', kind: 'windows', protection: 'Protected', protectionReason: 'Handles security and passwords.', sessionId: 0 }),
  ];
  const common = {
    'GET /api/v1/status': () => statusResponse(),
    'GET /api/v1/live': () => ({ connected: true, detail: null, status: null, live: liveSnapshot() }),
    'GET /api/v1/control/processes': () => processes,
    'GET /api/v1/control/services': () => services,
    'GET /api/v1/tuneup': () => report(),
  };

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
    localStorage.clear();
    setSafeMode(true);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('ends a process after confirming, and asks for more before ending a part of Windows', async () => {
    const fetchMock = mockApi({ ...common, 'POST /api/v1/control/processes/4242/end': () => ({ ok: true, message: 'Ended game.exe.', changed: 1, restartNeeded: false }) });
    renderApp('/tune-up?tab=processes');

    await userEvent.click(await screen.findByRole('button', { name: 'Actions for game.exe (4242)' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'End process' }));
    const dialog = await screen.findByRole('dialog', { name: 'End game.exe?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'End process' }));
    expect(await screen.findByText('Ended game.exe.')).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/control/processes/4242/end')).toEqual({ start: 638_000_000_000_000_000, tree: false, confirmed: false, safe: true });

    // A part of Windows needs ticking "I understand" first.
    await userEvent.click(screen.getByRole('button', { name: 'Actions for explorer.exe (600)' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'End process' }));
    const careful = await screen.findByRole('dialog', { name: 'End explorer.exe?' });
    expect(within(careful).getByRole('button', { name: 'End process' })).toBeDisabled();

    // What Windows needs cannot be chosen at all.
    await userEvent.keyboard('{Escape}');
    expect(screen.getByRole('checkbox', { name: 'Select lsass.exe (700)' })).toBeDisabled();
  });

  it('cleans the selected targets, the safe ones selected at first', async () => {
    const fetchMock = mockApi({ ...common, 'POST /api/v1/tuneup/clean': () => ({ ok: true, message: '1200 files removed.', freedBytes: 1.5 * GB, changed: 1200, restartNeeded: false }) });
    renderApp('/tune-up?tab=cleanup');

    const list = await screen.findByRole('list', { name: 'Clean-up targets' });
    expect(within(list).getByRole('checkbox', { name: /Your temporary files/ })).toBeChecked();
    expect(within(list).getByRole('checkbox', { name: /Graphics shader caches/ })).not.toBeChecked();
    expect(within(list).getByText('Games compile their shaders again.')).toBeInTheDocument();

    // Like every list, it groups and sorts: by whether it is selected first, the smallest first.
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Add a grouping' }), 'choice');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Add a sort key' }), 'size');
    await userEvent.click(screen.getByRole('button', { name: 'Size, descending' }));
    expect(within(list).getByText('Safe to clean')).toBeInTheDocument();
    expect(within(list).getByText('Your choice')).toBeInTheDocument();
    expect(within(list).getAllByRole('checkbox').map((c) => (c as HTMLInputElement).checked)).toEqual([true, false]);

    await userEvent.click(screen.getByRole('button', { name: 'Clean up 1.5 GB' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Remove 1.5 GB?' })).getByRole('button', { name: 'Clean up' }));
    expect(await screen.findByText('1200 files removed.')).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/tuneup/clean')).toEqual({ items: ['user-temp'], safe: true });
  });

  it('shows what a clean-up target holds as a tree, opened folder by folder', async () => {
    const entry = (over: Record<string, unknown>) => ({ root: 0, path: '', name: 'Temp', folder: true, bytes: 3 * GB, files: 40, removableBytes: 1.5 * GB, removableFiles: 30, modified: '2026-10-01T10:00:00Z', partial: false, ...over });
    mockApi({
      ...common,
      'GET /api/v1/tuneup/cleanup/user-temp/files': (url) => {
        const query = new URLSearchParams(String(url).split('?')[1]);
        if (query.get('root') === '-1') return { target: 'user-temp', location: null, entries: [entry({})], more: 0, note: null };
        return {
          target: 'user-temp',
          location: 'C:\\Users\\me\\AppData\\Local\\Temp',
          entries: [entry({ path: 'old-setup', name: 'old-setup', bytes: 2 * GB, files: 12 }), entry({ path: 'log.txt', name: 'log.txt', folder: false, bytes: 1024, files: 1, removableBytes: 0, removableFiles: 0 })],
          more: 0,
          note: null,
        };
      },
    });
    renderApp('/tune-up?tab=cleanup');

    const list = await screen.findByRole('list', { name: 'Clean-up targets' });
    await userEvent.click(within(list).getByRole('button', { name: 'Show the files of Your temporary files' }));
    const tree = await screen.findByRole('tree', { name: 'Files' });
    await userEvent.click(await within(tree).findByRole('button', { name: /^Temp/ }));
    expect(await within(tree).findByRole('treeitem', { name: 'old-setup' })).toBeInTheDocument();
    const file = within(tree).getByRole('treeitem', { name: 'log.txt' });
    expect(within(file).getByText('1.0 KB')).toBeInTheDocument();
    expect(within(file).getByText('—')).toBeInTheDocument(); // too new to be removed
  });

  it('optimizes in safe mode, leaving what other programs use, and can be switched off', async () => {
    const candidate = (over: Record<string, unknown>) => ({
      pid: 5000, startTicks: 1, name: 'idle-launcher.exe', kind: 'background', cpuPercent: 0, workingSetBytes: 300 * 2 ** 20, gpuPercent: 0, gpuMemoryBytes: 900 * 2 ** 20,
      netBytesPerSec: 0, diskBytesPerSec: 0, inUse: null, protected: null, ...over,
    });
    const plan = {
      at: '2026-10-15T19:00:00Z',
      gpuMemory: [candidate({}), candidate({ pid: 6000, name: 'game.exe', kind: 'app', gpuPercent: 80, gpuMemoryBytes: 8 * GB, inUse: 'it is the program in front' })],
      memory: [],
      cpu: [],
      drives: [{ mount: 'C:', model: 'Samsung SSD', ssd: true, activePercent: 3, inUse: null }],
      gpus: [{ instanceId: 'PCI\\VEN_10DE', name: 'NVIDIA GeForce RTX 4090' }],
      standbyBytes: 4 * GB,
      standbyInUse: 'game.exe is drawing (80 % GPU): it would read its files again and stutter',
      gpuRestartInUse: 'game.exe is using the GPU (80 %); restarting the driver could crash it',
      wslBytes: null,
      wslInUse: null,
      foregroundName: 'game.exe',
    };
    const ok = (message: string) => () => ({ ok: true, message, changed: 1, restartNeeded: false });
    const fetchMock = mockApi({
      ...common,
      'GET /api/v1/tuneup/plan': () => plan,
      'POST /api/v1/tuneup/free-vram': ok('Ended 1 program(s) holding GPU memory.'),
      'POST /api/v1/tuneup/lower-priority': ok('Lowered.'),
      'POST /api/v1/tuneup/trim-memory': () => ({ ok: true, message: 'Trimmed 3 program(s).', changed: 3, restartNeeded: false, skipped: ['game.exe (6000): it is the program in front'] }),
      'POST /api/v1/tuneup/free-standby': () => ({ ok: false, message: 'Safe mode left The standby memory.', changed: 0, restartNeeded: false, skipped: ['The standby memory: game.exe is drawing'] }),
      'POST /api/v1/tuneup/retrim-drives': ok('Retrimmed 1 solid-state drive(s).'),
      'POST /api/v1/tuneup/flush-dns': ok('The DNS cache is cleared.'),
    });
    renderApp('/tune-up?tab=optimize');

    // Safe mode: the game in front cannot be chosen, the driver is not restarted under it.
    const holders = await screen.findByRole('list', { name: 'Programs holding GPU memory' });
    expect(within(holders).getByRole('checkbox', { name: 'Choose game.exe (6000)' })).toBeDisabled();
    expect(within(holders).getByText('In use: it is the program in front')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Restart NVIDIA GeForce RTX 4090 driver' })).toBeDisabled();

    await userEvent.click(within(holders).getByRole('checkbox', { name: 'Choose idle-launcher.exe (5000)' }));
    await userEvent.click(screen.getByRole('button', { name: /^Free .+ of GPU memory$/ }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'End them' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/tuneup/free-vram')).toEqual({ items: ['5000:1'], confirmed: false, safe: true }));

    // Optimize now: every step, each saying what safe mode left.
    await userEvent.click(screen.getByRole('button', { name: 'Optimize now' }));
    const done = await screen.findByRole('list', { name: 'What was done' });
    await waitFor(() => expect(within(done).getAllByRole('listitem').length).toBeGreaterThanOrEqual(5));
    expect(within(done).getByText(/Safe mode left game\.exe \(6000\): it is the program in front/)).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/tuneup/retrim-drives')).toEqual({ items: [], safe: true, confirmed: true });

    // Switched off: the game can be chosen too.
    await userEvent.click(screen.getByRole('switch', { name: 'Safe mode' }));
    expect(screen.getByText('Safe mode off')).toBeInTheDocument();
    expect(within(holders).getByRole('checkbox', { name: 'Choose game.exe (6000)' })).toBeEnabled();
  });

  it('stops a service from another program; Windows needs its own', async () => {
    const fetchMock = mockApi({ ...common, 'POST /api/v1/control/services/AppUpdater/stop': () => ({ ok: true, message: 'App Updater stopped.', changed: 1, restartNeeded: false }) });
    renderApp('/tune-up?tab=services');

    await userEvent.click(await screen.findByRole('button', { name: 'Actions for App Updater' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Stop' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Stop App Updater?' })).getByRole('button', { name: 'Stop service' }));
    expect(await screen.findByText('App Updater stopped.')).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/control/services/AppUpdater/stop')).toMatchObject({ confirmed: false });

    await userEvent.click(screen.getByRole('button', { name: 'Actions for Remote Procedure Call (RPC)' }));
    expect(await screen.findByRole('menuitem', { name: 'Stop' })).toBeDisabled();
  });

  it('shows advice first and acts on it after confirming', async () => {
    const fetchMock = mockApi({ ...common, 'POST /api/v1/tuneup/game-mode': () => ({ ok: true, message: 'Game Mode is on.', changed: 1, restartNeeded: false }) });
    renderApp('/tune-up');

    const advice = await screen.findByRole('list', { name: 'Advice' });
    await waitFor(() => expect(within(advice).getByText('Game Mode is off')).toBeInTheDocument());
    await userEvent.click(within(advice).getByRole('button', { name: 'Turn on Game Mode' }));
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Turn on Game Mode?' })).getByRole('button', { name: 'Turn on Game Mode' }));
    expect(await screen.findByText('Game Mode is on.')).toBeInTheDocument();
    expect(bodyOf(fetchMock, 'POST', '/api/v1/tuneup/game-mode')).toMatchObject({ value: 'on' });
  });
});
