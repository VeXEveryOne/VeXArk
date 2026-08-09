# Changelog

All notable VeXArk changes are documented here. Versions before 1.0 are
development releases; compatibility can still evolve.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses semantic versioning.

## [Unreleased]

### Added

- Manifest v2 component completeness, typed CE/DE/external/OBB roots, ROM
  identity, restore classes, coverage and per-component verification/failures.
- Typed AOSP, Lineage, crDroid 12, Launcher3, Xiaomi dada, GameSpace and theme
  adapter catalogs. Unknown active provider keys are explicitly archived or
  ignored with a reason.
- Transactional rooted app-data restore with persistent recovery journal,
  size/BLAKE3 verification, read-back health check and rollback.
- Virtualized per-item operation UI with Pending/Running/NeedsUser/Succeeded/
  Skipped/Failed/RolledBack states, central repository unlock and detailed report.
- Rooted home/lock wallpaper backup with restore through `WallpaperManager`,
  contacts/call-log merge with deduplication, system locale and conservative
  same-ROM theme overlay restore.
- Reproducible, discovery-only ROM source scanner plus a Xiaomi 13 (`fuxi`)
  seed catalog covering the open-source families listed in the linked 4PDA topic.

### Changed

- APK inventory no longer hashes every installed artifact during compatibility
  checks; hashes are fetched only for selected packages.
- Play Protect remains enabled and delayed installs are shown as `NeedsUser`.
- Exact-ROM compatibility ignores spoofable fingerprint but requires SDK,
  codename, incremental build, provider schemas, component signatures and versions.
- SMS/MMS, unsupported ROM services, raw Wi-Fi/root-module payloads and unknown
  preferences are reported honestly as archive-only.
- APK preflight now authenticates chunks, object ids, size and whole-file SHA-256
  in one pass. Restore stages the next verified APK while Android installs the
  current package, but keeps PackageManager commits strictly sequential.

### Fixed

- Removed verifier-disabling and obsolete install-session paths.
- A failed shared/full scan can no longer be published as a complete component.
- Root stream abort/drain handling, FIFO cleanup races, global helper locking,
  corrupted manifests, repository concurrency and password-without-input UI.

- Additional restore fixtures across Android 10–16 and multiple ROM families
  remain planned before 1.0.

## [0.8.0-beta.4] — 2026-07-30

### Fixed

- Release builds now run diagnostics tests under their required Dev runtime
  profile before publishing the separately built Stable desktop application.
- Includes the certificate and clean-runner fixes from beta.3.

## [0.8.0-beta.3] — 2026-07-30

### Fixed

- Stable APK certificate verification now hashes the signer PEM certificate
  directly instead of depending on localized `apksigner` text.
- Desktop cancellation UI tests no longer require Android Platform Tools on a
  clean Windows CI runner.

### Included

- Safe cancellation for encrypted backup, Android/iPhone media copy and restore.
- Side-by-side VeXArk Dev / Agent Dev, strict build preflight, KernelSU root
  tracing, structured diagnostics and offline support bundles from beta.2.

## [0.8.0-beta.2] — 2026-07-30

### Added

- KernelSU grants are now requested by Android Agent instead of being gated by
  the unrelated ADB shell UID, and changed grants are detected without restarting Agent.
- Added a side-by-side VeXArk Dev / Agent Dev channel with a separate Android
  package and port, strict Agent build preflight, structured desktop/Android
  diagnostics, an end-to-end root test, and offline support-bundle export.
- Added a prominent cancel-copying action for encrypted backups, Android/iPhone
  media exports and restore operations.
- Interrupted Android media files retain authenticated resume metadata; cancelled
  backups do not publish an incomplete snapshot.
- Root backup failures are collected in an encrypted `backup-failures.json`
  component so a live file can disappear without losing the rest of the snapshot.

### Fixed

- Root streaming no longer performs socket writes from Android's main thread or
  lets late libsu callbacks crash Agent with `NetworkOnMainThreadException`.
- Root scan and read use a FIFO-backed helper stream, and broken pipes no longer
  abort the native helper.
