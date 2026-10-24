import { describe, expect, it } from 'vitest';
import { heatColor, heatGradient, levelOf } from './readings';

describe('reading styles', () => {
  it('colors temperatures from cool blue to red along the sensor limits', () => {
    expect(heatColor(20, 80, 92)).toBe('#38bdf8');
    expect(heatColor(60, 80, 92)).toBe('#22c55e');
    expect(heatColor(80, 80, 92)).toBe('#eab308');
    expect(heatColor(92, 80, 92)).toBe('#ef4444');
    expect(heatColor(120, 80, 92)).toBe('#ef4444');
    // Between stops the color is mixed.
    expect(heatColor(70, 80, 92)).not.toMatch(/^#(22c55e|eab308)$/);
  });

  it('places the heat scale on the thermometer range', () => {
    const stops = heatGradient(20, 110, 80, 92);
    expect(stops[0]).toEqual({ offset: expect.closeTo(20 / 90, 5), color: '#38bdf8' });
    expect(stops.at(-1)).toEqual({ offset: expect.closeTo(72 / 90, 5), color: '#ef4444' });
  });

  it('grades a value against its limits', () => {
    expect(levelOf(null, 80, 90)).toBe('unavailable');
    expect(levelOf(50, 80, 90)).toBe('normal');
    expect(levelOf(80, 80, 90)).toBe('warning');
    expect(levelOf(95, 80, 90)).toBe('critical');
  });
});
