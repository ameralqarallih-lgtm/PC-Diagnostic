import type { DeviceInfo, SystemOverview } from '@/lib/api/types';
import { describeUnavailable } from '@/lib/format';

/**
 * Why readings are unavailable, in terms a person can act on. The recorder notes a reason for every reading it
 * could not take; this names the reading and its device, sets apart those that cannot apply to this PC (Wi-Fi on a
 * wired adapter, a fan on integrated graphics, a device that was removed) and says what can be done about the rest.
 */
export interface ReadingRow {
  series: string;
  /** "NVIDIA GeForce RTX 4090: power". */
  label: string;
  device: string;
  area: string;
  availability: string;
  category: 'missing' | 'notApplicable' | 'notOnThisGpu' | 'removed';
  why: string;
  fix: string;
}

const DAY = 24 * 3600_000;

const METRIC_TEXT: Record<string, string> = {
  power_limit_w: 'power limit',
  power_w: 'power',
  power_pct: 'power (share of its limit)',
  throttle_flags: 'throttling reasons',
  core_mhz: 'core clock',
  mem_mhz: 'memory clock',
  fan_rpm: 'fan speed',
  hotspot_c: 'hotspot temperature',
  temp_c: 'temperature',
  util_pct: 'utilization',
  wifi_quality: 'Wi-Fi signal',
  wifi_rssi: 'Wi-Fi signal strength',
  compressed_b: 'compressed memory',
  power_on_hours: 'power-on hours',
  read_errors: 'read errors',
  write_errors: 'write errors',
  temperature: 'temperature',
  temperature_max: 'highest temperature',
  wear: 'wear',
  package_temp: 'package temperature',
  package_temp_c: 'package temperature',
  package_power_w: 'package power',
};

const AREA_TEXT: Record<string, string> = { cpu: 'CPU', memory: 'Memory', gpu: 'Graphics', disk: 'Drives', net: 'Network', sensor: 'Hardware sensors', volume: 'Volumes' };

/** Windows reliability counters that NVMe drives replace with their own health log. */
const NOT_ON_NVME = new Set(['read_errors', 'write_errors', 'temperature_max']);

/** Metrics integrated graphics do not have (no fan, no hotspot sensor, no memory of their own). */
const NOT_ON_INTEGRATED = new Set(['fan_rpm', 'hotspot_c', 'mem_mhz', 'power_limit_w', 'throttle_flags']);

/**
 * NVIDIA's GeForce RTX 50 series and other Blackwell GPUs: their driver no longer reports a hotspot temperature, so no
 * tool can read it there.
 */
export function noHotspotSensor(device: DeviceInfo | undefined): boolean {
  return !!device && device.kind === 'gpu' && /\bRTX\s*50\d0\b|blackwell/i.test(device.name ?? '');
}

function isIntegrated(device: DeviceInfo | undefined): boolean {
  if (!device || device.kind !== 'gpu') return false;
  const dedicated = device.details?.dedicatedMemoryBytes;
  return typeof dedicated === 'number' ? dedicated < 1024 * 1024 * 1024 : /intel/i.test(device.vendor ?? '');
}

function isWireless(device: DeviceInfo | undefined): boolean {
  const kind = String(device?.details?.kind ?? device?.vendor ?? '');
  return /wi-?fi|wireless|80211/i.test(kind);
}

function metricText(metric: string): string {
  return METRIC_TEXT[metric] ?? metric.replace(/_/g, ' ');
}

/** What can be done, for a reading that applies to this PC but is missing. */
function fixFor(availability: string, area: string, metric: string, device: DeviceInfo | undefined): string {
  switch (availability) {
    case 'RequiresElevation':
      return 'Run the recorder as administrator; the installed recorder service does.';
    case 'DriverMissing':
      return 'Repair the installation: it installs the PawnIO sensor driver that reads CPU and motherboard sensors.';
    case 'PermissionDenied':
      return 'Allow it in Windows privacy settings (e.g. Location for Wi-Fi details), then restart the recorder.';
    case 'Disabled':
      return 'Turn it on in Settings.';
    case 'NotSelected':
      return 'Left out of the custom recording: record everything, or include it in the selection.';
    case 'Error':
      return 'See the recorder log (Settings shows where); it usually recovers by itself on the next sample.';
    default:
      break;
  }
  if (area === 'sensor' && metric.startsWith('disk')) return '';
  if (area === 'gpu' && /intel/i.test(device?.vendor ?? '')) return 'Intel graphics report these only through Intel’s own sensor interface, which Windows does not offer to other programs.';
  if (area === 'gpu' && /nvidia/i.test(device?.vendor ?? '')) return 'Update the NVIDIA driver; power and throttling come from it.';
  if (area === 'gpu' && /amd|ati/i.test(device?.vendor ?? '')) return 'Update the AMD driver; these come from it.';
  if (area === 'memory' && metric === 'compressed_b') return 'Compressed memory is on by default in Windows; it shows once Windows reports it.';
  return 'This hardware or its driver does not report it to Windows.';
}

