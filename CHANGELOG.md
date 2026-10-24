# Changelog

All notable changes to PC Diagnostic & Incident Recorder. Each release's installer is on the
[releases page](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases).

## [1.0.2] - 2026-10-25

### Changed
- A moment's sections are a bar of buttons pinned at its top: **Values · Processes · Services**, plus **Chart** and
  **Events** on History. Each scrolls to its part; Processes and Services also open that list. Picking a moment on
  History scrolls straight to it. ([#5](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/5))

## [1.0.1] - 2026-10-25

### Fixed
- **History moments list every process again.** A process's name is sent once, when it is first seen; while the
  recorder only watched (dashboard open, not recording) those were dropped, so processes already running when a
  recording started had no stored name and were left out of every moment. They are now kept while nothing is stored
  and written with each process's first stored figures. Moments recorded before this fix stay incomplete.
  ([#2](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/2))
- **History moments list the services.** The hourly service list was dropped the same way; it is now kept and written
  with the first stored sample, with the changes since. ([#3](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/3))
- **CPU usage matches Task Manager.** It is now Windows' "% Processor Time" (what Task Manager shows since Windows 11's
  2025 update) instead of "% Processor Utility", which counts work against the base clock and ran far above it while the
  CPU boosted. Memory, GPU, disk and network readings were checked against Windows the same way and matched.
  ([#4](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/4))

## [1.0.0] - 2026-10-24

The first public release: free and open source under the GPL-3.0, every feature for everyone.

### Fixed
- On a GeForce RTX 50 series GPU the hotspot temperature shows as "Not available on this GPU" (NVIDIA removed the
  sensor) instead of counting as an unavailable reading. ([#1](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/1))

## Known issues
- The recorder uses about 4% CPU while the dashboard is open, double its 2% budget.
  ([#6](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/6))
- A process seen in only one sample shows CPU "Unavailable" (correct, but it reads like a fault).
  ([#7](https://github.com/ameralqarallih-lgtm/PC-Diagnostic/issues/7))

[1.0.2]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.2
[1.0.1]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/compare/v1.0.0...v1.0.2
[1.0.0]: https://github.com/ameralqarallih-lgtm/PC-Diagnostic/releases/tag/v1.0.0
