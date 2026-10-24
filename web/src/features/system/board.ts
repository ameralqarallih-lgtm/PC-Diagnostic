import type { SensorInfo } from '@/lib/api/types';

/** How a board reading looks: fine, worth a look, act now, for information only, or probably not a real reading. */
export type BoardStatus = 'ok' | 'warning' | 'critical' | 'info' | 'doubtful';

export interface BoardReading {
  key: string;
  /** The monitoring chip, e.g. "Nuvoton NCT6701D". */
  chip: string;
  label: string;
  value: number | null;
  unit: string;
  status: BoardStatus;
  /** What the value means here. */
  note: string;
  /** The rail's nominal value and tolerance, e.g. "3.3 V ± 5 %". */
  nominal?: string;
}

export interface BoardFan extends BoardReading {
  /** The duty the board drives the header at (fan control), when known. */
  controlPercent: number | null;
}

export interface BoardMonitor {
  /** The board's monitoring chips (Super I/O, embedded controller). */
  chips: string[];
  temperatures: BoardReading[];
  fans: BoardFan[];
  voltages: BoardReading[];
  /** Readings that are warnings or critical. */
  attention: number;
}

/** Sensors of the board's monitoring chips (LibreHardwareMonitor's "lpc" hardware: Super I/O and embedded controller). */
export const BOARD_SENSOR_PREFIX = 'lhm/lpc/';

interface Rail {
  pattern: RegExp;
  nominal: number;
}

// The supply rails a board measures, with ATX tolerances (± 5 %; ± 10 % is out of specification).
const RAILS: Rail[] = [
  { pattern: /^(\+?3\.3 ?v|3vcc|3\.3v|avcc3?|\+?3v standby|3vsb|avsb|vsb3)$/i, nominal: 3.3 },
  { pattern: /^(\+?5 ?v|5vcc|\+?5v standby|5vsb)$/i, nominal: 5 },
  { pattern: /^(\+?12 ?v|12vcc)$/i, nominal: 12 },
];

const BATTERY = /cmos|battery|vbat/i;
const UNLABELED = /^(voltage|temperature|fan)\s*#?\s*\d+$/i;

function chipOf(s: SensorInfo): string {
  const slash = s.name.indexOf(' / ');
  return slash > 0 ? s.name.slice(0, slash) : s.provider;
}

function labelOf(s: SensorInfo): string {
  const slash = s.name.indexOf(' / ');
  return slash > 0 ? s.name.slice(slash + 3) : s.name;
}

/** "lhm/lpc/nct6701d/0/fan/3" → "lhm/lpc/nct6701d/0" and 3. */
function slot(key: string): { hardware: string; index: number } | null {
  const match = /^(.*)\/(?:fan|control)\/(\d+)$/.exec(key);
  return match ? { hardware: match[1]!, index: Number(match[2]) } : null;
}

const volts = (v: number) => `${v.toFixed(3).replace(/0+$/, '').replace(/\.$/, '')} V`;

