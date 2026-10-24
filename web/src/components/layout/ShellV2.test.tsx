import { describe, expect, it, vi } from 'vitest';
import { applyDensity, loadDensity } from '@/lib/look';
import { NAV_ITEMS } from './AppShell';
import { findItems, type PaletteItem } from './CommandPalette';
import { AREAS } from './ShellV2';

describe('shell', () => {
  it('puts every page in exactly one of the four areas', () => {
    expect(AREAS.map((a) => a.name)).toEqual(['Monitor', 'On screen', 'Devices', 'Settings']);
    const placed = AREAS.flatMap((a) => a.pages);
    expect([...placed].sort()).toEqual(NAV_ITEMS.map((n) => n.to).sort());
  });

  it('remembers the density and makes everything smaller when compact', () => {
    applyDensity('compact');
    expect(document.documentElement.classList.contains('compact')).toBe(true);
    expect(loadDensity()).toBe('compact');
    applyDensity('comfortable');
    expect(document.documentElement.classList.contains('compact')).toBe(false);
    expect(loadDensity()).toBe('comfortable');
  });
});

describe('command palette', () => {
  const item = (id: string, kind: PaletteItem['kind'], title: string, keywords = ''): PaletteItem => ({ id, kind, title, keywords, run: vi.fn() });
  const items = [item('a', 'Actions', 'Switch between dark and light', 'theme'), item('s', 'Settings', 'Recorder app', 'tray hotkey'), item('p', 'Pages', 'Hardware', 'System')];

  it('finds by every word typed, in title, place or keywords', () => {
    expect(findItems(items, 'hotkey').map((i) => i.id)).toEqual(['s']);
    expect(findItems(items, 'recorder tray').map((i) => i.id)).toEqual(['s']);
    expect(findItems(items, 'system').map((i) => i.id)).toEqual(['p']);
    expect(findItems(items, 'nothing like this')).toEqual([]);
  });

  it('lists pages first, then settings, then actions', () => {
    expect(findItems(items, '').map((i) => i.kind)).toEqual(['Pages', 'Settings', 'Actions']);
  });
});