- Desktop and Agent now use identical platform-independent signed-JSON
  canonicalization and strict response-envelope validation.
- Backup scan, file reads and control commands use separate Agent connections,
  preventing stream frames from being consumed by the wrong request.
- Android 16 package snapshot cleanup uses `cmd package unstop`; cancellation
  cleanup independently releases force-stopped apps and abandoned install sessions.

### Verified

- KernelSU root and native helper both returned UID 0 on Xiaomi `24129PN74G`.
- A full encrypted snapshot with 301 components verified all 23,577 referenced
  objects, while a separate MediaStore export copied 574 files / 19.31 GiB.
- The backup and media sets were duplicated to a second physical disk and the
  encrypted mirror passed full verification.
- All 39 Core tests, 21 Desktop tests, Android unit tests and the local Dev build.
- Stable Release builds now fail fast when the APK signing certificate does not match the production certificate.

## [0.8.0-beta.1] — 2026-07-30

### Added

- Native Windows photo/video import from trusted, unlocked iPhones over USB.
- New-only iPhone copying with preserved DCIM structure, HEIC/video/Live Photo
  companions and live transfer progress.
- iPhone discovery, source selection and bilingual setup guidance in the
  Photos & videos page.

### Changed

- Windows desktop and dependent diagnostic tools now target the Windows 10
  2004 API surface required by the native media-import APIs.
- Tagged versions with a prerelease suffix are published as GitHub prereleases.

### Verified

- Release build and self-contained single-file publish on Windows.
- All 38 Core tests and the unpackaged WPF no-device media-import path.
- Physical iPhone transfer remains the focus of this beta.

## [0.7.1] — 2026-07-30

### Added

- Built-in Desktop connection benchmark for testing replacement USB cables and
  motherboard ports without copying personal files.
- Native Windows USB link detection for Low/Full/High/SuperSpeed negotiation,
  including a warning when a USB 3-capable phone falls back to USB 2.0.
- A 256 MiB integrity-checked comparison of ADB and encrypted Fast Wi-Fi with
  a transport recommendation and estimated times for 10, 50 and 100 GiB.

## [0.7.0] — 2026-07-30

### Added

- Optional encrypted Fast LAN data channel for no-root MediaStore exports.
- Automatic ADB, Fast Wi-Fi and destination-disk throughput probes.
- Parallel media transfer with disk-aware worker selection and a 64 MiB buffer cap.
- Resumable `.vexark.part` files with source metadata validation.
- End-to-end SHA-256 verification for each transferred range.
- Live transport, throughput, progress, ETA and active-file diagnostics.
- Per-device Auto, Fast Wi-Fi and ADB transport preference.
- Cross-language C# and Kotlin cryptographic protocol test vectors.

### Changed

- MediaStore reads now use reusable 1 MiB buffers and seekable descriptors.
- ADB DATA frames no longer flush or allocate a second Android buffer per block.
- ADB forwards are reused by media workers and removed when the client closes.
- Media copies are preallocated and published atomically after verification.
- Closing a Fast Wi-Fi listener or rejecting a malformed worker no longer lets
  an executor exception terminate the Android Agent process.
- Local release builds validate matching Desktop/Agent versions and regenerate
  the EXE, APK and SHA-256 checksum bundle together.

### Security

- Fast LAN listeners bind only to the active private Wi-Fi address and exist
  only for an authenticated, short-lived media session.
- Worker and direction keys are separated with HKDF-SHA256; every record uses
  AES-256-GCM with a replay-protected monotonic counter.
- The direct LAN protocol is read-only and accepts only validated MediaStore
  image/video URIs.

### Verified

- Xiaomi 13 (`fuxi`) with a custom ROM: 35.42 MiB/s through ADB and
  44.82 MiB/s through Fast Wi-Fi.
- Encrypted sample transfer, SHA-256 verification, LAN listener shutdown and
  ADB-forward cleanup on the physical device.
- Two consecutive Fast Wi-Fi session shutdowns without an Agent crash.

## [0.6.1] — 2026-07-29

### Added

