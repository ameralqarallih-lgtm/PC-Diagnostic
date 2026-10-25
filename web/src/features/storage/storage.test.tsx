import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LocalStoreView, StatusResponse, StorageLocationsResponse, StorageLocationView, StorageResponse } from '@/lib/api/types';
import { bodyOf, mockApi, renderApp } from '@/test/app';
import { FakeEventSource, agentStatus, statusResponse } from '@/test/fixtures';

const MB = 1024 ** 2;
const GB = 1024 ** 3;

function storage(overrides: Partial<StorageResponse> = {}): StorageResponse {
  return {
    folder: 'C:\\Users\\me\\AppData\\Local\\PcDiagnostic',
    drive: { name: 'C:', totalBytes: 2000 * GB, freeBytes: 400 * GB },
    totalBytes: 680 * MB,
    limitedBytes: 680 * MB,
    kinds: [
      { id: 'incidents', bytes: 450 * MB, files: 3 },
      { id: 'telemetry', bytes: 220 * MB, files: 3 },
      { id: 'logs', bytes: 7 * MB, files: 9 },
      { id: 'index', bytes: 3 * MB, files: 3 },
    ],
    incidents: [
      { key: 'INC-20261006-00001', title: 'Screen Flicker', centerAt: '2026-10-06T10:00:00Z', state: 'Complete', source: 'Hotkey', userType: 'ScreenFlicker', severity: 'Medium', pinned: true, snapshotOnly: false, bytes: 300 * MB },
      { key: 'INC-20261007-00002', title: 'Game stutter', centerAt: '2026-10-07T10:00:00Z', state: 'Complete', source: 'User', userType: 'Slowdown', severity: 'High', pinned: false, snapshotOnly: false, bytes: 100 * MB },
      { key: 'INC-20261008-00003', title: 'Display driver reset', centerAt: '2026-10-08T10:00:00Z', state: 'Complete', source: 'Event', userType: null, severity: 'High', pinned: false, snapshotOnly: false, bytes: 50 * MB },
    ],
    leftoverBytes: 0,
    telemetry: { fullDetailFrom: new Date(Date.now() - 20 * 3_600_000).toISOString(), summariesFrom: new Date(Date.now() - 12 * 86_400_000).toISOString(), fileBytes: 220 * MB, freeBytes: 40 * MB },
    limits: { dataLimitGb: 10, keepFreeGb: 5, maxIncidents: 0, telemetryRetentionHours: 24, rollupRetentionDays: 30, fullDetailFloorHours: 2, summariesFloorDays: 7, pauseBelowBytes: GB, resumeAboveBytes: 1.5 * GB },
    recorder: { condition: 'Normal', message: null, checkedAt: null, freeBytes: 400 * GB, usedBytes: 680 * MB, lastPassAt: null, recent: [] },
    recent: [{ at: '2026-10-09T09:00:00Z', kind: 'full-detail-shortened', detail: 'Full detail kept for 20 h instead of 24 h to stay within the 10 GB space limit.', freedBytes: 30 * MB, automatic: true, count: 3 }],
    recorderProblem: null,
    ...overrides,
  };
}

const THIS_PC: StorageLocationView = {
  location: { id: 'pc', name: 'This PC', kind: 'pc', keeps: ['pictures', 'backups', 'drivers', 'exports'], pathStyle: true, default: true },
  hasKeys: false,
  accessKeyEnd: null,
  online: true,
  problem: null,
};

const MINIO: StorageLocationView = {
  location: { id: 'minio', name: 'MinIO', kind: 's3', keeps: ['backups'], endpoint: 'http://minio.lan:9000', bucket: 'pcdiag', region: 'us-east-1', prefix: 'home', pathStyle: true },
  hasKeys: true,
  accessKeyEnd: '1234',
  online: false,
  problem: 'The service could not be reached: timed out.',
};

function locations(views: StorageLocationView[] = [THIS_PC, MINIO]): StorageLocationsResponse {
  return {
    locations: views,
    job: { running: false, done: 0, total: 0, copied: 0, problem: null, finished: null },
    kinds: ['pictures', 'backups', 'drivers', 'exports'],
    onThisPc: ['pictures', 'drivers'],
  };
}