/** The unavailable readings of an overview, named and classified. */
export function classifyReadings(overview: SystemOverview): ReadingRow[] {
  const newest = Math.max(0, ...overview.devices.map((d) => d.lastSeen));
  const byKey = new Map<string, DeviceInfo>();
  for (const d of overview.devices) if (d.key) byKey.set(d.key, d);
  const disks = overview.devices.filter((d) => d.kind === 'disk');

  return overview.availability
    .filter((a) => a.availability !== 'Available')
    .map((a) => {
      // "gpu.{key}.{metric}", "net.{GUID}.{metric}", "sensor.disk/0/wear", "memory.compressed_b", "cpu/package_temp".
      const [head = a.series, ...rest] = a.series.split(/[./]/);
      let device: DeviceInfo | undefined;
      let metric = rest.at(-1) ?? head;
      let deviceName = AREA_TEXT[head] ?? head;
      if (head === 'gpu' || head === 'net') {
        const key = a.series.slice(head.length + 1, a.series.lastIndexOf('.'));
        device = byKey.get(key) ?? byKey.get(`net-${key}`);
        deviceName = device?.name ?? (head === 'gpu' ? 'Graphics adapter' : 'Network adapter');
      } else if (head === 'sensor' && rest[0] === 'disk') {
        const number = Number(rest[1]);
        device = disks.find((d) => d.details?.number === number);
        deviceName = device?.name ?? `Drive ${rest[1]}`;
        metric = rest.slice(2).join('_') || metric;
      }

      let category: ReadingRow['category'] = 'missing';
      let why = a.reason && !/^Not exposed by this hardware/.test(a.reason) ? a.reason : describeUnavailable(a.availability);
      let fix = fixFor(a.availability, head, head === 'sensor' ? `disk_${metric}` : metric, device);
      if (device && newest - device.lastSeen > DAY) {
        category = 'removed';
        why = 'The device has not been seen for a day (removed, or listed again under a new name after a driver update).';
        fix = 'Nothing to do; its old readings stay in the history.';
      } else if (head === 'net' && metric.startsWith('wifi') && !isWireless(device)) {
        category = 'notApplicable';
        why = 'A wired or virtual adapter has no Wi-Fi signal.';
        fix = 'Nothing to do.';
      } else if (head === 'gpu' && metric === 'hotspot_c' && noHotspotSensor(device)) {
        category = 'notOnThisGpu';
        why = 'Not available on this GPU: NVIDIA removed the hotspot sensor from the GeForce RTX 50 series (Blackwell), so no monitoring tool can read it. The GPU temperature is still recorded.';
        fix = 'Nothing to do.';
      } else if (head === 'gpu' && isIntegrated(device) && NOT_ON_INTEGRATED.has(metric)) {
        category = 'notApplicable';
        why = 'Integrated graphics have no fan, hotspot sensor, memory clock or power limit of their own.';
        fix = 'Nothing to do.';
      } else if (head === 'sensor' && rest[0] === 'disk' && /nvme/i.test(String(device?.details?.busType ?? device?.vendor ?? '')) && NOT_ON_NVME.has(metric)) {
        category = 'notApplicable';
        why = 'NVMe drives do not count read and write errors or report a temperature limit this way; they report media errors, spare capacity and life used instead (Sensors tab).';
        fix = 'Nothing to do.';
      } else if (head === 'sensor' && rest[0] === 'disk') {
        why = 'Neither Windows’ reliability counters nor the drive’s own health log reach the recorder. An NVMe drive behind a RAID or VMD controller driver (Intel RST) or a maker’s own NVMe driver usually does not pass the health request on; on Windows’ standard NVMe driver it does.';
        fix = 'The recorder must run as administrator to ask. Otherwise check the drive with the controller’s or drive maker’s tool (Intel RST, Samsung Magician).';
      }

      return {
        series: a.series,
        label: `${deviceName}: ${metricText(metric)}`,
        device: deviceName,
        area: AREA_TEXT[head] ?? head,
        availability: a.availability,
        category,
        why,
        fix,
      };
    });
}
