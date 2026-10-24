import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DeviceEventInfo, IoDevice, RegisteredDevice, SensorInfo, SystemOverview } from '@/lib/api/types';
import { mockApi, renderApp } from '@/test/app';
import { FakeEventSource, statusResponse } from '@/test/fixtures';
import { boardMonitor, memoryNotes, ratedSpeed } from './board';
import { countText, summarize } from './devices';

const NOW = Date.parse('2026-10-08T12:00:00Z');

const lpc = (index: number, kind: string, label: string, unit: string, value: number | null, part = kind.toLowerCase()): SensorInfo => ({
  provider: 'lhm',
  kind,
  name: `Nuvoton NCT6701D / ${label}`,
  unit,
  key: `lhm/lpc/nct6701d/0/${part}/${index}`,
  value,
});

const boardSensors: SensorInfo[] = [
  lpc(3, 'Voltage', '+3.3V', 'V', 3.264),
  lpc(7, 'Voltage', '+3V Standby', 'V', 3.376),
  lpc(8, 'Voltage', 'CMOS Battery', 'V', 2.71),
  lpc(4, 'Voltage', '+12V', 'V', 1.008),
  lpc(0, 'Voltage', 'Vcore', 'V', 1.264),
  lpc(10, 'Voltage', 'Voltage #11', 'V', 0.504),
  lpc(0, 'Temperature', 'CPU Core', '°C', 53),
  lpc(3, 'Temperature', 'Temperature #3', '°C', 3),
  lpc(6, 'Temperature', 'Temperature #6', '°C', 80),
  lpc(1, 'Fan', 'Fan #2', 'RPM', 872),
  lpc(1, 'Other', 'Fan #2', '%', 35.7, 'control'),
  lpc(0, 'Fan', 'Fan #1', 'RPM', 0),
  lpc(0, 'Other', 'Fan #1', '%', 67.8, 'control'),
  { provider: 'lhm', kind: 'Temperature', name: 'Intel Core Ultra 9 285K / CPU Package', unit: '°C', key: 'lhm/intelcpu/0/temperature/0', value: 61 },
];

describe('Board monitoring', () => {
  it('judges rails against their nominal value and spots readings that are not real', () => {
    const board = boardMonitor(boardSensors);

    expect(board.chips).toEqual(['Nuvoton NCT6701D']);
    const voltage = (label: string) => board.voltages.find((v) => v.label === label)!;
    expect(voltage('+3.3V')).toMatchObject({ status: 'ok', nominal: '3.3 V ± 5 %' });
    expect(voltage('+3V Standby').status).toBe('ok');
    expect(voltage('CMOS Battery')).toMatchObject({ status: 'warning' });
    expect(voltage('+12V').status).toBe('doubtful');
    expect(voltage('Vcore').status).toBe('info');
    expect(voltage('Voltage #11').note).toMatch(/Unlabeled input/);

    const temperature = (label: string) => board.temperatures.find((t) => t.label === label)!;
    expect(temperature('CPU Core').status).toBe('ok');
    expect(temperature('Temperature #3').status).toBe('doubtful');
    expect(temperature('Temperature #6').status).toBe('info');
    expect(board.temperatures).toHaveLength(3); // the processor's own sensor is not the board's

    // A fan reports its speed with the duty the board drives it at; an empty header is not a fault.
    expect(board.fans.find((f) => f.label === 'Fan #2')).toMatchObject({ status: 'ok', controlPercent: 35.7 });
    expect(board.fans.find((f) => f.label === 'Fan #1')!.note).toMatch(/0 RPM while driven at 68 %/);
    expect(board.attention).toBe(1);
  });

  it('calls a flat CMOS battery critical and a scorching unlabeled input not real', () => {
    expect(boardMonitor([lpc(8, 'Voltage', 'CMOS Battery', 'V', 2.31)]).voltages[0]!.status).toBe('critical');
    expect(boardMonitor([lpc(6, 'Temperature', 'Temperature #6', '°C', 122)]).temperatures[0]!.status).toBe('doubtful');
    expect(boardMonitor([lpc(6, 'Temperature', 'Temperature #6', '°C', 100)]).temperatures[0]!.status).toBe('warning');
  });

  it('notices memory below its rated speed, sizes that differ and mixed kits', () => {
    expect([ratedSpeed('CMK32GX5M2B5600C36'), ratedSpeed('F5-6000J3038F16G'), ratedSpeed('FF3D532G6000HC38A01'), ratedSpeed('M323R4GA3DB0-CWM')]).toEqual([5600, 6000, 6000, null]);
    const notes = memoryNotes([
      { slot: 'A1', capacity: 16 * 1024 ** 3, configured: 4800, part: 'CMK32GX5M2B5600C36' },
      { slot: 'B1', capacity: 48 * 1024 ** 3, configured: 4800, part: 'CMK96GX5M2B5600C40' },
    ]);
    expect(notes.map((n) => n.tone)).toEqual(['warning', 'info', 'info']);
    expect(notes[0]!.text).toMatch(/runs at 4800 MT\/s, but its modules are rated 5600/);
    expect(notes[1]!.text).toMatch(/16 GB \+ 48 GB\): the first 32 GB run in dual channel/);
    expect(memoryNotes([{ slot: 'A1', capacity: 16 * 1024 ** 3, configured: 6000, part: 'F5-6000J3038F16G' }, { slot: 'B1', capacity: 16 * 1024 ** 3, configured: 6000, part: 'F5-6000J3038F16G' }])).toEqual([]);
  });
});