- Prominent Computer access card at the top of the Android Agent.
- One-tap shortcut to Android Developer options with a best-effort request to
  scroll to and highlight the USB debugging preference.
- Clear three-step USB onboarding for first-time users.

### Changed

- Trusted, waiting, pending-approval and connected computer states now have
  distinct high-visibility presentations.
- New-computer fingerprint approval is displayed inside the primary access
  card instead of a lower secondary section.
- Waiting status now refers to VeXArk Desktop rather than implying that the
  physical ADB connection itself is missing.

### Verified

- Developer settings deep-link and USB debugging highlight on Xiaomi 13
  running a custom Android ROM.
- Android debug build and in-place upgrade from Agent 0.4.0.

## [0.6.0] — 2026-07-28

### Added

- System, Light, Dark and true-black OLED themes for the Windows client.
- Live theme switching with persisted settings and Windows theme tracking.
- English as the default UI language.
- English/Russian language selection in the Windows client and Android Agent.
- Bilingual project website, README and release documentation.
- GitHub Actions workflows for build validation, releases and Pages deployment.
- Security, contribution and issue-reporting documentation.

### Changed

- Centralized desktop appearance settings and theme-aware brushes.
- Android status text and foreground-service notifications now use English
  canonical state with Russian presentation when selected.
- Version bumped to 0.6.0.

## [0.5.0] — 2026-07-28

### Added

- VeXArk name and geometric black-and-white V/X identity.
- Windows ICO/PNG assets and Android adaptive/monochrome launcher icon.
- `.vexark` portable encrypted snapshot extension.

### Changed

- Renamed public Windows and Android product surfaces from the development
  PhoneBackup/MobiArk names to VeXArk.
- Kept one-way compatibility with legacy `.pbbackup` bundles and internal
  storage identifiers.

## [0.4.0] — 2026-07-28

### Added

- Redesigned Fluent-inspired Windows shell and navigation.
- No-root MediaStore exporter for photos and videos.
- Duplicate skipping and original folder preservation for family media copies.
- Export/import of an encrypted snapshot as one portable local file.
- Material You Android UI with safe system-bar insets.

### Changed

- Repository, backup, restore and media workflows moved into dedicated pages.

## [0.3.0] — 2026-07-27

### Added

- Portable APK/split APK backup and streamed restore.
- Rooted CE/DE app-data capture through the constrained native helper.
- Signature checks, UID remapping, `restorecon` and safety snapshots.
- Shared-storage, contacts, SMS/MMS metadata and call-log export.
- Compatibility levels for Portable and Controlled Full restore.

## [0.2.0] — 2026-07-27

### Added

- Encrypted content-addressed repository.
- FastCDC chunking, BLAKE3 verification, zstd compression and AES-256-GCM.
- Argon2id password wrapping and independent 24-word recovery key.
- Atomic manifests, incremental reuse, integrity verification and garbage collection.
- Ed25519 desktop pairing, replay protection and on-device restore approval.

## [0.1.0] — 2026-07-27

### Added

- Initial monorepo with WPF desktop controller, Kotlin/Compose Agent and Rust helper.
- USB/Wireless ADB discovery and physical-device transport deduplication.
- Android 10–16 no-root inventory and capability probing.
- Versioned length-prefixed loopback protocol over `adb forward`.

[Unreleased]: https://github.com/VeXEveryOne/VeXArk/compare/v0.8.0-beta.4...HEAD
[0.8.0-beta.4]: https://github.com/VeXEveryOne/VeXArk/compare/v0.8.0-beta.3...v0.8.0-beta.4
[0.8.0-beta.3]: https://github.com/VeXEveryOne/VeXArk/compare/v0.8.0-beta.2...v0.8.0-beta.3
[0.8.0-beta.2]: https://github.com/VeXEveryOne/VeXArk/compare/v0.8.0-beta.1...v0.8.0-beta.2
[0.8.0-beta.1]: https://github.com/VeXEveryOne/VeXArk/compare/v0.7.1...v0.8.0-beta.1
[0.7.1]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.1
[0.7.0]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.0
[0.6.1]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.6.1
[0.6.0]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.6.0
[0.5.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.1.0
