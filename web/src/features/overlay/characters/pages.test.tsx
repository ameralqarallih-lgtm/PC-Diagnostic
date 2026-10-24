import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { CharacterInfo, OverlayConfig, OverlayResponse, OverlayState } from '@/lib/api/types';
import { mockApi, renderApp } from '@/test/app';
import { statusResponse } from '@/test/fixtures';
import { placesOf } from './CharactersPage';

const state: OverlayState = {
  running: true,
  visible: true,
  problem: null,
  displays: [{ key: 'AUS32F4/1', name: 'PG32UCDP', number: 1, x: 0, y: 0, width: 3840, height: 2160, scale: 1.5, primary: true, refreshHz: 240 }],
  sources: [{ id: 'gpu.g1.temp', label: 'GPU temp', group: 'RTX 4090', unit: '°C', value: 41 }],
};

const library: CharacterInfo[] = [
  { id: 'mochi', name: 'Mochi', shipped: true, actions: ['idle', 'walk', 'run', 'sit', 'sleep'], used: false, here: true, added: null },
  { id: 'astro', name: 'Astro', shipped: true, actions: ['idle', 'walk', 'jump'], used: false, here: true, added: null },
];

describe('Characters page', () => {
  it('lists the library, puts the chosen character on the screen, and saves it with the save bar', async () => {
    let saved: OverlayConfig = { version: 1, enabled: true, hotkey: 'Ctrl+Alt+Shift+O', intervalMs: 1000, hideFromCapture: false, screens: [] };
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay': (): OverlayResponse => ({ config: saved, state, fileProblem: null, recorderProblem: null, presets: [] as never }),
      'GET /api/v1/overlay/state': () => state,
      'GET /api/v1/overlay/characters': () => library,
      'PUT /api/v1/overlay': (_url, init) => {
        saved = JSON.parse(String(init!.body)) as OverlayConfig;
        return { config: saved, state: null, fileProblem: null, recorderProblem: null };
      },
    });
    {
      renderApp('/characters');
      const list = await screen.findByRole('list', { name: 'Library' });
      await userEvent.click(await within(list).findByRole('button', { name: /Astro/ }));
      // Its detail slides in in place of the first one's.
      await waitFor(() => expect(screen.getAllByRole('button', { name: 'Put on 1: PG32UCDP' })).toHaveLength(1));
      await waitFor(() => expect(screen.getByText('Not on a screen yet.').closest('div')?.parentElement?.textContent).toMatch(/Astro/));
      await userEvent.click(screen.getByRole('button', { name: 'Put on 1: PG32UCDP' }));
      expect(await screen.findByText('1 unsaved change')).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: 'Save' }));
      await waitFor(() => expect(saved.screens?.[0]?.characters?.[0]?.character).toBe('astro'));
      expect(placesOf(saved, 'astro')).toEqual(['PG32UCDP']);
      // The sidebar has the page in the On screen area.
      expect(within(screen.getByRole('group', { name: 'On screen' })).getByRole('link', { name: 'Characters' })).toHaveAttribute('aria-current', 'page');
    }
  });

  it('gives the generator a page of its own', async () => {
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/overlay/generator': () => ({ kind: 'off', url: null, hasKey: false, python: null, folder: null, kit: null }),
      'GET /api/v1/overlay/generations': () => [],
    });
    {
      renderApp('/generator');
      expect(await screen.findByRole('heading', { name: 'Character generator', level: 1 })).toBeInTheDocument();
      expect(await screen.findByText(/Set up a generator first/)).toBeInTheDocument();
    }
  });
});
