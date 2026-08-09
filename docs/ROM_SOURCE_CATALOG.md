# ROM source settings catalog

`scripts/rom_catalog.py` discovers settings implemented by open-source Android
ROMs without installing those ROMs. Discovery output is deliberately separated
from the Agent allow-list: a discovered key cannot be restored until it has a
typed validator, target capability probe, read-back check and rollback path.

The Xiaomi 13 (`fuxi`) seed list lives in
`catalog/rom-sources/xiaomi-13-fuxi.json`. The list records what was present in
the linked 4PDA topic and distinguishes an official upstream manifest from the
exact source provenance of a particular unofficial build. Proprietary ports are
kept in the inventory but are not represented as fully open source.

Validate the catalog:

```powershell
python scripts/rom_catalog.py verify `
  --catalog catalog/rom-sources/xiaomi-13-fuxi.json
```

Scan an existing checkout:

```powershell
python scripts/rom_catalog.py scan `
  --catalog catalog/rom-sources/xiaomi-13-fuxi.json `
  --source crdroid=D:\src\crdroid-settings `
  --output artifacts\rom-catalog\crdroid.json
```

Or shallow-clone only the explicitly reviewed repositories in the catalog into
an isolated cache and scan them:

```powershell
python scripts/rom_catalog.py sync-scan `
  --catalog catalog/rom-sources/xiaomi-13-fuxi.json `
  --work-dir D:\rom-source-cache `
  --rom lineageos --rom crdroid `
  --output artifacts\rom-catalog\fuxi.json
```

`sync-scan` refuses to use a non-empty directory unless it contains its own
`.vexark-rom-source-workspace` marker. It does not run `repo sync`, build a ROM,
or touch a connected phone.

The scanner currently recognizes direct Settings provider calls, Lineage
provider calls, shared preferences, XML preference keys and system properties.
Dynamic keys, native/HAL constraints and proprietary components remain
`archiveOnly` or `exactDevice` candidates until a human-reviewed adapter exists.
