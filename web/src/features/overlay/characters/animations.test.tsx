import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConfirmProvider } from '@/components/ui/confirm';
import type { AnimationsAnswer, CharacterAnimation } from '@/lib/api/types';
import { bodyOf, mockApi } from '@/test/app';
import { AnimationsSection } from './AnimationsSection';

afterEach(() => vi.unstubAllGlobals());

const wave: CharacterAnimation = {
  id: 'wave',
  name: 'Wave',
  prompt: 'The character waves hello.',
  created: '2026-10-19T00:00:00Z',
  kindOf: 'animation',
  clips: { ren: { state: 'done', file: '0123456789abcdef.char' }, mochi: { state: 'running', progress: 0.5, step: 'Animating' }, hana: { state: 'failed', error: 'no GPU' } },
};

const worried: CharacterAnimation = {
  id: 'worried',
  name: 'Worried',
  prompt: 'The character looks worried.',
  created: '2026-10-19T00:00:00Z',
  kindOf: 'emotion',
  clips: { ren: { state: 'done', file: 'fedcba9876543210.char' }, mochi: { state: 'done', file: '00112233aabbccdd.char', active: false } },
};

function setup(kind: 'animation' | 'emotion') {
  let list: CharacterAnimation[] = [wave, worried];
  const fetchMock = mockApi({
    'GET /api/v1/overlay/animations': (): AnimationsAnswer => ({ animations: list, characters: ['ren', 'mochi', 'hana'] }),
    'GET /api/v1/overlay/characters': () => [
      { id: 'ren', name: 'Ren', shipped: true, actions: ['idle'], used: true, here: true, added: null },
      { id: 'mochi', name: 'Mochi', shipped: true, actions: ['idle'], used: false, here: true, added: null },
      { id: 'hana', name: 'Hana', shipped: true, actions: ['idle'], used: false, here: true, added: null },
    ],
    'POST /api/v1/overlay/animations': () => {
      list = [...list, { ...wave, id: 'spin', name: 'Spin', clips: { ren: { state: 'queued' } } }];
      return list.at(-1);
    },
    'POST /api/v1/overlay/animations/emotions': () => list,
    'POST /api/v1/overlay/animations/wave/retry': () => list,
    'POST /api/v1/overlay/animations/wave/regenerate': () => list,
    'POST /api/v1/overlay/animations/wave/ren/regenerate': () => list,
    'PUT /api/v1/overlay/animations/wave': () => list,
    'PUT /api/v1/overlay/animations/wave/active': () => list,
    'PUT /api/v1/overlay/animations/worried/mochi/active': () => list,
    'DELETE /api/v1/overlay/animations/worried/ren': () => undefined,
    'DELETE /api/v1/overlay/animations/wave': () => undefined,
  });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ConfirmProvider>
        <AnimationsSection kind={kind} />
      </ConfirmProvider>
    </QueryClientProvider>,
  );
  return fetchMock;
}

const called = (fetchMock: ReturnType<typeof mockApi>, method: string, end: string) => fetchMock.mock.calls.some(([u, i]) => String(u).endsWith(end) && (i?.method ?? 'GET') === method);

describe('New animations', () => {
  it('are made from a prompt for every character, each character shown with its preview as it comes along, and managed', async () => {
    const fetchMock = setup('animation');
    const shown = await screen.findByRole('list', { name: 'The animations' });
    // Only the animations here (the emotions have their own tab).
    expect(within(shown).queryByRole('listitem', { name: 'Worried' })).not.toBeInTheDocument();
    expect(within(shown).getByText('1 of 3 made · 1 failed · 1 under way')).toBeInTheDocument();
    const each = within(shown).getByRole('list', { name: 'Wave: each character' });
    expect(await within(each).findByRole('listitem', { name: 'Ren: made' })).toBeInTheDocument();
    expect(within(each).getByRole('listitem', { name: 'Mochi: making' })).toHaveTextContent('50 %');
    expect(within(each).getByRole('listitem', { name: 'Hana: failed' })).toHaveAttribute('title', 'no GPU');

    const user = userEvent.setup();
    await user.type(screen.getByRole('textbox', { name: "The new animation's name" }), 'Spin');
    await user.type(screen.getByRole('textbox', { name: 'Describe the new animation' }), 'The character spins around once.');
    await user.click(screen.getByRole('button', { name: 'Make it for every character' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/overlay/animations')).toEqual({ name: 'Spin', prompt: 'The character spins around once.', kind: 'animation' }));

    // One character's clip made again; the failed ones made; the whole of it off, made again, renamed.
    await user.click(within(each).getByRole('button', { name: 'Make Wave again for Ren' }));
    await vi.waitFor(() => expect(called(fetchMock, 'POST', '/animations/wave/ren/regenerate')).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Make Wave where it failed or is missing' }));
    await vi.waitFor(() => expect(called(fetchMock, 'POST', '/animations/wave/retry')).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Turn Wave off' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/overlay/animations/wave/active')).toEqual({ active: false }));
    await user.click(screen.getByRole('button', { name: 'Make Wave again for every character' }));
    await user.click(await screen.findByRole('button', { name: 'Make again' }));
    await vi.waitFor(() => expect(called(fetchMock, 'POST', '/animations/wave/regenerate')).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Edit Wave' }));
    const name = screen.getByRole('textbox', { name: 'Its name' });
    await user.clear(name);
    await user.type(name, 'Wave hello');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/overlay/animations/wave')).toEqual({ name: 'Wave hello', prompt: 'The character waves hello.' }));
  });
});

describe('New emotions', () => {
  it('have their own tab: the built-in ones made with one click, custom ones from a prompt, each character’s turned on or off or removed', async () => {
    const fetchMock = setup('emotion');
    const shown = await screen.findByRole('list', { name: 'The emotions' });
    expect(within(shown).queryByRole('listitem', { name: 'Wave' })).not.toBeInTheDocument();
    const each = within(shown).getByRole('list', { name: 'Worried: each character' });
    expect(await within(each).findByRole('listitem', { name: 'Hana: not made' })).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Make them' }));
    await vi.waitFor(() => expect(called(fetchMock, 'POST', '/animations/emotions')).toBe(true));
    await user.type(screen.getByRole('textbox', { name: "The new emotion's name" }), 'Angry');
    await user.type(screen.getByRole('textbox', { name: 'Describe the new emotion' }), 'A deep frown.');
    await user.click(screen.getByRole('button', { name: 'Make it for every character' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'POST', '/api/v1/overlay/animations')).toEqual({ name: 'Angry', prompt: 'A deep frown.', kind: 'emotion' }));

    await user.click(within(each).getByRole('button', { name: 'Turn Worried on for Mochi' }));
    await vi.waitFor(() => expect(bodyOf(fetchMock, 'PUT', '/api/v1/overlay/animations/worried/mochi/active')).toEqual({ active: true }));
    await user.click(within(each).getByRole('button', { name: 'Remove Worried for Ren' }));
    await user.click(await screen.findByRole('button', { name: 'Remove' }));
    await vi.waitFor(() => expect(called(fetchMock, 'DELETE', '/animations/worried/ren')).toBe(true));
  });
});

describe('An empty tab', () => {
  it('says where the others are', async () => {
    mockApi({
      'GET /api/v1/overlay/animations': (): AnimationsAnswer => ({ animations: [worried], characters: ['ren'] }),
      'GET /api/v1/overlay/characters': () => [],
    });
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <AnimationsSection kind="animation" />
      </QueryClientProvider>,
    );
    expect(await screen.findByText('No animation yet. 1 emotion is on the Emotions tab.')).toBeInTheDocument();
  });
});
