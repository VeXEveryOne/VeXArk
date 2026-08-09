<div align="center">
  <img src="docs/assets/vexark-icon.png" width="112" alt="VeXArk logo">
  <h1>VeXArk</h1>
  <p><strong>Your Android data. Your computer. No cloud in between.</strong></p>
  <p>
    <a href="docs/README.ru.md">Русский</a> ·
    <a href="https://vexeveryone.github.io/VeXArk/">Website</a> ·
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.1">Downloads</a> ·
    <a href="CHANGELOG.md">Changelog</a> ·
    <a href="SECURITY.md">Security</a>
  </p>
  <p>
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.1">
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
when Magisk, KernelSU or APatch is available. The Windows client can also import
original photos and videos from an iPhone without installing an iOS app.

> [!IMPORTANT]
> VeXArk is under active development. Always keep a second copy of irreplaceable
> data and test restores before relying on it as your only backup.

## Download

| Platform | Package | Requirements |
| --- | --- | --- |
| Windows | [Download `VeXArk.exe`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/VeXArk.exe) | Windows 10/11 x64 |
| Android | [Download `VeXArk-Agent.apk`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/VeXArk-Agent.apk) | Android 10–16, arm64-v8a |
| Checksums | [`SHA256SUMS.txt`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/SHA256SUMS.txt) | Verify before installing |

The Windows build is self-contained and portable. It bundles the matching Agent
APK and Android Platform Tools; neither an installer nor administrator rights are
required.

> Testing iPhone import? Use the
> [`v0.8.0-beta.4` prerelease](https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.8.0-beta.4).

## Why VeXArk

- **Offline by design.** No account, cloud backend, analytics or telemetry.
- **No-root photo migration.** Copy every MediaStore photo and video into an
  ordinary Windows folder while preserving directories and skipping duplicates.
- **iPhone photo and video import.** Copy new originals, HEIC files, videos and
  Live Photo companions from a trusted, unlocked iPhone over USB while preserving
  its DCIM folder structure.
- **Fast Wi-Fi and resume.** VeXArk benchmarks ADB, encrypted direct LAN and the
  destination disk, copies with up to four workers and resumes interrupted files.
- **Safe cancellation.** Stop backup, media copying or restore from the status
  panel without publishing a partial snapshot; Android media resumes next time.
- **Cable and port tester.** The Desktop client shows the negotiated USB link,
  measures ADB and Fast Wi-Fi without reading personal files, recommends the
  faster transport and estimates transfer time before a long copy.
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
- rooted CE/DE app data, `/Android/data`, OBB and optional caches, with an
  explicit `Complete`/`Partial` result for every data root;
- dangerous runtime-permission grants, enabled state, battery exemption and
  per-app locales (protected permission flags and arbitrary app-ops are not replayed);
- contacts and call log with merge/deduplication; SMS/MMS is retained as an
  encrypted archive until temporary default-SMS-role restore is implemented;
- Documents, Downloads, Music, Podcasts, Recordings and other non-visual media;
- a typed AOSP/Lineage/crDroid settings catalog, coverage for unknown provider
  keys, confirmed roles, system language, compatible theme overlays and rooted
  home/lock wallpapers.

Photos and videos are excluded from encrypted snapshots by default and are
handled by the simpler no-root media exporter.

iPhone support is intentionally limited to the photo/video importer. VeXArk does
not read iOS app data, messages, credentials or device backups.

### Controlled Full

Adds encrypted archive-only snapshots of selected Wi-Fi internals and KernelSU/
Magisk module files. Compatible Launcher3 data is handled separately as
`ExactRom` and restored only after ROM identity, package signature and safety
snapshot checks. Archive-only components are never applied automatically.

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
5. Open Photos & videos and run Connection test whenever you change a cable or port.
6. Choose a repository folder and create a password plus recovery key.
7. Start with a small backup and verify it from History.

Root is optional. VeXArk does not patch boot images or install a root provider.

For iPhone media import:

1. Install Apple Devices from the Microsoft Store.
2. Connect the iPhone over USB, unlock it and approve **Trust This Computer**.
3. Open **Photos & videos**, select **Find iPhone**, choose the destination and
   select **Copy from iPhone**.

If iCloud Photos uses optimized storage, download the full-resolution originals
to the iPhone before importing.

## Build from source

Prerequisites: .NET 9 SDK, JDK 21, Android SDK/NDK and Rust with the
`aarch64-linux-android` target.

```powershell
git clone https://github.com/VeXEveryOne/VeXArk.git
cd VeXArk
.\scripts\build.ps1 -Configuration Release
```

To build the side-by-side diagnostic channel:

```powershell
.\scripts\build.ps1 -Configuration Debug -Channel Dev
```

If local PowerShell execution policy blocks scripts, run the same command through
`powershell.exe -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Debug -Channel Dev`.
The Dev desktop targets only `com.vex.phonebackup.agent.dev` on port `49322`;
it does not replace the stable Agent.

Outputs:

- `artifacts/publish/VeXArk.exe`
- `artifacts/dev/VeXArk-Dev.exe`
- `artifacts/dev/VeXArk-Agent-Dev.apk`
- `artifacts/dev/SHA256SUMS.txt`
- `agent/app/build/outputs/apk/release/app-release.apk`

## Project status

The no-root pipeline, encrypted manifest v2 repository, typed ROM coverage and
transactional rooted app-data restore are implemented and covered by automated
tests. Settings without a stable public/provider API (including most
XiaomiParts, GameSpace, LiveDisplay and charging-control internals) are reported
as archive-only instead of being written blindly. Broad device/ROM round-trip
validation is still required before a stable 1.0 release.

See the complete [changelog](CHANGELOG.md), [threat model](docs/THREAT_MODEL.md),
[Fast Media protocol](docs/FAST_MEDIA_PROTOCOL.md),
[ROM source catalog](docs/ROM_SOURCE_CATALOG.md) and
[contribution guide](CONTRIBUTING.md).

## License and acknowledgements

VeXArk is licensed under `GPL-3.0-only`.

The project is informed by the architecture and workflows of
[DataBackup](https://github.com/XayahSuSuSu/Android-DataBackup) and
[Open Android Backup](https://github.com/mrrfv/open-android-backup).
No Neo Backup source code is included.
