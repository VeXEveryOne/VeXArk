package com.vex.phonebackup.agent

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RomSettingsCatalogTest {
    @Test
    fun `animation scale accepts Android floating point values`() {
        val identity = identity("aosp-settings")
        val rule = RomSettingsCatalog.activeRules(identity)
            .single { it.id == "global:animator_duration_scale" }

        assertEquals("1.0", rule.validate("1.0"))
        assertNull(rule.validate("11.0"))
        assertEquals(SettingRestoreClass.ARCHIVE_ONLY, rule.restoreClass)
    }

    @Test
    fun `quick settings validates built in and custom tiles`() {
        val identity = identity("aosp-settings")
        val rule = RomSettingsCatalog.activeRules(identity)
            .single { it.id == "secure:sysui_qs_tiles" }

        assertEquals(
            "internet,bt,custom(com.example.tiles/.MyTile)",
            rule.validate("internet,bt,custom(com.example.tiles/.MyTile)")
        )
        assertNull(rule.validate("internet,custom(bad package/.Tile)"))
    }

    @Test
    fun `package data stays archived until a semantic adapter exists`() {
        val identity = identity("aosp-settings", "launcher3")
        val coverage = RomSettingsCatalog.coverage(identity)
        val launcher = (0 until coverage.length())
            .map { coverage.getJSONObject(it) }
            .first { it.getString("id") == "packageData:pref_icon_pack" }

        assertEquals("archived", launcher.getString("status"))
        assertEquals("semantic_adapter_not_implemented", launcher.getString("reason"))
    }

    @Test
    fun `semantic service adapters are supported with conservative classes`() {
        val coverage = RomSettingsCatalog.coverage(identity("aosp-settings", "theme-overlays"))
        val entries = (0 until coverage.length()).map { coverage.getJSONObject(it) }
        assertEquals("supported", entries.first {
            it.getString("id") == "service:system_locale"
        }.getString("status"))
        val theme = entries.first { it.getString("id") == "service:theme_overlays" }
        assertEquals("supported", theme.getString("status"))
        assertEquals("exact_rom", theme.getString("restoreClass"))
    }

    @Test
    fun `every active rule has a unique stable id`() {
        val rules = RomSettingsCatalog.activeRules(identity(
            "aosp-settings", "lineage-settings", "crdroid-settings", "launcher3",
            "gamespace", "theme-overlays", "xiaomi-dada-parts"
        ))
        assertEquals(rules.size, rules.map { it.id }.distinct().size)
        assertTrue(rules.all { it.id.contains(':') })
    }

    private fun identity(vararg capabilities: String) = JSONObject()
        .put("sdk", 36)
        .put("device", "dada")
        .put("crDroidBuildVersion", "12.11")
        .put("capabilities", JSONArray(capabilities.toList()))
}
