<div align="center">
  <img src="docs/assets/vexark-icon.png" width="112" alt="VeXArk logo">
  <h1>VeXArk</h1>
  <p><strong>Your Android data. Your computer. No cloud in between.</strong></p>
  <p>
    <a href="docs/README.ru.md">Русский</a> ·
    <a href="https://vexeveryone.github.io/VeXArk/">Website</a> ·
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.0">Downloads</a> ·
    <a href="CHANGELOG.md">Changelog</a> ·
    <a href="SECURITY.md">Security</a>
  </p>
  <p>
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.0">
      <img alt="GitHub release" src="https://img.shields.io/github/v/release/VeXEveryOne/VeXArk?include_prereleases&style=flat-square">
    </a>
    <a href="https://github.com/VeXEveryOne/VeXArk/actions/workflows/build.yml">
      <img alt="Build" src="https://img.shields.io/github/actions/workflow/status/VeXEveryOne/VeXArk/build.yml?branch=main&style=flat-square&label=build">
    </a>
    <img alt="Windows" src="https://img.shields.io/badge/Windows-10%2F11-0078D4?style=flat-square">
    <img alt="Android" src="https://img.shields.io/badge/Android-10–16-3DDC84?style=flat-square">
    <a href="LICENSE">
      <img alt="GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0--only-black?style=flat-square">
    </a>
  </p>
</div>

---

VeXArk is an offline Android backup system built around a portable Windows
controller, a Material You Android agent and a constrained native root helper.
It supports useful no-root workflows today and unlocks private app-data snapshots
when Magisk, KernelSU or APatch is available.

> [!IMPORTANT]
> VeXArk is under active development. Always keep a second copy of irreplaceable
> data and test restores before relying on it as your only backup.

## Download

| Platform | Package | Requirements |
| --- | --- | --- |
| Windows | [Download `VeXArk.exe`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/VeXArk.exe) | Windows 10/11 x64 |
| Android | [Download `VeXArk-Agent.apk`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/VeXArk-Agent.apk) | Android 10–16, arm64-v8a |
| Checksums | [`SHA256SUMS.txt`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/SHA256SUMS.txt) | Verify before installing |

The Windows build is self-contained and portable. It bundles the matching Agent
APK and Android Platform Tools; neither an installer nor administrator rights are
required.

## Why VeXArk

- **Offline by design.** No account, cloud backend, analytics or telemetry.
- **No-root photo migration.** Copy every MediaStore photo and video into an
  ordinary Windows folder while preserving directories and skipping duplicates.
- **Fast Wi-Fi and resume.** VeXArk benchmarks ADB, encrypted direct LAN and the
  destination disk, copies with up to four workers and resumes interrupted files.
- **Encrypted portable backups.** Export a selected snapshot as one `.vexark`
  file and import it on another PC.
- **Incremental storage.** FastCDC chunking, BLAKE3 addressing and deduplication
  avoid storing unchanged data twice.
- **Strong repository protection.** Argon2id, AES-256-GCM, a random master key
  and an independent 24-word recovery key.
- **Root when you want it.** APK/splits work without root; CE/DE app data,
  permissions and advanced snapshots are enabled only after explicit root access.
- **Restore safety.** Package signatures, paths, compatibility and user approval
  are checked before any destructive step.
- **Native-looking clients.** Fluent-inspired Windows UI with System, Light,
  Dark and OLED themes; Material You and safe system-bar insets on Android.
- **English and Russian.** English is the default, Russian can be selected in
  both clients.

## Screenshots

| Light | Dark |
| --- | --- |
| ![VeXArk light theme](docs/assets/desktop-light.png) | ![VeXArk dark theme](docs/assets/desktop-dark.png) |

<details>
<summary>OLED and Russian UI</summary>

| OLED | Русский |
| --- | --- |
| ![VeXArk OLED theme](docs/assets/desktop-oled.png) | ![VeXArk Russian UI](docs/assets/desktop-russian.png) |

</details>

## Backup modes

### Portable

Designed for moving between ROMs:

- base and split APKs;
- rooted CE/DE app data and external app data;
- runtime permissions, app-ops and selected package state;
- contacts, SMS/MMS metadata, call log and account inventory;
- Documents, Downloads, Music, Podcasts, Recordings and other non-visual media;
- safe Android settings and roles.

Photos and videos are excluded from encrypted snapshots by default and are
handled by the simpler no-root media exporter.

### Controlled Full

Adds ROM-sensitive data such as selected Wi-Fi internals, launcher/SystemUI state
and root-module metadata. Full components are classified as `EXACT`,
`SAME_FAMILY` or `PORTABLE_ONLY` and are never restored blindly.

## What VeXArk will never copy

Android Keystore and StrongBox keys, Gatekeeper credentials, PIN/password/biometric
data, eSIM secrets, Wallet data, DRM keys and hardware-bound passkeys are excluded.
Google account names can be recorded as an encrypted inventory, but passwords,
OAuth tokens and login sessions are not transferable; sign-in is required after
migration.

## Quick start

1. Enable Developer options and USB debugging on the phone.
2. Start `VeXArk.exe`.
3. Connect the phone and approve the Android debugging prompt.
4. Install or update VeXArk Agent from the Devices page.
5. Choose a repository folder and create a password plus recovery key.
6. Start with a small backup and verify it from History.

Root is optional. VeXArk does not patch boot images or install a root provider.

## Build from source

Prerequisites: .NET 9 SDK, JDK 21, Android SDK/NDK and Rust with the
`aarch64-linux-android` target.

```powershell
git clone https://github.com/VeXEveryOne/VeXArk.git
cd VeXArk
.\scripts\build.ps1 -Configuration Release
```

Outputs:

- `artifacts/publish/VeXArk.exe`
- `agent/app/build/outputs/apk/release/app-release.apk`

## Project status

The no-root pipeline and encrypted repository are implemented and covered by
automated tests. Root-only private app-data and Controlled Full restore paths are
capability-gated and still require broader device/ROM validation before a stable
1.0 release.

See the complete [changelog](CHANGELOG.md), [threat model](docs/THREAT_MODEL.md)
[Fast Media protocol](docs/FAST_MEDIA_PROTOCOL.md) and
[contribution guide](CONTRIBUTING.md).

## License and acknowledgements

VeXArk is licensed under `GPL-3.0-only`.

The project is informed by the architecture and workflows of
[DataBackup](https://github.com/XayahSuSuSu/Android-DataBackup) and
[Open Android Backup](https://github.com/mrrfv/open-android-backup).
No Neo Backup source code is included.
