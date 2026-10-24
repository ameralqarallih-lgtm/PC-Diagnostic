# Driver packages

A driver package lets PC Diagnostic use hardware it has no built-in support for:
- **USB screens** that are not Windows monitors;
- **sensors**, such as a probe on a serial port, a HID device, or a gadget that answers on the local network.

A package is a zip holding a `driver.json`, either at the top or inside one folder. Import it in **Devices › Drivers › Import a package**. Then check it and **approve** it; it is used only once approved.

## What is checked

The recorder checks every import on its own copy, in a folder only administrators can write:
- at most **256 files** and **64 MB**, counted as they are unpacked;
- no file packed more than 200 times smaller (a zip bomb);
- safe relative names only: no `..`, no drive letters, no `:` or reserved names such as `CON`;
- a valid `driver.json`;
- not the id of a driver that comes with PC Diagnostic.

The package then goes to `C:\ProgramData\PcDiagnostic\drivers\<id>`, where administrators write and everyone reads.

**Approval and changes.** An approval names the package's **SHA-256**, worked out over every file in it. A package whose content changes waits for approval again.

**Where packages run.** Packages are loaded only by the **screen host**, which has no administrator rights; the recorder itself never loads one. A package with code runs with a standard user's rights, so approve only packages you trust.

**When reading fails.** Each sensor driver is read every `every` seconds, with 5 s per read. A failed read marks its values unavailable. After 5 failures in a row the driver is set aside until the recorder starts again; the reason shows on the Drivers page.

**Where the values appear.** Sensor values appear with the other sensors under `ext/<package>/<device>/<sensor>`. They can never replace a built-in sensor. At most 64 values are kept per driver. They are recorded, shown in History and available to panels on the screens.

## driver.json

```json
{
  "id": "aquarium",
  "name": "Aquarium probe",
  "version": "1.0",
  "publisher": "Me",
  "sensors": {
    "family": "http-json",
    "url": "http://192.168.1.40/data",
    "every": 5,
    "device": "tank",
    "sensors": [
      { "id": "water", "name": "Water temperature", "kind": "temperature", "unit": "°C", "path": "probes[0].temp" }
    ]
  }
}
```

**Common fields**
- **id:** lower-case letters, digits and dashes.
- **name, version, publisher:** required.

**sensors**
- **family:** `http-json`, `serial-lines` or `hid-report`. Alternatively, **type** names an `ISensorDriver` in the package's **assembly** (a package with code).
- **every:** seconds between reads, 1–60 (2 by default).
- **device:** the device's name in the keys (the package's id when absent).
- **sensors:** 1–64 sensors, each with:
  - **id:** letters, digits or dashes;
  - **name**;
  - **kind:** `temperature`, `clock`, `power`, `fan`, `voltage`, `load` or `other`;
  - **unit**;
  - **scale** and **add:** optional; the value is multiplied by `scale`, then `add` is added.

### http-json

Reads `url`, which must be on this PC or the local network (any other address is refused when connecting; redirects are not followed). The answer may be at most 64 KB and must arrive within 2 s.

Each sensor's **path** says where its value is, for example `data.humidity` or `probes[1].temp`. Numbers, numeric strings and `true`/`false` are read.

### serial-lines

Opens **port** (`COM1`–`COM999`) at **baud** (9600 by default) and reads its lines. A line carries `KEY=value` or `KEY: value` pairs, several separated by `,`, `;` or tabs.

Each sensor's **key** names its value. The last value of each key is kept.

```json
"sensors": { "family": "serial-lines", "port": "COM4", "baud": 115200,
  "sensors": [ { "id": "co2", "name": "CO₂", "unit": "ppm", "key": "CO2" } ] }
```

### hid-report

Reads the input reports of the first HID device with **vid** and **pid** (hexadecimal). Each sensor has:
- **offset:** its first byte; the report id is byte 0.
- **type:** `u8`, `i8`, `u16le`, `i16le`, `u16be`, `i16be`, `u32le`, `i32le` or `f32le`.
- **reportId:** optional; only reports with this id carry it.

```json
"sensors": { "family": "hid-report", "vid": "16C0", "pid": "0480",
  "sensors": [ { "id": "temp", "name": "Temperature", "kind": "temperature", "unit": "°C", "offset": 1, "type": "i16le", "scale": 0.01 } ] }
```

### A package with code

```json
{ "id": "my-sensors", "name": "My sensors", "version": "1.0", "publisher": "Me",
  "assembly": "MySensors.dll",
  "sensors": { "type": "MySensors.Driver", "every": 2, "settings": { "anything": "the driver reads" } } }
```

The type implements `PcDiag.Screens.ISensorDriver` from PcDiag.Screens.Sdk. It has a public constructor `(string id, SensorSpec spec, ISensorIo io)` and `Read` returns its values.

Its libraries (including native ones) are loaded from the package's folder. `ISensorIo` gives it local-network HTTP, serial ports and HID devices.

### Screens

A package can also drive USB screens, alongside its sensors or on its own. It does so with **models**, plus a protocol **family** (`turzx-winusb`) or an **assembly** and **type** implementing `IScreenDriver`. See `ScreenDriverPackage` in the SDK.

## Windows drivers for devices that have none

**Devices › Drivers › Devices without a driver** lists the connected devices Windows has no working driver for. These are problem 28 (no driver), 1 (not set up correctly) and 18 (driver needs installing again), each with its hardware and compatible ids. Devices Windows starts with are never listed: storage controllers, disks, processors, volumes and the system's own devices.

**Import a Windows driver** takes a zip from the device's maker: one `.inf` with its `.sys` and catalog (`.cat`) beside it. It is checked like any driver package (size, names, zip bombs). It is kept in `C:\ProgramData\PcDiagnostic\windows-drivers`, where only administrators can go. The page shows what the INF says:
- the maker (Provider);
- the version and date (DriverVer);
- the device class;
- the hardware ids it installs for **on this PC's architecture** (models decorated NTamd64, or NTarm64 on Arm);
- whether its catalog is signed by a publisher Windows trusts, and by whom (checked offline);
- the devices it fits.

A package with no models for this PC, or no catalog, is refused. An unsigned one is listed but cannot be installed.

**Installing:**
1. You choose the device.
2. The recorder shows a window **on the PC's own screen** with the device, the driver, its maker and its signer. Only *Install* there goes ahead; *Cancel* is the default, and after 2 minutes the window closes and nothing is installed.
3. When asked, a System Restore point is made first. If Windows cannot make one, nothing is installed. Windows may also decline to make a new restore point when one was made in the last 24 hours.
4. Windows puts the package in its driver store (`SetupCopyOEMInf`) and offers it to the device (`UpdateDriverForPlugAndPlayDevices`, **never forced**). It installs only when the driver suits the device better than what it has.
5. The name Windows gave the package (`oemNN.inf`) is written down. *Remove* uninstalls it with `DiUninstallDriver`, again confirmed on the PC, then deletes the package.

Installing and removing need the recorder running as administrator, and work only on the PC itself. A signed catalog alone does not prove the files match it; Windows checks that when it installs, and refuses a driver whose files do not match.
