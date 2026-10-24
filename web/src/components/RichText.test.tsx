import { describe, expect, it } from 'vitest';
import { structure, tokenize } from './RichText';

const kinds = (text: string) => tokenize(text).filter((t) => t.kind !== 'text').map((t) => [t.kind, t.text]);

describe('report text', () => {
  it('finds the times, figures, incidents and names in a sentence', () => {
    expect(kinds('14 unclean stops of this kind, the last detected at 19:43:09: The recorder stopped unexpectedly after 2026-10-15 15:42:57 UTC; Windows kept running.')).toEqual([
      ['number', '14'],
      ['time', '19:43:09'],
      ['time', '2026-10-15 15:42:57 UTC'],
    ]);
    expect(kinds('CPU at 97 % for 12 min (peak 4.2 GHz); chrome.exe used 3.5 GB; Event ID 41 in INC-20261015-00003.')).toEqual([
      ['number', '97 %'],
      ['number', '12 min'],
      ['number', '4.2 GHz'],
      ['code', 'chrome.exe'],
      ['number', '3.5 GB'],
      ['code', 'Event ID 41'],
      ['incident', 'INC-20261015-00003'],
    ]);
    expect(kinds('The GPU ran at 84 °C.')).toEqual([['number', '84 °C']]);
    // Single digits alone stay text (one, two…), as does an ordinary word.
    expect(kinds('It happened 3 times today.')).toEqual([]);
  });

  it('turns a lead and parts into a headline and items', () => {
    expect(structure('14 unclean stops of this kind, the last detected at 19:43:09: The recorder stopped unexpectedly; Windows kept running.')).toEqual({
      lead: '14 unclean stops of this kind, the last detected at 19:43:09',
      parts: ['The recorder stopped unexpectedly', 'Windows kept running.'],
    });
    // A time's colons are not a lead.
    expect(structure('It stopped at 19:43:09 and came back.')).toEqual({ lead: null, parts: ['It stopped at 19:43:09 and came back.'] });
  });
});
