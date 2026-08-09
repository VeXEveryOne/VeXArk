import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("rom_catalog", ROOT / "scripts" / "rom_catalog.py")
rom_catalog = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = rom_catalog
SPEC.loader.exec_module(rom_catalog)


class RomCatalogTests(unittest.TestCase):
    def test_catalog_is_valid_and_discovery_only(self):
        data = rom_catalog.read_catalog(ROOT / "catalog" / "rom-sources" / "xiaomi-13-fuxi.json")
        self.assertTrue(data["policy"]["discoveryOnly"])
        self.assertGreaterEqual(len(data["roms"]), 10)

    def test_scanner_finds_provider_xml_and_hardware_settings_conservatively(self):
        catalog = rom_catalog.read_catalog(
            ROOT / "catalog" / "rom-sources" / "xiaomi-13-fuxi.json")
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            (source / "Settings.kt").write_text(
                'Settings.Secure.putInt(resolver, "qs_tiles_rows", 4)\n'
                'SystemProperties.set("persist.vendor.display.hbm", "1")\n',
                encoding="utf-8")
            (source / "prefs.xml").write_text(
                '<Preference android:key="lockscreen_weather_enabled"/>', encoding="utf-8")
            values = source / "res" / "values"
            values.mkdir(parents=True)
            (values / "strings.xml").write_text(
                '<resources><string name="pref_qs_key">qs_show_brightness_slider</string></resources>',
                encoding="utf-8")
            (source / "resolved.xml").write_text(
                '<Preference android:key="@string/pref_qs_key"/>', encoding="utf-8")
            report = rom_catalog.scan_sources(
                catalog, [rom_catalog.SourceSpec("crdroid", source)])

        by_key = {item["key"]: item for item in report["findings"]}
        self.assertEqual("exactRom", by_key["qs_tiles_rows"]["recommendedRestoreClass"])
        self.assertEqual("exactDevice", by_key["persist.vendor.display.hbm"]["recommendedRestoreClass"])
        self.assertEqual("lockscreen", by_key["lockscreen_weather_enabled"]["category"])
        self.assertEqual("quick_settings", by_key["qs_show_brightness_slider"]["category"])
        self.assertTrue(all(item["reviewRequired"] for item in report["findings"]))

    def test_never_keys_are_not_promoted(self):
        catalog = rom_catalog.read_catalog(
            ROOT / "catalog" / "rom-sources" / "xiaomi-13-fuxi.json")
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            (source / "Security.java").write_text(
                'Settings.Secure.putString(r, "gatekeeper.password_handle", value);',
                encoding="utf-8")
            report = rom_catalog.scan_sources(
                catalog, [rom_catalog.SourceSpec("lineageos", source)])
        self.assertEqual("never", report["findings"][0]["recommendedRestoreClass"])


if __name__ == "__main__":
    unittest.main()
