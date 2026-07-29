# Changelog

All notable VeXArk changes are documented here. Versions before 1.0 are
development releases; compatibility can still evolve.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses semantic versioning.

## [Unreleased]

- Stable signing and broader rooted-device validation.
- Additional restore fixtures across Android 10–16 and multiple ROM families.

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

[Unreleased]: https://github.com/VeXEveryOne/VeXArk/compare/v0.6.1...HEAD
[0.6.1]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.6.1
[0.6.0]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.6.0
[0.5.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/VeXEveryOne/VeXArk/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.1.0
