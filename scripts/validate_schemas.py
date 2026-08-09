#!/usr/bin/env python3
"""Validate every checked-in VeXArk JSON schema against its declared draft."""

from __future__ import annotations

import json
from pathlib import Path

from jsonschema.validators import validator_for


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    schemas = sorted((root / "protocol").rglob("*.json"))
    if not schemas:
        raise SystemExit("No protocol schemas found")
    for path in schemas:
        with path.open("r", encoding="utf-8") as stream:
            schema = json.load(stream)
        validator_for(schema).check_schema(schema)
        print(f"valid: {path.relative_to(root)}")


if __name__ == "__main__":
    main()