export function voltage(s: SensorInfo): BoardReading {
  const label = labelOf(s);
  const base = { key: s.key, chip: chipOf(s), label, value: s.value, unit: 'V' };
  const v = s.value;
  if (v == null) return { ...base, status: 'info', note: 'No reading.' };

  if (BATTERY.test(label)) {
    const nominal = '3.0 V (CR2032 coin cell)';
    if (v < 1) return { ...base, nominal, status: 'doubtful', note: 'Too low to be a real battery reading: this board probably does not measure the battery on this input.' };
    if (v < 2.5) return { ...base, nominal, status: 'critical', note: 'The CMOS battery is nearly flat: replace it (CR2032). Until then the clock and firmware settings can be lost whenever the PC is unplugged.' };
    if (v < 2.8) return { ...base, nominal, status: 'warning', note: 'The CMOS battery is getting low; plan to replace it (CR2032).' };
    return { ...base, nominal, status: 'ok', note: 'The CMOS battery is fine.' };
  }

  const rail = RAILS.find((r) => r.pattern.test(label));
  if (rail) {
    const nominal = `${rail.nominal} V ± 5 %`;
    const off = (v - rail.nominal) / rail.nominal;
    // A board that wires the input through another divider reads a fraction or a multiple: not a real reading.
    if (v < rail.nominal * 0.5 || v > rail.nominal * 1.5) {
      return { ...base, nominal, status: 'doubtful', note: `Reads ${volts(v)} for a ${rail.nominal} V rail: the chip's input is scaled differently on this board, so this is not the real rail voltage.` };
    }
    const percent = `${off >= 0 ? '+' : '−'}${Math.abs(off * 100).toFixed(1)} %`;
    if (Math.abs(off) > 0.1) return { ...base, nominal, status: 'critical', note: `${percent} from nominal, outside the ATX limits: the power supply or the board's regulation is struggling.` };
    if (Math.abs(off) > 0.05) return { ...base, nominal, status: 'warning', note: `${percent} from nominal, beyond the usual ± 5 %. Worth watching under load; sensor scaling can also be slightly off.` };
    return { ...base, nominal, status: 'ok', note: `${percent} from nominal.` };
  }

  if (UNLABELED.test(label)) return { ...base, status: 'info', note: 'Unlabeled input: what it measures depends on how the board maker wired the chip.' };
  return { ...base, status: 'info', note: 'Set by the board and the processor; it changes with load and firmware settings.' };
}

export function temperature(s: SensorInfo): BoardReading {
  const label = labelOf(s);
  const base = { key: s.key, chip: chipOf(s), label, value: s.value, unit: '°C' };
  const t = s.value;
  if (t == null) return { ...base, status: 'info', note: 'No reading.' };
  if (t < 5 || t > 125 || t === -128 || t === 127) {
    return { ...base, status: 'doubtful', note: 'No sensor is attached to this input (an unused or disconnected thermistor reads nonsense).' };
  }
  if (UNLABELED.test(label)) {
    // An unused input floats: an unlabeled one this hot is not a part that is still working.
    if (t >= 110) return { ...base, status: 'doubtful', note: 'Too hot to be a working part: most likely an unused input that reads nonsense.' };
    return t >= 95
      ? { ...base, status: 'warning', note: 'An unlabeled input reads very hot: it may be a VRM or chipset sensor, or an unused input that reads nonsense.' }
      : { ...base, status: 'info', note: 'Unlabeled input: the board maker did not say what it measures.' };
  }
  if (t >= 95) return { ...base, status: 'critical', note: 'Very hot: check the case airflow and the fans near this part.' };
  if (t >= 85) return { ...base, status: 'warning', note: 'Hot: fine for short loads, worth watching if it stays here.' };
  return { ...base, status: 'ok', note: 'Normal.' };
}

export function fan(s: SensorInfo, control: number | null): BoardFan {
  const label = labelOf(s);
  const base = { key: s.key, chip: chipOf(s), label, value: s.value, unit: 'RPM', controlPercent: control };
  const rpm = s.value;
  const at = control != null ? ` at ${Math.round(control)} %` : '';
  if (rpm == null || rpm <= 0) {
    return {
      ...base,
      status: 'info',
      note:
        control != null && control >= 30
          ? `0 RPM while driven${at}: no fan on this header, or a fan that has stopped. Check it if a fan should be connected here.`
          : 'No fan on this header, or the fan is stopped (many fans stop when cool).',
    };
  }
  if (rpm < 200 && control != null && control >= 50) return { ...base, status: 'warning', note: `Very slow for its setting${at}: a failing fan or a worn bearing.` };
  return { ...base, status: 'ok', note: `Spinning${at}.` };
}

