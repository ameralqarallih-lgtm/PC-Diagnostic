# Spike: process sampling and hang detection (Phase 5)

Date: 2026-09-29. Machine: Windows 11 (10.0.26200), about 480 processes, 364 services.

## SYSTEM_PROCESS_INFORMATION layout

`NtQuerySystemInformation(SystemProcessInformation)` returns every process in one call. The winternl.h documentation marks several fields the recorder needs as `Reserved`: create time, user and kernel time, parent PID, private working set and I/O transfer counts. The x64 offsets used (create time 32, user 40, kernel 48, PID 80, parent 88, handles 96, working set 144, pagefile usage 184, read/write transfer 232/240) are verified by `NtProcessesLayoutTests` against documented sources on every test run:

| Field | Compared with | Result |
|---|---|---|
| Create time | `Process.StartTime` | exact match |
| Parent PID | WMI `Win32_Process.ParentProcessId` | exact match |
| User + kernel time | `Process.TotalProcessorTime` | within the time between the two reads |
| Read/write transfer | `GetProcessIoCounters` | ≤ documented value (read slightly earlier) |
| Threads, handles, working set | `Process` | within normal drift |

If a future Windows build moves a field, these tests fail instead of the recorder silently storing wrong data.

## Hang detection

- `IsHungAppWindow` works as documented: the test window was flagged about 5 s after its UI thread stopped pumping messages, then recovered.
- **Test pitfall:** `Start-Sleep` on a PowerShell STA thread pumps messages, so the window never hangs. A busy loop is needed. Windows also does not flag a window during its startup processing.
- **False positive fixed:** when a window hangs, DWM shows a "ghost" window (class `Ghost`, owned by `dwm.exe`) that also reports as hung. Ghost windows are skipped, otherwise `dwm.exe` would appear hung whenever any application hangs.

## Per-process GPU memory from Windows can be implausible

`\GPU Process Memory(pid_*)\Dedicated Usage` reported **33,013 MB for dwm.exe and 61,192 MB for NVIDIA Overlay on a 24 GB RTX 4090**, while the adapter-level counter read 7.9 GB. Values above the adapter's dedicated memory (+5 %) are recorded as `Error` and logged once with the raw value; they are not presented as fact.

## Storage volume

The union of the top 25 by CPU, I/O, GPU and memory gave 69 rows per 2-second tick. After adding activity thresholds (CPU ≥ 0.1 %, I/O ≥ 1 KB/s) and ranking memory every 10 s, it is about 36 rows per tick. The projected total is about 460–500 MB/day after the one-time identity and service rows; this is tracked in the storage-budget task.
