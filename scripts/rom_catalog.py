#!/usr/bin/env python3
"""Build a conservative catalog of Android ROM settings from source trees.

The output is discovery data, not an allow-list for restore.  Every finding is
marked reviewRequired and must be promoted to a typed Agent descriptor only
after capability, read-back and rollback behavior have been implemented.
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import subprocess
import sys
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable


TEXT_SUFFIXES = {".java", ".kt", ".kts", ".xml", ".aidl", ".cpp", ".cc", ".h"}
IGNORED_PARTS = {".git", ".repo", "build", "out", "bin", "obj", "node_modules"}
ANDROID_NS_KEY = "{http://schemas.android.com/apk/res/android}key"

PROVIDER_CALL = re.compile(
    r"(?P<owner>(?:Lineage)?Settings)\s*\.\s*(?P<provider>System|Secure|Global)"
    r"\s*\.\s*(?:get|put)(?:Int|Long|Float|String|StringForUser|IntForUser|LongForUser)?"
    r"\s*\(\s*[^,;\n]{1,300}\s*,\s*"
    r"(?P<key>\"(?:\\.|[^\"])+\"|[A-Za-z_$][\w.$]*)",
    re.MULTILINE,
)
SHARED_PREF_CALL = re.compile(
    r"\.(?:get|put)(?:Boolean|Int|Long|Float|String|StringSet)\s*\(\s*"
    r"(?P<key>\"(?:\\.|[^\"])+\"|[A-Za-z_$][\w.$]*)"
)
SYSTEM_PROPERTY_CALL = re.compile(
    r"(?:SystemProperties|android::base)\s*(?:\.|::)\s*(?:get|set|GetProperty|SetProperty)"
    r"\s*\(\s*(?P<key>\"(?:\\.|[^\"])+\")"
)
STRING_CONSTANT = re.compile(
    r"(?:const\s+val|static\s+final\s+String|String)\s+(?P<name>[A-Za-z_$][\w$]*)"
    r"\s*=\s*\"(?P<value>(?:\\.|[^\"])*)\""
)
XML_KEY = re.compile(r"(?:android:)?key\s*=\s*\"(?P<key>[^\"]+)\"")
RESOURCE_STRING = re.compile(
    r"<string\s+[^>]*name\s*=\s*\"(?P<name>[A-Za-z0-9_.-]+)\"[^>]*>"
    r"(?P<value>.*?)</string>",
    re.DOTALL,
)


@dataclass(frozen=True)
class SourceSpec:
    rom_id: str
    root: Path


@dataclass(frozen=True)
class Finding:
    stableId: str
    romId: str
    provider: str
    key: str
    category: str
    valueType: str
    recommendedRestoreClass: str
    confidence: str
    reviewRequired: bool
    reason: str
    sourceFile: str
    sourceLine: int
    evidence: str


def read_catalog(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ValueError("Unsupported ROM source catalog schemaVersion")
    if not isinstance(data.get("roms"), list):
        raise ValueError("ROM source catalog must contain a roms array")
    ids = [item.get("id") for item in data["roms"]]
    if any(not isinstance(item, str) or not item for item in ids) or len(ids) != len(set(ids)):
        raise ValueError("ROM ids must be unique non-empty strings")
    return data


def iter_source_files(root: Path) -> Iterable[Path]:
    for path in root.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in TEXT_SUFFIXES:
            continue
        if any(part in IGNORED_PARTS for part in path.parts):
            continue
        try:
            if path.stat().st_size <= 4 * 1024 * 1024:
                yield path
        except OSError:
            continue


def unquote(value: str, constants: dict[str, str]) -> tuple[str | None, str]:
    value = value.strip()
    if value.startswith('"') and value.endswith('"'):
        try:
            return json.loads(value), "literal"
        except json.JSONDecodeError:
            return value[1:-1], "literal"
    short = value.rsplit(".", 1)[-1]
    return constants.get(short), "resolved_constant" if short in constants else "unresolved_constant"


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def normalize_provider(owner: str, provider: str) -> str:
    prefix = "lineage" if owner.startswith("Lineage") else ""
    return prefix + provider[0].upper() + provider[1:] if prefix else provider.lower()


def category_for(key: str, source_file: str) -> str:
    value = f"{key} {source_file}".lower()
    groups = [
        ("quick_settings", ("qs_", "quick_settings", "quicksettings", "tile_")),
        ("status_bar", ("status_bar", "statusbar", "clock", "battery_style", "network_traffic")),
        ("lockscreen", ("lockscreen", "lock_screen", "keyguard", "udfps", "ambient_media")),
        ("aod_ambient", ("aod", "doze", "ambient", "edge_light", "pulse")),
        ("notifications", ("notification", "heads_up", "bubble", "ticker")),
        ("sound_haptics", ("sound", "audio", "volume", "ringer", "vibrat", "haptic", "dirac")),
        ("display", ("display", "brightness", "refresh", "night_mode", "font_scale", "dimming", "hbm")),
        ("battery_charging", ("battery", "charging", "charge", "power_save")),
        ("accessibility", ("accessibility", "caption", "magnification")),
        ("privacy_security", ("privacy", "permission", "secure", "vpn", "dns", "applock", "biometric")),
        ("network", ("wifi", "bluetooth", "mobile_data", "network", "hotspot", "nfc")),
        ("input_gestures", ("gesture", "button", "navigation", "touch", "keyboard", "ime")),
        ("launcher", ("launcher", "workspace", "desktop", "drawer", "recents")),
        ("game_space", ("gamespace", "game_space", "game_mode", "gamebar", "game_bar")),
        ("themes", ("theme", "overlay", "monet", "icon_pack", "wallpaper")),
        ("hardware", ("parts", "sysfs", "/sys/", "thermal", "saturation")),
    ]
    for category, needles in groups:
        if any(needle in value for needle in needles):
            return category
    return "other"


def classify(provider: str, key: str, source_file: str, rom_id: str) -> tuple[str, str, str]:
    value = f"{provider} {key} {source_file}".lower()
    never = (
        "gatekeeper", "weaver", "lockscreen.password", "lock_pattern", "biometric_template",
        "keystore", "strongbox", "passkey", "frp", "factory_reset_protection",
        "payment_credential", "drm_key", "esim", "pairing_key",
    )
    if any(item in value for item in never):
        return "never", "high", "security_or_hardware_bound_secret"
    if provider in {"systemProperty", "sysfs"} or any(
        item in value for item in ("device/", "device\\", "parts", "/sys/", "hbm", "dc_dimming", "touch_game")
    ):
        return "exactDevice", "medium", "hardware_or_device_service_dependency"
    if provider in {"sharedPreference", "deviceConfig"}:
        return "archiveOnly", "medium", "no_stable_cross_rom_provider_contract"
    if any(item in value for item in ("launcher", "systemui", "gamespace", "evolver", "matrixxlab")):
        return "exactRom", "medium", "component_schema_or_implementation_specific"
    if provider in {"system", "secure", "global", "lineageSystem", "lineageSecure", "lineageGlobal"}:
        if rom_id in {"aosp", "lineageos"}:
            return "portableCandidate", "medium", "typed_validator_and_capability_probe_required"
        return "exactRom", "medium", "same_key_may_have_different_rom_semantics"
    return "archiveOnly", "low", "unclassified_apply_contract"


def value_type_from_evidence(evidence: str) -> str:
    lowered = evidence.lower()
    for kind in ("boolean", "float", "long", "int", "string"):
        if kind in lowered:
            return "integer" if kind in {"long", "int"} else kind
    return "unknown"


def make_finding(
    spec: SourceSpec,
    path: Path,
    text: str,
    offset: int,
    provider: str,
    key: str,
    evidence: str,
    confidence_hint: str,
) -> Finding | None:
    key = key.strip()
    if not key or key.startswith("@") or len(key) > 512 or "\x00" in key:
        return None
    relative = path.relative_to(spec.root).as_posix()
    restore_class, confidence, reason = classify(provider, key, relative, spec.rom_id)
    if confidence_hint == "low":
        confidence = "low"
    digest = hashlib.sha256(f"{provider}\0{key}".encode()).hexdigest()[:16]
    return Finding(
        stableId=f"discovered:{provider}:{key}:{digest}",
        romId=spec.rom_id,
        provider=provider,
        key=key,
        category=category_for(key, relative),
        valueType=value_type_from_evidence(evidence),
        recommendedRestoreClass=restore_class,
        confidence=confidence,
        reviewRequired=True,
        reason=reason,
        sourceFile=relative,
        sourceLine=line_number(text, offset),
        evidence=evidence[:240].strip(),
    )


def collect_indexes(files: list[Path]) -> tuple[dict[str, str], dict[str, str]]:
    constants: dict[str, str] = {}
    resources: dict[str, str] = {}
    for path in files:
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for match in STRING_CONSTANT.finditer(text):
            constants.setdefault(
                match.group("name"),
                bytes(match.group("value"), "utf-8").decode("unicode_escape"))
        if path.suffix.lower() == ".xml" and "values" in path.parts:
            for match in RESOURCE_STRING.finditer(text):
                value = re.sub(r"<[^>]+>", "", match.group("value")).strip()
                if value and not value.startswith("@"):
                    resources.setdefault(match.group("name"), html.unescape(value))
    return constants, resources


def scan_file(
    spec: SourceSpec,
    path: Path,
    global_constants: dict[str, str] | None = None,
    resource_strings: dict[str, str] | None = None,
) -> list[Finding]:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    constants = dict(global_constants or {})
    constants.update({
        match.group("name"): bytes(match.group("value"), "utf-8").decode("unicode_escape")
        for match in STRING_CONSTANT.finditer(text)
    })
    findings: list[Finding] = []
    for match in PROVIDER_CALL.finditer(text):
        key, resolution = unquote(match.group("key"), constants)
        if key:
            finding = make_finding(
                spec, path, text, match.start(),
                normalize_provider(match.group("owner"), match.group("provider")),
                key, match.group(0), "low" if resolution == "unresolved_constant" else "medium")
            if finding:
                findings.append(finding)
    for match in SHARED_PREF_CALL.finditer(text):
        key, resolution = unquote(match.group("key"), constants)
        if key:
            finding = make_finding(
                spec, path, text, match.start(), "sharedPreference", key,
                match.group(0), "low" if resolution == "unresolved_constant" else "medium")
            if finding:
                findings.append(finding)
    for match in SYSTEM_PROPERTY_CALL.finditer(text):
        key, _ = unquote(match.group("key"), constants)
        if key:
            finding = make_finding(
                spec, path, text, match.start(), "systemProperty", key,
                match.group(0), "medium")
            if finding:
                findings.append(finding)
    if path.suffix.lower() == ".xml":
        for match in XML_KEY.finditer(text):
            key = match.group("key")
            if key.startswith("@string/"):
                key = (resource_strings or {}).get(key.removeprefix("@string/"), "")
                if not key:
                    continue
            finding = make_finding(
                spec, path, text, match.start(), "sharedPreference", key,
                match.group(0), "medium")
            if finding:
                findings.append(finding)
    return findings


def scan_sources(catalog: dict, specs: list[SourceSpec]) -> dict:
    catalog_ids = {item["id"] for item in catalog["roms"]}
    findings: list[Finding] = []
    source_summaries = []
    for spec in specs:
        if spec.rom_id not in catalog_ids:
            raise ValueError(f"Unknown rom id: {spec.rom_id}")
        if not spec.root.is_dir():
            raise ValueError(f"Source root does not exist: {spec.root}")
        files = list(iter_source_files(spec.root))
        global_constants, resource_strings = collect_indexes(files)
        for path in files:
            findings.extend(scan_file(spec, path, global_constants, resource_strings))
        metadata_path = spec.root / ".vexark-source.json"
        metadata = {}
        if metadata_path.is_file():
            try:
                metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                metadata = {"metadataError": "invalid_vexark_source_metadata"}
        source_summaries.append({
            "romId": spec.rom_id,
            "root": str(spec.root),
            "filesScanned": len(files),
            **metadata,
        })

    unique: dict[tuple[str, str, str, str, int], Finding] = {}
    for finding in findings:
        key = (finding.romId, finding.provider, finding.key, finding.sourceFile, finding.sourceLine)
        unique[key] = finding
    ordered = sorted(unique.values(), key=lambda item: (
        item.romId, item.category, item.provider, item.key, item.sourceFile, item.sourceLine))
    counts: dict[str, int] = {}
    for item in ordered:
        counts[item.recommendedRestoreClass] = counts.get(item.recommendedRestoreClass, 0) + 1
    return {
        "schemaVersion": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "device": catalog.get("device"),
        "sources": source_summaries,
        "summary": {"findingCount": len(ordered), "byRestoreClass": counts},
        "findings": [asdict(item) for item in ordered],
    }


def run_git(arguments: list[str], cwd: Path | None = None) -> str:
    result = subprocess.run(
        ["git", *arguments], cwd=cwd, text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip() or "git failed")
    return result.stdout.strip()


def ensure_workspace(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    marker = path / ".vexark-rom-source-workspace"
    if not marker.exists():
        if any(path.iterdir()):
            raise ValueError("Work directory is not empty and has no VeXArk workspace marker")
        marker.write_text("VeXArk ROM source cache v1\n", encoding="utf-8")


def sync_repositories(catalog: dict, work_dir: Path, rom_ids: set[str]) -> list[SourceSpec]:
    ensure_workspace(work_dir)
    specs: list[SourceSpec] = []
    for rom in catalog["roms"]:
        if rom_ids and rom["id"] not in rom_ids:
            continue
        for index, repository in enumerate(rom.get("repositories", [])):
            url = repository["url"]
            revision = repository["revision"]
            destination = work_dir / rom["id"] / f"{index:02d}-{repository['role']}"
            destination.parent.mkdir(parents=True, exist_ok=True)
            if (destination / ".git").is_dir():
                run_git(["fetch", "--depth", "1", "origin", revision], destination)
                run_git(["checkout", "--detach", "FETCH_HEAD"], destination)
            elif destination.exists():
                raise ValueError(f"Refusing to overwrite non-git directory: {destination}")
            else:
                run_git([
                    "clone", "--depth", "1", "--filter=blob:none", "--branch", revision,
                    url, str(destination),
                ])
            commit = run_git(["rev-parse", "HEAD"], destination)
            (destination / ".vexark-source.json").write_text(json.dumps({
                "romId": rom["id"], "role": repository["role"], "url": url,
                "revision": revision, "commit": commit,
            }, indent=2), encoding="utf-8")
            specs.append(SourceSpec(rom["id"], destination.resolve()))
    return specs


def parse_source(value: str) -> SourceSpec:
    if "=" not in value:
        raise argparse.ArgumentTypeError("source must be ROM_ID=PATH")
    rom_id, path = value.split("=", 1)
    return SourceSpec(rom_id.strip(), Path(path).expanduser().resolve())


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    verify = sub.add_parser("verify", help="validate source catalog")
    verify.add_argument("--catalog", type=Path, required=True)

    scan = sub.add_parser("scan", help="scan one or more local source trees")
    scan.add_argument("--catalog", type=Path, required=True)
    scan.add_argument("--source", action="append", type=parse_source, required=True)
    scan.add_argument("--output", type=Path, required=True)

    sync = sub.add_parser("sync-scan", help="shallow-clone catalog repositories and scan them")
    sync.add_argument("--catalog", type=Path, required=True)
    sync.add_argument("--work-dir", type=Path, required=True)
    sync.add_argument("--rom", action="append", default=[])
    sync.add_argument("--output", type=Path, required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        catalog = read_catalog(args.catalog.resolve())
        if args.command == "verify":
            print(f"OK: {len(catalog['roms'])} ROM families")
            return 0
        if args.command == "scan":
            report = scan_sources(catalog, args.source)
        else:
            specs = sync_repositories(catalog, args.work_dir.resolve(), set(args.rom))
            if not specs:
                raise ValueError("No repositories selected for scanning")
            report = scan_sources(catalog, specs)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Wrote {report['summary']['findingCount']} findings to {args.output}")
        return 0
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
