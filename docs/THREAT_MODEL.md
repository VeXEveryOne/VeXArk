# Threat model

- The ADB authorization boundary is not enough on its own. The agent additionally
  pairs a desktop public key and requires an on-device confirmation for restores.
- The agent binds only to loopback. ADB port forwarding is the only supported
  transport.
- Root operations are allow-listed. Package names and paths are validated before
  being passed to the helper.
- Repository metadata and chunks are authenticated encryption payloads.
- Manifest v2 records completeness, root kind, restore class, failures and hash
  verification. A requested rooted backup is not silently downgraded to APK-only.
- Restore rejects absolute paths, `..`, NULs, and symlink escapes.
- App-data restore stages all roots for one package, verifies size/hash, preserves
  PackageManager-owned links, swaps roots transactionally and rolls back on
  restore, launch or health-check failure. A persistent journal lets the Agent
  recover an interrupted transaction.
- Settings are allow-listed and typed. Portable values are capability-checked;
  Exact ROM/device values also require the recorded ROM tuple. Build fingerprint
  is informational because it can be spoofed. Every applied setting is read back
  and the previous value is restored on failure.
- Unknown provider keys are inventoried by name but are not automatically
  restored. Raw/full components and unsupported service payloads are archive-only.
- Keystore, Gatekeeper, lock credentials, eSIM, TEE/StrongBox and DRM data are
  permanent exclusions.
- A complete safety snapshot is required before app data, shared storage,
  portable personal data, wallpaper or settings are modified. Shared-storage
  writes are atomic per file; the safety snapshot remains the recovery source if
  a later file fails.
- Play Protect is never disabled. A long-running package install is surfaced as
  `NeedsUser` while VeXArk waits for the user to decide on the phone.