/** The board's temperatures, fans (with their control duty) and voltages, judged against what is normal. */
export function boardMonitor(sensors: readonly SensorInfo[]): BoardMonitor {
  const board = sensors.filter((s) => s.key.startsWith(BOARD_SENSOR_PREFIX));
  const controls = new Map<string, number | null>();
  for (const s of board.filter((s) => /\/control\/\d+$/.test(s.key))) {
    const at = slot(s.key);
    if (at) controls.set(`${at.hardware}#${at.index}`, s.value);
  }

  const byIndex = (a: SensorInfo, b: SensorInfo) => a.key.localeCompare(b.key, undefined, { numeric: true });
  const temperatures = board.filter((s) => s.kind === 'Temperature').sort(byIndex).map(temperature);
  const voltages = board.filter((s) => s.kind === 'Voltage').sort(byIndex).map(voltage);
  const fans = board
    .filter((s) => s.kind === 'Fan')
    .sort(byIndex)
    .map((s) => {
      const at = slot(s.key);
      return fan(s, at ? (controls.get(`${at.hardware}#${at.index}`) ?? null) : null);
    });
  const all = [...temperatures, ...fans, ...voltages];
  return {
    chips: [...new Set(board.map(chipOf))],
    temperatures,
    fans,
    voltages,
    attention: all.filter((r) => r.status === 'warning' || r.status === 'critical').length,
  };
}

/** One memory module as the inventory lists it. */
export interface MemoryModuleFacts {
  slot: string;
  capacity: number | null;
  /** MT/s it runs at. */
  configured: number | null;
  part: string | null;
}

/** The speed a module is sold at, from common part-number formats (Corsair …5600C36, G.Skill F5-6000…, TeamGroup …6000HC38). */
export function ratedSpeed(part: string | null): number | null {
  if (!part) return null;
  const match = /(?:^|[^0-9])([2-9]\d00)H?C\d{2}/i.exec(part) ?? /F[45]-([2-9]\d00)/i.exec(part);
  return match ? Number(match[1]) : null;
}

/** What is worth knowing about the installed memory: below its rated speed, sizes that differ between channels, mixed kits. */
export function memoryNotes(modules: readonly MemoryModuleFacts[]): { tone: 'warning' | 'info'; text: string }[] {
  const notes: { tone: 'warning' | 'info'; text: string }[] = [];
  const configured = modules.map((m) => m.configured).filter((v): v is number => v != null && v > 0);
  const rated = modules.map((m) => ratedSpeed(m.part)).filter((v): v is number => v != null);
  const running = configured.length ? Math.min(...configured) : null;
  if (running != null && rated.length === modules.length && rated.length > 0 && Math.min(...rated) > running + 100) {
    notes.push({
      tone: 'warning',
      text: `The memory runs at ${running} MT/s, but its modules are rated ${Math.min(...rated)} MT/s (by their part numbers): their XMP/EXPO profile is off in the firmware settings. Turning it on makes memory-heavy work faster.`,
    });
  }

  const sizes = [...new Set(modules.map((m) => m.capacity).filter((v): v is number => v != null && v > 0))];
  if (modules.length >= 2 && sizes.length > 1) {
    const gb = (b: number) => `${Math.round(b / 1024 ** 3)} GB`;
    const smallest = Math.min(...sizes);
    notes.push({
      tone: 'info',
      text: `The modules differ in size (${modules.map((m) => (m.capacity ? gb(m.capacity) : '?')).join(' + ')}): the first ${gb(smallest * modules.length)} run in dual channel and the rest in single channel, so part of the memory is slower than with matched modules.`,
    });
  }

  const parts = [...new Set(modules.map((m) => m.part?.trim()).filter(Boolean))];
  if (modules.length >= 2 && parts.length > 1) {
    notes.push({ tone: 'info', text: 'The modules come from different kits. Mixed kits often cannot run at their rated speed together; if the PC is unstable with XMP/EXPO on, this is the first suspect.' });
  }
  return notes;
}