const device = (key: string, name: string, kind: string, extra: Partial<IoDevice> = {}): IoDevice => ({
  key,
  name,
  kind,
  kinds: [kind],
  type: null,
  class: null,
  bus: 'USB',
  manufacturer: 'Logitech',
  vendorId: '046D',
  productId: 'C54D',
  present: true,
  connected: 1,
  external: true,
  technical: false,
  problemCode: 0,
  problem: null,
  instanceId: `USB\\${key}`,
  location: 'Port_#0003.Hub_#0001',
  service: 'usbccgp',
  driverProvider: 'Microsoft',
  driverVersion: '10.0.26100.1',
  driverDate: '2006-06-21',
  driverInf: 'usb.inf',
  hardwareId: null,
  firstInstalled: '2025-06-18T09:00:00Z',
  lastArrival: '2026-10-05T08:00:00Z',
  lastRemoval: null,
  parts: [],
  ...extra,
});

const registered = (d: IoDevice): RegisteredDevice => ({ device: d, firstSeen: NOW - 86_400_000, lastSeen: NOW });

const ioDevices: RegisteredDevice[] = [
  registered(device('id:046D:C54D', 'LIGHTSPEED Receiver', 'keyboard', { kinds: ['keyboard', 'mouse'] })),
  registered(device('bt:E8C913314F45', 'Galaxy Buds3 Pro', 'audio', { bus: 'Bluetooth', present: false, connected: 0, manufacturer: null, vendorId: null, productId: null, lastRemoval: '2026-10-04T10:00:00Z' })),
  registered(device('id:0000:0002', 'Unknown USB Device (Device Descriptor Request Failed)', 'usb', { present: false, connected: 0, manufacturer: null, vendorId: '0000', productId: '0002' })),
  registered(device('id:14C3:7925', 'MediaTek Wi-Fi 7 MT7925 Wireless LAN Card', 'system', { bus: 'PCI', external: false, problemCode: 43, problem: 'Windows stopped this device because it reported problems.', manufacturer: 'MediaTek' })),
  registered(device('id:05E3:0610', 'USB2.0 Hub', 'usb', { technical: true, manufacturer: 'Genesys Logic' })),
];

const system: SystemOverview = {
  information: { 'board.manufacturer': 'ASUSTeK COMPUTER INC.', 'board.product': 'ROG MAXIMUS Z890 HERO', 'board.chipset': 'Intel Z890', 'firmware.type': 'UEFI', 'firmware.secure_boot': 'on', 'tpm.version': '2.0', 'tpm.maker': 'INTC', 'tpm.state': 'ready', 'memory.slots': '4', 'memory.slots_used': '2' },
  devices: [],
  driversTs: null,
  drivers: [],
  displayTs: null,
  display: null,
  hardwareTs: null,
  hardware: { expansion_slots: [{ SlotDesignation: 'PCIEX16(G5)', CurrentUsage: 4, MaxDataWidth: 13 }, { SlotDesignation: 'PCIEX16(G4)', CurrentUsage: 3, MaxDataWidth: 10 }] },
  availability: [],
  sensors: boardSensors,
  ioDevices,
};

const events: DeviceEventInfo[] = [
  { ts: NOW - 60_000, eventId: 4, title: 'Device keeps disconnecting: LIGHTSPEED Receiver (3 times in 10 minutes)', message: 'Try a port on the back of the PC.', severity: 2, device: 'LIGHTSPEED Receiver', data: { vendorId: '046D', productId: 'C54D' } },
  { ts: NOW - 120_000, eventId: 2, title: 'Device disconnected: LIGHTSPEED Receiver (keyboard, mouse)', message: null, severity: 1, device: 'LIGHTSPEED Receiver', data: { vendorId: '046D', productId: 'C54D' } },
];

