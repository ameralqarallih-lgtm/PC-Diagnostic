import { describe, expect, it } from 'vitest';
import type { SystemOverview } from '@/lib/api/types';
import { classifyReadings } from './readings';

const NOW = Date.parse('2026-10-08T12:00:00Z');
const DAY = 24 * 3600_000;

const overview: SystemOverview = {
  information: {},
  devices: [
    { id: 1, kind: 'gpu', name: 'NVIDIA GeForce RTX 4090', vendor: 'NVIDIA', details: { dedicatedMemoryBytes: 25e9 }, firstSeen: NOW - 9 * DAY, lastSeen: NOW, key: 'gpu-10de-2684-467519da' },
    { id: 3, kind: 'gpu', name: 'NVIDIA GeForce RTX 4090', vendor: 'NVIDIA', details: { dedicatedMemoryBytes: 25e9 }, firstSeen: NOW - 9 * DAY, lastSeen: NOW - 2 * DAY, key: 'gpu-10de-2684-467519da-1' },
    { id: 2, kind: 'gpu', name: 'Intel(R) Graphics', vendor: 'Intel', details: { dedicatedMemoryBytes: 134217728 }, firstSeen: NOW - 9 * DAY, lastSeen: NOW, key: 'gpu-8086-7d67-88ef1043' },
    { id: 6, kind: 'network', name: 'Ethernet 3', vendor: 'Ethernet', details: { kind: 'Ethernet' }, firstSeen: NOW - 9 * DAY, lastSeen: NOW, key: 'net-{7B1B3B8E-DD70-42B2-83A0-8BDCDDE78DE9}' },
    { id: 4, kind: 'disk', name: 'Samsung SSD 9100 PRO', vendor: 'NVMe', details: { number: 0 }, firstSeen: NOW - 9 * DAY, lastSeen: NOW, key: 'disk-bfac5402cfa6' },
    { id: 5, kind: 'disk', name: 'WD Blue', vendor: 'SATA', details: { number: 1, busType: 'SATA' }, firstSeen: NOW - 9 * DAY, lastSeen: NOW, key: 'disk-1' },
  ],
  driversTs: null,
  drivers: [],
  displayTs: null,
  display: null,
  hardwareTs: null,
  hardware: null,
  availability: [
    { series: 'gpu.gpu-10de-2684-467519da-1.power_w', availability: 'Unsupported', reason: 'Not exposed by this hardware, driver or Windows version.', since: NOW },
    { series: 'gpu.gpu-8086-7d67-88ef1043.fan_rpm', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'gpu.gpu-8086-7d67-88ef1043.temp_c', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'gpu.gpu-8086-7d67-88ef1043.power_pct', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'net.{7B1B3B8E-DD70-42B2-83A0-8BDCDDE78DE9}.wifi_quality', availability: 'NotPresent', reason: 'The device is not present.', since: NOW },
    { series: 'sensor.disk/0/wear', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'sensor.disk/0/read_errors', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'sensor.disk/1/read_errors', availability: 'Unsupported', reason: null, since: NOW },
    { series: 'cpu/package_temp', availability: 'DriverMissing', reason: null, since: NOW },
    { series: 'cpu', availability: 'Available', reason: null, since: NOW },
  ],
  sensors: [],
};

describe('unavailable readings', () => {
  it('are named by device and set apart when they cannot apply to this PC', () => {
    const rows = classifyReadings(overview);
    const by = (series: string) => rows.find((r) => r.series === series)!;

    expect(rows).toHaveLength(9);
    expect(by('gpu.gpu-10de-2684-467519da-1.power_w')).toMatchObject({ label: 'NVIDIA GeForce RTX 4090: power', category: 'removed' });
    expect(by('gpu.gpu-8086-7d67-88ef1043.fan_rpm')).toMatchObject({ label: 'Intel(R) Graphics: fan speed', category: 'notApplicable' });
    expect(by('gpu.gpu-8086-7d67-88ef1043.power_pct')).toMatchObject({ label: 'Intel(R) Graphics: power (share of its limit)', category: 'notApplicable' });
    // Intel's driver does not give out an integrated GPU's temperature: not a fault on this PC.
    expect(by('gpu.gpu-8086-7d67-88ef1043.temp_c')).toMatchObject({ category: 'notOnThisGpu', why: expect.stringMatching(/^Not available on this GPU/) });
    expect(by('net.{7B1B3B8E-DD70-42B2-83A0-8BDCDDE78DE9}.wifi_quality')).toMatchObject({ label: 'Ethernet 3: Wi-Fi signal', category: 'notApplicable' });

    // What applies but is missing says what can be done.
    expect(by('sensor.disk/0/wear')).toMatchObject({ label: 'Samsung SSD 9100 PRO: wear', category: 'missing', fix: expect.stringContaining('administrator') });
    // NVMe drives report errors their own way; a SATA drive's error counters do apply.
    expect(by('sensor.disk/0/read_errors')).toMatchObject({ category: 'notApplicable', why: expect.stringContaining('media errors') });
    expect(by('sensor.disk/1/read_errors')).toMatchObject({ label: 'WD Blue: read errors', category: 'missing' });
    expect(by('cpu/package_temp')).toMatchObject({ label: 'CPU: package temperature', category: 'missing', why: 'Needs the sensor driver (PawnIO)', fix: expect.stringContaining('PawnIO') });
  });

  it('say why an RTX 50 series card has no hotspot reading, and where to see it', () => {
    const rtx50 = { id: 7, kind: 'gpu', name: 'NVIDIA GeForce RTX 5070 Ti', vendor: 'NVIDIA', details: { dedicatedMemoryBytes: 17e9 }, firstSeen: NOW - DAY, lastSeen: NOW, key: 'gpu-10de-2c05-1' };
    const rows = classifyReadings({ ...overview, devices: [...overview.devices, rtx50], availability: [
      { series: 'gpu.gpu-10de-2c05-1.hotspot_c', availability: 'Unsupported', reason: null, since: NOW },
      { series: 'gpu.gpu-10de-2c05-1.fan_rpm', availability: 'Unsupported', reason: null, since: NOW },
    ] });
    // The sensor exists (the recorder reads it through PawnIO): a missing reading, not one the card cannot have.
    expect(rows[0]).toMatchObject({ label: 'NVIDIA GeForce RTX 5070 Ti: hotspot temperature', category: 'missing', why: expect.stringContaining('PawnIO'), fix: expect.stringContaining('Repair the installation') });
    expect(rows[1]).toMatchObject({ category: 'missing' });
  });
});