describe('Storage page', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('shows what uses the space against the limit, the incidents by size and the recent clean-ups', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/storage': () => storage() });
    renderApp('/storage');

    expect(await screen.findByRole('heading', { name: 'Storage', level: 1 })).toBeInTheDocument();
    const space = screen.getByRole('region', { name: 'Space' });
    expect(within(space).getByText('680.0 MB')).toBeInTheDocument();
    expect(within(space).getByText('of 10 GB allowed')).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'Share of the space limit used' })).toHaveAttribute('aria-valuenow', '7');
    expect(screen.getByText(/Drive C:/)).toBeInTheDocument();

    const kinds = screen.getByRole('region', { name: 'What uses the space' });
    expect(within(kinds).getByText('Incidents')).toBeInTheDocument();
    expect(within(kinds).getByText('Recording (full detail and summaries)')).toBeInTheDocument();

    // Largest first; the pinned one says so.
    const incidents = screen.getByRole('region', { name: 'Incidents by size' });
    const titles = within(incidents).getAllByRole('link').map((l) => l.textContent);
    expect(titles).toEqual(['Screen Flicker', 'Game stutter', 'Display driver reset']);
    expect(within(incidents).getByRole('button', { name: 'Unpin INC-20261006-00001' })).toHaveAttribute('aria-pressed', 'true');

    const recent = screen.getByRole('region', { name: 'Recent clean-ups' });
    expect(within(recent).getByText('Full detail shortened')).toBeInTheDocument();
    expect(within(recent).getByText(/· 3 times/)).toBeInTheDocument();

    const limits = screen.getByRole('region', { name: 'Limits' });
    expect(within(limits).getByText('the oldest unpinned incidents, for the space limit only.')).toBeInTheDocument();
  });

  it('counts the character generator apart from the space limit', async () => {
    const base = storage();
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/storage': () => storage({ totalBytes: 680 * MB + 55 * GB, kinds: [{ id: 'characters', bytes: 55 * GB, files: 40 }, ...base.kinds] }),
    });
    renderApp('/storage');

    const space = await screen.findByRole('region', { name: 'Space' });
    expect(within(space).getByText('680.0 MB')).toBeInTheDocument();
    expect(within(space).getByText(/for the character generator .* which the limit does not count/)).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'Share of the space limit used' })).toHaveAttribute('aria-valuenow', '7');
    expect(within(screen.getByRole('region', { name: 'What uses the space' })).getByText(/Character generator/)).toBeInTheDocument();
  });

  it('deletes the selected incidents after confirming, and cleans up through the recorder', async () => {
    let current = storage();
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/storage': () => current,
      'POST /api/v1/storage/incidents/delete': () => {
        current = storage({ incidents: current.incidents.filter((i) => i.pinned) });
        return { deleted: ['INC-20261007-00002', 'INC-20261008-00003'], problems: [] };
      },
      'POST /api/v1/storage/clean': () => ({ freedBytes: 40 * MB, message: 'Database compacted: 40 MB given back.', state: current.recorder }),
    });
    const user = userEvent.setup();
    renderApp('/storage');

    const incidents = await screen.findByRole('region', { name: 'Incidents by size' });
    await user.click(within(incidents).getByRole('button', { name: 'Select unpinned' }));
    await user.click(within(incidents).getByRole('button', { name: 'Delete 2 (150.0 MB)' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete 2 incidents?' });
    expect(within(dialog).getByText(/150.0 MB go back to the drive/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/storage/incidents/delete')).toEqual({ keys: ['INC-20261007-00002', 'INC-20261008-00003'] }));
    expect(await screen.findByText('2 incidents deleted')).toBeInTheDocument();
    await waitFor(() => expect(within(incidents).queryByText('Game stutter')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Clean up now' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/storage/clean')).toEqual({ compact: true }));
    expect(await screen.findByText('Database compacted: 40 MB given back.')).toBeInTheDocument();
  });

  it('says on every page when the drive is almost full, and on the storage page why', async () => {
    const paused = 'Only 800 MB free on C:: nothing new is recorded until 1.5 GB are free (the last 30 minutes wait in memory).';
    const status: StatusResponse = { ...statusResponse(), agent: { connected: true, detail: null, status: agentStatus({ storage: { condition: 'WritesPaused', message: paused } }) } };
    mockApi({
      'GET /api/v1/status': () => status,
      'GET /api/v1/storage': () => storage({ recorder: { condition: 'WritesPaused', message: paused, checkedAt: null, freeBytes: 800 * MB, usedBytes: 680 * MB, lastPassAt: null, recent: [] } }),
      'GET /api/v1/incidents': () => ({ total: 0, items: [] }),
    });
    renderApp('/incidents');

    const banner = await screen.findByRole('alert');
    expect(banner).toHaveTextContent('Not recording to disk:');
    expect(within(banner).getByRole('link', { name: 'Storage' })).toHaveAttribute('href', '/storage');
  });

  it('lists where files are kept, changes what each keeps, adds a tested folder and removes one', async () => {
    let current = locations();
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/storage': () => storage(),
      'GET /api/v1/overlay/backups': () => [],
      'GET /api/v1/storage/locations': () => current,
      'PATCH /api/v1/storage/locations/minio': (_url, init) => {
        const { keeps } = JSON.parse(String(init?.body)) as { keeps: StorageLocationView['location']['keeps'] };
        current = locations([THIS_PC, { ...MINIO, location: { ...MINIO.location, keeps } }]);
        return current;
      },
      'POST /api/v1/storage/locations/test': () => ({ problem: null }),
      'POST /api/v1/storage/locations': () => {
        current = locations([...current.locations, { ...THIS_PC, location: { id: 'nas', name: 'NAS', kind: 'folder', keeps: ['backups', 'exports'], path: 'D:\\Backups', prefix: 'PcDiagnostic', pathStyle: true } }]);
        return current;
      },
      'DELETE /api/v1/storage/locations/minio': () => {
        current = locations(current.locations.filter((v) => v.location.id !== 'minio'));
        return current;
      },
      'POST /api/v1/storage/locations/minio/default': () => current,
    });
    const user = userEvent.setup();
    renderApp('/storage');

    // What the recorder reads stays on This PC (the default); a service that cannot be reached says so, and its keys only by their end.
    const list = await screen.findByRole('list', { name: 'Storage locations' });
    const pc = within(list).getByRole('listitem', { name: 'This PC' });
    const table = screen.getByRole('table', { name: 'What goes where' });
    expect(within(table).getByRole('checkbox', { name: 'Keep Panel pictures in This PC' })).toBeDisabled();
    expect(within(table).getByRole('checkbox', { name: 'Keep Panel pictures in This PC' })).toBeChecked();
    expect(within(table).getByRole('checkbox', { name: 'Keep Layout backups in MinIO' })).toBeChecked();
    expect(within(table).getByRole('checkbox', { name: 'Keep Kept exports in MinIO' })).not.toBeChecked();
    expect(within(pc).getByText('Default')).toBeInTheDocument();
    expect(within(pc).queryByRole('button', { name: 'Make This PC the default' })).not.toBeInTheDocument();
    expect(within(pc).queryByRole('button', { name: 'Remove This PC' })).not.toBeInTheDocument();
    const minio = within(list).getByRole('listitem', { name: 'MinIO' });
    expect(within(minio).getByText('Not reached')).toBeInTheDocument();
    expect(within(minio).getByText('minio.lan:9000 · pcdiag/home · us-east-1 · key …1234')).toBeInTheDocument();
    expect(within(minio).getByText('Keeps Layout backups')).toBeInTheDocument();

    // The default keeps everything; MinIO starts keeping exports.
    expect(within(table).getByRole('checkbox', { name: 'Keep Kept exports in This PC' })).toBeDisabled();
    await user.click(within(table).getByRole('checkbox', { name: 'Keep Kept exports in MinIO' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'PATCH', '/api/v1/storage/locations/minio')).toEqual({ keeps: ['backups', 'exports'] }));
    await waitFor(() => expect(within(table).getByRole('checkbox', { name: 'Keep Kept exports in MinIO' })).toBeChecked());

    // MinIO made the default (after confirming).
    await user.click(within(minio).getByRole('button', { name: 'Make MinIO the default' }));
    const asked = await screen.findByRole('dialog', { name: 'Keep files in MinIO by default?' });
    expect(within(asked).getByText(/This PC keeps only what the recorder reads/)).toBeInTheDocument();
    await user.click(within(asked).getByRole('button', { name: 'Make it the default' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/storage/locations/minio/default')).toEqual({ move: true }));

    // A folder: tested, then added.
    await user.click(screen.getByRole('button', { name: 'Add a folder' }));
    const form = screen.getByRole('form', { name: 'Add a folder' });
    await user.clear(within(form).getByLabelText('Name'));
    await user.type(within(form).getByLabelText('Name'), 'NAS');
    await user.type(within(form).getByLabelText(/^Folder/), 'D:\\Backups');
    await user.click(within(form).getByRole('button', { name: 'Test' }));
    expect(await within(form).findByText(/It works/)).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Add' }));
    await waitFor(() =>
      expect(bodyOf(fetchMock, 'POST', '/api/v1/storage/locations')).toEqual({ kind: 'folder', name: 'NAS', keeps: ['backups', 'exports'], path: 'D:\\Backups', prefix: 'PcDiagnostic' }),
    );
    expect(await within(list).findByRole('listitem', { name: 'NAS' })).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: 'Add a folder' })).not.toBeInTheDocument();

    // Removing asks first (its files stay there).
    await user.click(within(minio).getByRole('button', { name: 'Remove MinIO' }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove MinIO?' });
    expect(within(dialog).getByText(/Nothing is deleted there/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(within(list).queryByRole('listitem', { name: 'MinIO' })).not.toBeInTheDocument());
  });

  it('sets PC Diagnostic Storage up on this PC, shows it running as the default, and stops it', async () => {
    const view = (over: Partial<LocalStoreView> = {}): LocalStoreView => ({
      name: 'PC Diagnostic Storage',
      version: '4.48',
      program: null,
      downloadBytes: 47_000_874,
      defaultFolder: 'C:\\Users\\me\\AppData\\Local\\PcDiagnostic\\object-store',
      locationId: null,
      folder: null,
      port: null,
      running: false,
      stopped: false,
      problem: null,
      isDefault: false,
      setup: { running: false, step: null, done: 0, problem: null, finished: null },
      ...over,
    });
    let local = view();
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/storage': () => storage(),
      'GET /api/v1/overlay/backups': () => [],
      'GET /api/v1/storage/locations': () => locations([THIS_PC]),
      'GET /api/v1/storage/local': () => local,
      'POST /api/v1/storage/local': () => {
        local = view({ setup: { running: true, step: 'Downloading SeaweedFS (once, about 47 MB)', done: 0.4, problem: null, finished: null } });
        return local;
      },
      'POST /api/v1/storage/local/stop': () => {
        local = { ...local, running: false, stopped: true };
        return local;
      },
    });
    const user = userEvent.setup();
    renderApp('/storage');

    // Not set up: what it is, then its set-up (its folder, the default, the download said).
    const card = await screen.findByRole('region', { name: 'PC Diagnostic Storage' });
    expect(within(card).getByText(/SeaweedFS 4.48 · downloaded once when set up/)).toBeInTheDocument();
    await user.click(within(card).getByRole('button', { name: 'Set it up' }));
    const form = within(card).getByRole('form', { name: 'Set up PC Diagnostic Storage' });
    expect(within(form).getByLabelText('Its files go in')).toHaveValue('C:\\Users\\me\\AppData\\Local\\PcDiagnostic\\object-store');
    expect(within(form).getByText(/downloaded once from its releases on GitHub/)).toBeInTheDocument();
    expect(within(form).getByText(/runs on this PC only \(127\.0\.0\.1\)/)).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Set it up' }));
    await waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/storage/local')).toEqual({ folder: 'C:\\Users\\me\\AppData\\Local\\PcDiagnostic\\object-store', makeDefault: true }));
    expect(await within(card).findByText('Downloading SeaweedFS (once, about 47 MB)…')).toBeInTheDocument();

    // Set up: running on this PC only, the default; stopped when asked.
    local = view({ program: 'downloaded', locationId: 'local', folder: 'D:\\Store', port: 21300, running: true, isDefault: true });
    expect(await within(card).findByText('Running on this PC only (127.0.0.1:21300) · its files are in D:\\Store', {}, { timeout: 7000 })).toBeInTheDocument();
    expect(within(card).getByText('Default')).toBeInTheDocument();
    await user.click(within(card).getByRole('button', { name: 'Stop' }));
    expect(await within(card).findByRole('button', { name: 'Start' })).toBeInTheDocument();
    expect(within(card).getByText('Stopped · its files are in D:\\Store')).toBeInTheDocument();
  }, 15_000);

  it('lists the layout backups newest first and brings one back after confirming', async () => {
    const older = 'overlay-20261008-090000-000.json';
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/storage': () => storage(),
      'GET /api/v1/storage/locations': () => locations([THIS_PC]),
      'GET /api/v1/overlay/backups': () => [
        { kind: 'backups', name: older, size: 2048, updated: '2026-10-08T09:00:00Z' },
        { kind: 'backups', name: 'overlay-20261009-101500-250.json', size: 4096, updated: '2026-10-09T10:15:00Z' },
      ],
      [`POST /api/v1/overlay/backups/${older}/restore`]: () => ({ config: {}, state: null, fileProblem: null, recorderProblem: null }),
    });
    const user = userEvent.setup();
    renderApp('/storage');

    const backups = await screen.findByRole('list', { name: 'Layout backups' });
    const items = within(backups).getAllByRole('listitem');
    expect(items.map((i) => within(i).getByText(/KB$/).textContent)).toEqual(['4.0 KB', '2.0 KB']);
    await user.click(within(items[1]!).getByRole('button', { name: 'Bring back' }));
    const dialog = await screen.findByRole('dialog', { name: 'Bring this layout back?' });
    expect(within(dialog).getByText(/kept as a backup first/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Bring it back' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url) === `/api/v1/overlay/backups/${older}/restore` && init?.method === 'POST')).toBe(true));
    expect(await screen.findByText('Layout brought back')).toBeInTheDocument();
  });

  it('measures without the recorder and leaves the actions to it', async () => {
    mockApi({
      'GET /api/v1/status': () => statusResponse(false),
      'GET /api/v1/storage': () => storage({ recorder: null, recorderProblem: 'The recorder Agent is not running.' }),
    });
    renderApp('/storage');

    expect(await screen.findByText(/The recorder Agent is not running\. The sizes below are measured anyway/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clean up now' })).toBeDisabled();
  });
});