describe('Motherboard and Devices tabs', () => {
  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('sums up the register and names device counts naturally', () => {
    expect(summarize(ioDevices)).toEqual({ connected: 2, remembered: 2, problems: 1, disabled: 0, technical: 1 });
    expect([countText('mouse', 2), countText('keyboard', 1), countText('audio', 3)]).toEqual(['2 mice', '1 keyboard', '3 audio devices']);
  });

  it('shows the board, its firmware and its monitoring chip with live readings', async () => {
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/system': () => system,
      'GET /api/v1/system/sensors': () => boardSensors.map((s) => (s.key.endsWith('voltage/8') ? { ...s, value: 2.3 } : s)),
    });
    renderApp('/system?tab=motherboard');

    expect(await screen.findByText('ASUSTeK COMPUTER INC. ROG MAXIMUS Z890 HERO')).toBeInTheDocument();
    expect(screen.getByText('Intel Z890')).toBeInTheDocument();
    expect(screen.getByText('2.0 (INTC), ready')).toBeInTheDocument();
    expect(screen.getByText('2 of 4')).toBeInTheDocument();
    expect(screen.getByText('PCIEX16(G5)')).toBeInTheDocument();
    const voltages = screen.getByRole('region', { name: 'Voltages' });
    expect(await within(voltages).findByText('2.300 V')).toBeInTheDocument(); // the live value
    expect(within(voltages).getByText(/nearly flat: replace it/)).toBeInTheDocument();
  });

  it('groups and sorts the board readings like every other list', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/system': () => system, 'GET /api/v1/system/sensors': () => boardSensors });
    renderApp('/system?tab=motherboard');

    const voltages = await screen.findByRole('region', { name: 'Voltages' });
    await userEvent.click(within(voltages).getByRole('button', { name: 'Group and sort' }));
    await userEvent.selectOptions(within(voltages).getByRole('combobox', { name: 'Add a grouping' }), 'status');
    const list = within(voltages).getByRole('list', { name: 'Voltages' });
    expect(within(list).getAllByText('Check')).toHaveLength(2); // the low CMOS battery's group heading and its badge
    expect(within(list).getAllByText('Normal').length).toBeGreaterThan(2);

    await userEvent.selectOptions(within(voltages).getByRole('combobox', { name: 'Add a sort key' }), 'value');
    expect(within(voltages).getByRole('button', { name: /Grouped by Status, sorted by Value ↓/ })).toBeInTheDocument();
  });

  it('shows the displays as the recorder stores their layout', async () => {
    // The recorder writes { IsRemoteSession, Displays }; the page looked for lower-case names and said none were recorded.
    const display = { IsRemoteSession: false, Displays: [{ TargetKey: 'DISPLAY\AUS32F4', FriendlyName: 'PG32UCDP', Manufacturer: 'AUS', WidthPixels: 3840, HeightPixels: 2160, RefreshRateHz: 240, IsPrimary: true }] };
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/system': () => ({ ...system, display, displayTs: NOW }), 'GET /api/v1/system/sensors': () => [] });
    renderApp('/system');

    expect((await screen.findAllByText(/PG32UCDP/)).length).toBeGreaterThan(0);
    expect(screen.queryByText('No display layout recorded')).not.toBeInTheDocument();
  });

  it('lists devices by status, flags problems, and hides plumbing until asked', async () => {
    mockApi({ 'GET /api/v1/status': () => statusResponse(), 'GET /api/v1/system': () => system, 'GET /api/v1/system/device-events': () => events, 'GET /api/v1/system/sensors': () => [] });
    renderApp('/system');

    // The overview points at the failing device.
    const attention = await screen.findByRole('region', { name: 'Needs attention' });
    expect(within(attention).getByText('MediaTek Wi-Fi 7 MT7925 Wireless LAN Card is not working (code 43)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: /Devices/ }));

    expect(await screen.findByText('LIGHTSPEED Receiver')).toBeInTheDocument();
    expect(screen.getByText('keyboard, mouse · USB · Logitech · 046D:C54D')).toBeInTheDocument();
    expect(screen.getByText('failed to start')).toBeInTheDocument();
    expect(screen.queryByText('USB2.0 Hub')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('checkbox', { name: /Show hubs, controllers/ }));
    expect(screen.getByText('USB2.0 Hub')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /^Not connected/ }));
    expect(screen.getByText('Galaxy Buds3 Pro')).toBeInTheDocument();
    expect(screen.queryByText('LIGHTSPEED Receiver')).not.toBeInTheDocument();

    // Recent changes, and each device's own history in its details.
    expect(within(screen.getByRole('list', { name: 'Recent device changes' })).getByText(/keeps disconnecting/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /^All/ }));
    await userEvent.click(screen.getByRole('button', { name: /LIGHTSPEED Receiver/ }));
    expect(screen.getByText('Recorded changes')).toBeInTheDocument();
    expect(screen.getByText('Port_#0003.Hub_#0001')).toBeInTheDocument();
  });
});
