import { Bluetooth, Cable, Camera, Cpu, Gamepad2, HardDrive, Headphones, Keyboard, Monitor, Mouse, Network, Printer, Radar, ScanLine, ShieldCheck, Smartphone, Usb, type LucideIcon } from 'lucide-react';
import type { IoDevice, RegisteredDevice } from '@/lib/api/types';

export const KIND_LABELS: Record<string, string> = {
  keyboard: 'Keyboards',
  mouse: 'Mice and pointers',
  'game-controller': 'Game controllers',
  input: 'Other input devices',
  audio: 'Audio',
  camera: 'Cameras',
  imaging: 'Scanners and imaging',
  display: 'Monitors',
  storage: 'Drives and memory sticks',
  portable: 'Phones and portable devices',
  network: 'Network adapters',
  bluetooth: 'Bluetooth',
  printer: 'Printers',
  ports: 'Serial ports',
  security: 'Security keys and readers',
  sensor: 'Sensors',
  usb: 'Other USB devices',
  other: 'Other devices',
  system: 'Devices of this PC with a problem',
};

const KIND_NAMES: Record<string, string> = {
  keyboard: 'keyboard',
  mouse: 'mouse',
  'game-controller': 'game controller',
  input: 'input device',
  audio: 'audio',
  camera: 'camera',
  imaging: 'scanner',
  display: 'monitor',
  storage: 'drive',
  portable: 'phone',
  network: 'network',
  bluetooth: 'Bluetooth',
  printer: 'printer',
  ports: 'serial port',
  security: 'security',
  sensor: 'sensor',
  usb: 'USB',
  other: 'other',
  system: 'system',
};

const KIND_PLURALS: Record<string, string> = {
  keyboard: 'keyboards',
  mouse: 'mice',
  'game-controller': 'game controllers',
  input: 'input devices',
  audio: 'audio devices',
  camera: 'cameras',
  imaging: 'scanners',
  display: 'monitors',
  storage: 'drives',
  portable: 'phones',
  network: 'network adapters',
  bluetooth: 'Bluetooth devices',
  printer: 'printers',
  ports: 'serial ports',
  security: 'security devices',
  sensor: 'sensors',
  usb: 'other USB devices',
  other: 'other devices',
  system: 'devices of this PC',
};

/** "1 keyboard", "2 mice", "3 audio devices". */
export function countText(kind: string, n: number): string {
  if (n === 1) return `1 ${kind === 'audio' ? 'audio device' : kind === 'usb' ? 'other USB device' : kind === 'bluetooth' ? 'Bluetooth device' : (KIND_NAMES[kind] ?? kind)}`;
  return `${n} ${KIND_PLURALS[kind] ?? kind}`;
}

const KIND_ICONS: Record<string, LucideIcon> = {
  keyboard: Keyboard,
  mouse: Mouse,
  'game-controller': Gamepad2,
  input: Keyboard,
  audio: Headphones,
  camera: Camera,
  imaging: ScanLine,
  display: Monitor,
  storage: HardDrive,
  portable: Smartphone,
  network: Network,
  bluetooth: Bluetooth,
  printer: Printer,
  ports: Cable,
  security: ShieldCheck,
  sensor: Radar,
  usb: Usb,
  other: Usb,
  system: Cpu,
};

const KIND_ORDER = Object.keys(KIND_LABELS);

export const kindLabel = (kind: string) => KIND_LABELS[kind] ?? kind;
export const kindName = (kind: string) => KIND_NAMES[kind] ?? kind;
export const kindIcon = (kind: string): LucideIcon => KIND_ICONS[kind] ?? Usb;
export const kindRank = (kind: string) => {
  const i = KIND_ORDER.indexOf(kind);
  return i < 0 ? KIND_ORDER.length : i;
};

/** Problem code 22: disabled on purpose, not a fault. */
export const DISABLED = 22;

/** A device that is connected but not working (disabled devices are not faults). */
export const hasFault = (d: IoDevice) => d.present && d.problemCode !== 0 && d.problemCode !== DISABLED;

/** Windows names a device that failed to start this way ("Unknown USB Device (Device Descriptor Request Failed)"). */
export function failedToStart(d: IoDevice): string | null {
  const match = /^Unknown USB Device \((.+)\)$/i.exec(d.name);
  return match ? match[1]! : null;
}

export type DeviceState = 'problem' | 'connected' | 'disconnected';

export function stateOf(d: IoDevice): DeviceState {
  if (hasFault(d) || (d.present && d.problemCode === DISABLED)) return 'problem';
  return d.present ? 'connected' : 'disconnected';
}

export const STATE_LABELS: Record<DeviceState, string> = { problem: 'With a problem', connected: 'Connected', disconnected: 'Not connected' };

/** The other kinds a device has besides its main one ("keyboard and mouse" for a receiver). */
export function kindsText(d: IoDevice): string {
  const kinds = d.kinds.filter((k) => !['usb', 'input', 'bluetooth', 'other'].includes(k));
  return kinds.length > 1 ? kinds.map(kindName).join(', ') : '';
}

/** The newest time a device was connected or removed (Windows' dates, else the recorder's). */
export function lastActivity(r: RegisteredDevice): number {
  const times = [r.device.lastArrival, r.device.lastRemoval].filter(Boolean).map((t) => Date.parse(t!));
  return Math.max(r.lastSeen, ...times.filter(Number.isFinite));
}

export interface DeviceSummary {
  connected: number;
  remembered: number;
  problems: number;
  disabled: number;
  technical: number;
}

export function summarize(rows: readonly RegisteredDevice[]): DeviceSummary {
  const shown = rows.filter((r) => !r.device.technical);
  return {
    connected: shown.filter((r) => r.device.present).length,
    remembered: shown.filter((r) => !r.device.present).length,
    problems: rows.filter((r) => hasFault(r.device)).length,
    disabled: rows.filter((r) => r.device.present && r.device.problemCode === DISABLED).length,
    technical: rows.length - shown.length,
  };
}
