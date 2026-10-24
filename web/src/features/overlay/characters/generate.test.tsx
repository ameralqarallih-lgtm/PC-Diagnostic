import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { bodyOf, mockApi } from '@/test/app';
import { CharacterGenerate, GeneratorCard } from './CharacterGenerate';
import type { GenerationJob } from './generation';

const card = () => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <GeneratorCard />
  </QueryClientProvider>
);
const ui = () => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <CharacterGenerate onClose={() => {}} />
  </QueryClientProvider>
);
const job = (over: Partial<GenerationJob>): GenerationJob => ({ id: 'j1', type: 'stills', prompt: 'silver hair', kind: 'person', name: null, state: 'done', step: 'Choose a still', progress: 1, error: null, created: '2026-10-15T10:00:00Z', stills: 4, character: null, from: null, ...over });

afterEach(() => vi.unstubAllGlobals());

describe('Generating a character', () => {
  it('guides setting up a generator when there is none, and saves it', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'off', url: null, hasKey: false }),
      'GET /api/v1/overlay/generations': () => [],
      'PUT /api/v1/overlay/generator': () => ({ kind: 'external', url: 'https://gen.example', hasKey: true }),
      'POST /api/v1/overlay/generator/test': () => ({ ok: true, message: 'ready' }),
      'GET /api/v1/overlay/generator/status': () => ({ running: false, busy: false, message: 'Character generation is off.', canStart: false }),
    });
    render(card());
    const setup = await screen.findByRole('region', { name: 'Generator setup' });
    expect(within(setup).getByText(/python serve.py --port 7865/)).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(within(setup).getByText('Another service (external)'));
    await user.type(within(setup).getByRole('textbox', { name: 'Generator address' }), 'https://gen.example');
    await user.type(within(setup).getByLabelText('Generator key'), 'secret');
    await user.click(within(setup).getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/overlay/generator')).toEqual({ kind: 'external', url: 'https://gen.example', key: 'secret', python: null, folder: null, kit: null }));
  });

  it('starts and stops the generator on this PC from its section, and the modal sends to it when none is set up', async () => {
    let running = false;
    const fetchMock = mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'internal', url: 'http://127.0.0.1:7865', hasKey: false }),
      'GET /api/v1/overlay/generations': () => [],
      'GET /api/v1/overlay/generator/status': () => ({ running, busy: false, message: running ? 'PC Diagnostic character generator is running.' : 'Not running.', canStart: true }),
      'POST /api/v1/overlay/generator/start': () => {
        running = true;
        return { ok: true, message: 'ready' };
      },
      'POST /api/v1/overlay/generator/stop': () => {
        running = false;
        return { ok: true, message: 'Stopped.' };
      },
    });
    render(card());
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Start' }));
    expect(await screen.findByText('PC Diagnostic character generator is running.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Stop' }));
    expect(await screen.findByText('Not running.')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/generator/stop') && i?.method === 'POST')).toBe(true);
  });

  it('sends to the generator’s section from the modal when none is set up', async () => {
    mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'off', url: null, hasKey: false }),
      'GET /api/v1/overlay/generations': () => [],
      'GET /api/v1/overlay/generator/status': () => ({ running: false, busy: false, message: 'Character generation is off.', canStart: false }),
    });
    const onClose = vi.fn();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CharacterGenerate onClose={onClose} />
      </QueryClientProvider>,
    );
    await userEvent.setup().click(await screen.findByRole('button', { name: /Set up the character generator/ }));
    expect(onClose).toHaveBeenCalled();
  });

  it('goes through what the generator needs step by step, installing what is missing', async () => {
    let installing: object | null = null;
    const fetchMock = mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'internal', url: 'http://127.0.0.1:7865', hasKey: false, python: 'C:\py.exe', folder: 'C:\gen' }),
      'GET /api/v1/overlay/generations': () => [],
      'GET /api/v1/overlay/generator/status': () => ({ running: false, busy: false, message: 'Not running.', canStart: true }),
      'GET /api/v1/overlay/generator/prerequisites': () => ({
        kit: 'C:\kit',
        running: null,
        steps: [
          { id: 'python', title: 'Python', ok: true, warn: false, detail: 'Python 3.12', actions: ['python'] },
          { id: 'packages', title: 'Python packages', ok: false, warn: false, detail: 'Missing: diffusers', actions: ['packages'] },
          { id: 'models', title: 'Models', ok: false, warn: false, detail: 'Not downloaded: Animagine', actions: ['models', 'kit'] },
        ],
      }),
      'POST /api/v1/overlay/generator/prerequisites/packages': () => (installing = { action: 'packages', state: 'running', log: ['> pip install diffusers'], started: '2026-10-15T10:00:00Z' }),
      'GET /api/v1/overlay/generator/prerequisites/run': () => installing,
    });
    render(card());
    const steps = await screen.findByRole('region', { name: 'Prerequisites' });
    const packages = await within(steps).findByRole('listitem', { name: 'Python packages' });
    expect(packages).toHaveTextContent('Missing: diffusers');
    // What this PC has can be used instead of downloading.
    expect(within(within(steps).getByRole('listitem', { name: 'Models' })).getByRole('button', { name: /Use a kit folder/ })).toBeInTheDocument();
    await userEvent.setup().click(within(packages).getByRole('button', { name: 'Install the packages' }));
    await vi.waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/prerequisites/packages') && i?.method === 'POST')).toBe(true));
    expect(await within(steps).findByText('> pip install diffusers')).toBeInTheDocument();
  });

  it('tests the choice on the page before it is saved, the internal one with its Python and folder', async () => {
    const fetchMock = mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'off', url: null, hasKey: false, python: 'C:\kit\.venv\Scripts\python.exe', folder: 'C:\repo\tools\character-gen' }),
      'GET /api/v1/overlay/generations': () => [],
      'POST /api/v1/overlay/generator/test': () => ({ ok: true, message: 'PC Diagnostic character generator is ready (started by PC Diagnostic).' }),
      'GET /api/v1/overlay/generator/status': () => ({ running: false, busy: false, message: 'Character generation is off.', canStart: false }),
    });
    render(card());
    const setup = await screen.findByRole('region', { name: 'Generator setup' });
    expect(within(setup).getByRole('textbox', { name: 'Generator Python' })).toHaveValue('C:\kit\.venv\Scripts\python.exe');
    await userEvent.setup().click(within(setup).getByRole('button', { name: 'Test' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/overlay/generator/test')).toMatchObject({ kind: 'internal', folder: 'C:\repo\tools\character-gen' }));
    expect(await within(setup).findByText(/started by PC Diagnostic/)).toBeInTheDocument();
  });

  it('draws from a prompt, groups each character with its stages, and opens its animation once a draft is animated', async () => {
    let jobs = [
      job({ id: 'j1', created: '2026-10-15T10:00:00Z' }),
      job({ id: 'j0', type: 'stills', prompt: 'a corgi', created: '2026-10-15T09:00:00Z' }),
      job({ id: 'j2', type: 'character', name: 'Kuma', state: 'running', step: 'Animating', progress: 0.4, from: 'j0', created: '2026-10-15T09:10:00Z', log: ['Animating: walk 49 frames in 60 s'] }),
    ];
    const fetchMock = mockApi({
      'GET /api/v1/overlay/generator': () => ({ kind: 'internal', url: 'http://127.0.0.1:7865', hasKey: false }),
      'GET /api/v1/overlay/generations': () => jobs,
      'POST /api/v1/overlay/generations': (_url, init) => {
        const body = JSON.parse(String(init!.body)) as { type: string; name?: string };
        const made = body.type === 'stills' ? job({ id: 'j3', state: 'queued' }) : job({ id: 'j4', type: 'character', name: body.name ?? null, from: 'j1', state: 'queued', created: '2026-10-15T10:30:00Z' });
        jobs = [...jobs, made];
        return made;
      },
      'GET /api/v1/overlay/generator/status': () => ({ running: true, busy: false, message: 'PC Diagnostic character generator is running.', canStart: true }),
    });
    render(ui());
    const user = userEvent.setup();
    await user.type(await screen.findByRole('textbox', { name: 'Describe the character' }), 'silver hair, ponytail');
    await user.click(screen.getByRole('button', { name: /Draw it/ }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/overlay/generations')).toEqual({ type: 'stills', prompt: 'silver hair, ponytail', kind: 'person' }));

    // Kuma: its drafts and its animation under one group, with how far it is.
    const kuma = await screen.findByRole('listitem', { name: /^Kuma: Animating/ });
    expect(within(kuma).getAllByRole('progressbar')[0]).toHaveAttribute('aria-valuenow', '40');
    await user.click(within(kuma).getAllByRole('button', { expanded: false })[0]!);
    const stages = within(kuma).getByRole('list', { name: 'Its stages' });
    expect(within(stages).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'))).toEqual(['Drafts: 4 drawn', 'Animating Kuma: Animating 40 %']);
    // Its animation opened: what the generator said.
    expect(within(stages).getByLabelText("The generator's output")).toHaveTextContent('walk 49 frames in 60 s');

    // The new one: a draft chosen and animated; the drafts close and the animation opens.
    const fresh = screen.getByRole('listitem', { name: /^New character: Choose a draft/ });
    await user.click(within(fresh).getAllByRole('button', { expanded: false })[0]!);
    await user.click(within(fresh).getByRole('radio', { name: 'Draft 2' }));
    await user.type(within(fresh).getByRole('textbox', { name: 'Its name' }), 'Rin');
    await user.click(within(fresh).getByRole('button', { name: /Animate it/ }));
    await vi.waitFor(() => expect(fetchMock.mock.calls.filter(([u, i]) => String(u).endsWith('/overlay/generations') && i?.method === 'POST')).toHaveLength(2));
    const last = fetchMock.mock.calls.filter(([u, i]) => String(u).endsWith('/overlay/generations') && i?.method === 'POST').at(-1)!;
    expect(JSON.parse(String(last[1]!.body))).toMatchObject({ type: 'character', from: 'j1', still: 1, name: 'Rin', faces: 1 });
    const rin = await screen.findByRole('listitem', { name: /^Rin: Animating: waiting its turn/ });
    expect(within(rin).getByRole('listitem', { name: 'Drafts: 4 drawn' }).querySelector('button')).toHaveAttribute('aria-expanded', 'false');
    expect(within(rin).getByRole('listitem', { name: 'Animating Rin: Waiting its turn' }).querySelector('button')).toHaveAttribute('aria-expanded', 'true');
  });
});
