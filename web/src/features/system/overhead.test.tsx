import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderInRouter, mockApi } from '@/test/app';
import { RecorderOverheadCard } from './RecorderOverheadCard';

describe('Recorder cost', () => {
  it('shows where the CPU went last minute, by part, with readable names', async () => {
    mockApi({
      'GET /api/v1/recorder/overhead': () => ({ samples: 0, cpuAvgPercent: null }),
      'GET /api/v1/recorder/cost': () => ({
        cost: {
          at: '2026-10-15T14:20:00Z',
          seconds: 60,
          usedMs: 7700,
          sharePercent: 0.53,
          parts: [
            { part: 'usb.refresh', ms: 1077 },
            { part: 'overlay.motion', ms: 648 },
            { part: 'usb.motion', ms: 643 },
            { part: 'sensors.lhm', ms: 840 },
            { part: 'collect.inventory', ms: 0 },
            { part: 'other', ms: 2231 },
          ],
        },
        problem: null,
      }),
    });
    renderInRouter(<RecorderOverheadCard />);
    const split = await screen.findByRole('region', { name: 'Where the CPU went' });
    expect(within(split).getByText('0.53 % of the PC · 7.7 s CPU in 60 s')).toBeInTheDocument();
    const names = within(split).getAllByRole('listitem').map((li) => li.firstElementChild?.textContent);
    // The USB screen's motion is counted once (its own line, not again as the moving backgrounds); nothing under 1 ms.
    expect(names).toEqual(['USB screens: panels', 'USB screens: moving backgrounds', 'Sensors (temperatures, fans, voltages)', 'Everything else (storage, links, .NET)']);
  });
});
