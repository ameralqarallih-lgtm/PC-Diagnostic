import { describe, expect, it } from 'vitest';
import type { OverlayConfig } from '@/lib/api/types';
import { emptyConfig, newPanel } from './model';
import { changedCount } from './OverlayPage';
import { emptyUndo, recordChange, redoStep, undoKey, undoStep } from './undo';

describe('undo', () => {
  it('undoes and redoes changes in order', () => {
    let stack = emptyUndo<number>();
    stack = recordChange(stack, 1, 0);
    stack = recordChange(stack, 2, 1000);
    const back = undoStep(stack, 3)!;
    expect(back.value).toBe(2);
    const again = undoStep(back.stack, back.value)!;
    expect(again.value).toBe(1);
    expect(undoStep(again.stack, again.value)).toBeNull();
    const forward = redoStep(again.stack, again.value)!;
    expect(forward.value).toBe(2);
    expect(redoStep(forward.stack, forward.value)!.value).toBe(3);
  });

  it('joins quick changes (a drag) into one step, and a new change drops the redo', () => {
    let stack = emptyUndo<number>();
    stack = recordChange(stack, 0, 0);
    for (let t = 50; t <= 500; t += 50) stack = recordChange(stack, t, t);
    expect(stack.past).toEqual([0]);
    const back = undoStep(stack, 99)!;
    expect(back.value).toBe(0);
    expect(recordChange(back.stack, 0, 5000).future).toEqual([]);
  });

  it('keeps text fields their own undo', () => {
    const input = document.createElement('input');
    const div = document.createElement('div');
    expect(undoKey({ ctrlKey: true, metaKey: false, shiftKey: false, key: 'z', target: input })).toBeNull();
    expect(undoKey({ ctrlKey: true, metaKey: false, shiftKey: false, key: 'z', target: div })).toBe('undo');
    expect(undoKey({ ctrlKey: true, metaKey: false, shiftKey: true, key: 'Z', target: div })).toBe('redo');
    expect(undoKey({ ctrlKey: true, metaKey: false, shiftKey: false, key: 'y', target: div })).toBe('redo');
    expect(undoKey({ ctrlKey: false, metaKey: false, shiftKey: false, key: 'z', target: div })).toBeNull();
  });
});

describe('unsaved changes count', () => {
  const withPanels = (titles: string[]): OverlayConfig => ({
    ...emptyConfig(),
    screens: [{ key: 'A', name: 'A', panels: titles.map((t, i) => ({ ...newPanel('empty', [], []), id: `p${i}`, title: t })) }],
  });

  it('counts each panel changed, added or removed, and the settings', () => {
    const saved = withPanels(['CPU', 'GPU']);
    expect(changedCount(saved, saved)).toBe(0);
    expect(changedCount(withPanels(['CPU', 'Graphics']), saved)).toBe(1);
    expect(changedCount(withPanels(['CPU', 'Graphics', 'Net']), saved)).toBe(2);
    expect(changedCount({ ...withPanels(['CPU']), enabled: !saved.enabled }, saved)).toBe(2);
  });
});
