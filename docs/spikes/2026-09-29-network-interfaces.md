# Spike: which network interfaces to record, and Wi-Fi access (Phase 4)

Date: 2026-09-29. Machine: Windows 11 (10.0.26200), Intel I226-V (active, 1 Gbps), Hyper-V vEthernet (Default Switch, WSL), MediaTek Wi-Fi 7 MT7925 (not present).

## Problem found by the first real run

`NetworkInterface.GetAllNetworkInterfaces()` returned 26 interfaces. They included each NDIS **filter layer** stacked on an adapter, for example `Ethernet 3-WFP Native MAC Layer LightWeight Filter-0000` and `Ethernet 3-QoS Packet Scheduler-0000`, plus WAN Miniports and Hyper-V switch extension adapters. The filter layers report the same byte counters as the adapter underneath, so the headline network figure counted the Intel NIC four times (2644 kbps instead of about 660 kbps).

## Fix, verified with a probe

`GetIfTable2` → `MIB_IF_ROW2.InterfaceAndOperStatusFlags` has `HardwareInterface` (bit 0) and `FilterInterface` (bit 1).

- Probed with `Add-Type`. `InterfaceGuid` at offset 12 matches `NetworkInterface.Id` (e.g. `{7B1B3B8E-…}` for Ethernet 3). The flags byte at offset 1152 sets `filter=1` on every `…-0000` layer and `hw=1` on the physical NICs. Row size is 1352 bytes on x64.
- Rule: skip filter layers, loopback, and IPv6 transition tunnels (Teredo, ISATAP, 6to4, IP-HTTPS). Skip non-hardware system plumbing (WAN Miniport, RAS Async Adapter, kernel debugger adapter, Hyper-V switch extension adapter). Keep physical NICs, Hyper-V vEthernet adapters, VPNs and Bluetooth PAN.
- Headline throughput excludes Virtual and VPN kinds, because their traffic is also counted on the physical adapter.

## Wi-Fi and the Windows 11 24H2 location change

[Microsoft: Changes to API behavior for Wi-Fi access and location](https://learn.microsoft.com/windows/win32/nativewifi/wi-fi-access-location-changes)

- Without location consent, `WlanQueryInterface(wlan_intf_opcode_current_connection)` returns `ERROR_ACCESS_DENIED`. The first call also shows a system location prompt.
- `wlan_intf_opcode_rssi` is not on the affected list. The recorder uses it for signal strength every tick and derives quality with the documented mapping (−100 dBm = 0 %, −50 dBm = 100 %).
- The SSID (current connection) is read only when the user enables `Privacy:CaptureWifiNetworkNames`. When denied, it is recorded as `PermissionDenied`.
- **Not verified on hardware:** this machine's Wi-Fi card is "Not Present" (Windows lists five MT7925 instances, none active), so the WLAN path is covered by unit tests only.
