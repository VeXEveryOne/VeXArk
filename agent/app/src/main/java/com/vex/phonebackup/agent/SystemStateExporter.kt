package com.vex.phonebackup.agent

import android.app.role.RoleManager
import android.content.Context
import android.net.Uri
import android.provider.Settings
import com.topjohnwu.superuser.Shell
import org.json.JSONArray
import org.json.JSONObject

object SystemStateExporter {
    private val packagePattern = Regex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$")
    private val neverInventoryPatterns = listOf(
        Regex("(?i).*password.*"), Regex("(?i).*credential.*"), Regex("(?i).*biometric.*"),
        Regex("(?i).*fingerprint.*"), Regex("(?i).*gatekeeper.*"), Regex("(?i).*weaver.*"),
        Regex("(?i).*keystore.*"), Regex("(?i)^android_id$"), Regex("(?i)^bluetooth_address$"),
        Regex("(?i)^adb_.*"), Regex("(?i).*frp.*"), Regex("(?i).*device_provisioned.*")
    )

    fun export(context: Context): JSONObject {
        val identity = RomIdentityCollector.collect(context)
        val activeRules = RomSettingsCatalog.activeRules(identity)
        val allValues = readAllNamespaces(context)
        val output = JSONObject()
            .put("schemaVersion", 2)
            .put("romIdentity", identity)

        SettingSource.entries.forEach { source ->
            if (source == SettingSource.PACKAGE_DATA) return@forEach
            val section = JSONObject()
            activeRules.filter { it.source == source }.sortedBy { it.key }.forEach { rule ->
                val value = if (source == SettingSource.SERVICE)
                    readServiceValue(rule.key) else allValues[source]?.get(rule.key)
                section.put(rule.key, value ?: JSONObject.NULL)
            }
            output.put(source.wireName, section)
        }

        val registered = activeRules.groupBy { it.source }
            .mapValues { (_, rules) -> rules.map { it.key }.toSet() }
        val unknown = JSONObject()
        val coverage = RomSettingsCatalog.coverage(identity)
        allValues.forEach { (source, values) ->
            val unregistered = values.keys.asSequence()
                .filterNot { key -> registered[source].orEmpty().contains(key) }
                .sorted()
                .toList()
            unregistered.forEach { key ->
                val sensitive = isSensitiveInventoryKey(key)
                coverage.put(JSONObject()
                    .put("id", "${source.wireName}:$key")
                    .put("displayName", key)
                    .put("adapter", "unknown-inventory")
                    .put("source", source.wireName)
                    .put("valueType", "string")
                    .put("restoreClass", if (sensitive) "never" else "archive_only")
                    .put("requiresConfirmation", false)
                    .put("capability", JSONObject.NULL)
                    .put("status", if (sensitive) "ignored" else "archived")
                    .put("reason", if (sensitive) "sensitive_state" else "unknown_provider_key"))
            }
            val keys = unregistered.filterNot(::isSensitiveInventoryKey)
            unknown.put(source.wireName, JSONArray(keys))
        }
        output.put("coverage", coverage)
        output.put("unknownKeys", unknown)
        output.put("roles", exportRoles(context))
        return output
    }

    fun restore(
        context: Context,
        state: JSONObject,
        confirmedSettingIds: Set<String> = emptySet()
    ): JSONObject {
        val currentIdentity = RomIdentityCollector.collect(context)
        val sourceIdentity = state.optJSONObject("romIdentity")
        val exactRom = RomIdentityCollector.isExactRomMatch(sourceIdentity, currentIdentity)
        val exactDevice = exactRom &&
            sourceIdentity?.optString("device") == currentIdentity.optString("device")
        val capabilities = jsonStrings(currentIdentity.optJSONArray("capabilities"))
        val rules = RomSettingsCatalog.activeRules(currentIdentity)
        val outcomes = JSONArray()
        val failures = JSONArray()
        val needsConfirmation = JSONArray()

        rules.filter { it.source != SettingSource.PACKAGE_DATA }
            .forEach { rule ->
                val section = state.optJSONObject(rule.source.wireName) ?: return@forEach
                if (!section.has(rule.key) || section.isNull(rule.key)) return@forEach
                val requested = section.optString(rule.key)
                if (rule.capability != null && rule.capability !in capabilities) {
                    outcomes.put(outcome(rule, "skipped", "capability_unavailable"))
                    return@forEach
                }
                val classAllowed = when (rule.restoreClass) {
                    SettingRestoreClass.PORTABLE -> true
                    SettingRestoreClass.EXACT_ROM -> exactRom
                    SettingRestoreClass.EXACT_DEVICE -> exactDevice
                    SettingRestoreClass.ARCHIVE_ONLY, SettingRestoreClass.NEVER -> false
                }
                if (!classAllowed) {
                    outcomes.put(outcome(rule, "skipped", "compatibility_blocked"))
                    return@forEach
                }
                val normalized = rule.validate(requested)?.let {
                    adaptToTarget(context, rule, it)
                }
                if (normalized == null) {
                    failures.put(rule.id)
                    outcomes.put(outcome(rule, "failed", "validation_failed"))
                    return@forEach
                }
                if ((rule.requiresConfirmation ||
                        rule.restoreClass != SettingRestoreClass.PORTABLE) &&
                    rule.id !in confirmedSettingIds
                ) {
                    needsConfirmation.put(rule.id)
                    outcomes.put(outcome(rule, "needs_user", "confirmation_required"))
                    return@forEach
                }

                val previous = readValue(context, rule.source, rule.key)
                val applied = writeValue(context, rule.source, rule.key, normalized)
                val readBack = if (applied) readValue(context, rule.source, rule.key) else null
                if (applied && equivalent(normalized, readBack, rule.kind)) {
                    outcomes.put(outcome(rule, "succeeded", "readback_verified"))
                } else {
                    rollback(context, rule.source, rule.key, previous)
                    failures.put(rule.id)
                    outcomes.put(outcome(rule, "rolled_back", "apply_or_readback_failed"))
                }
            }

        restoreRoles(context, state.optJSONObject("roles"), confirmedSettingIds,
            outcomes, needsConfirmation, failures)
        return JSONObject()
            .put("ok", failures.length() == 0)
            .put("failures", failures)
            .put("needsConfirmation", needsConfirmation)
            .put("outcomes", outcomes)
    }

    private fun readAllNamespaces(context: Context): Map<SettingSource, Map<String, String>> = mapOf(
        SettingSource.SYSTEM to shellSettings("system"),
        SettingSource.SECURE to shellSettings("secure"),
        SettingSource.GLOBAL to shellSettings("global"),
        SettingSource.LINEAGE_SYSTEM to lineageSettings(context, "system"),
        SettingSource.LINEAGE_SECURE to lineageSettings(context, "secure"),
        SettingSource.LINEAGE_GLOBAL to lineageSettings(context, "global")
    )

    private fun shellSettings(namespace: String): Map<String, String> {
        val result = Shell.cmd("settings list $namespace").exec()
        if (!result.isSuccess) return emptyMap()
        return buildMap {
            result.out.forEach { line ->
                val separator = line.indexOf('=')
                if (separator > 0) {
                    val value = line.substring(separator + 1)
                    if (!value.equals("null", ignoreCase = true))
                        put(line.substring(0, separator), value)
                }
            }
        }
    }

    private fun lineageSettings(context: Context, namespace: String): Map<String, String> =
        runCatching {
            val uri = Uri.parse("content://lineagesettings/$namespace")
            context.contentResolver.query(uri, arrayOf("name", "value"), null, null, null)?.use {
                cursor ->
                val nameColumn = cursor.getColumnIndex("name")
                val valueColumn = cursor.getColumnIndex("value")
                buildMap {
                    while (cursor.moveToNext()) {
                        val name = cursor.getString(nameColumn)
                        val value = cursor.getString(valueColumn)
                        if (!name.isNullOrBlank() && value != null) put(name, value)
                    }
                }
            }.orEmpty()
        }.getOrDefault(emptyMap())

    private fun readValue(context: Context, source: SettingSource, key: String): String? =
        when (source) {
            SettingSource.SYSTEM -> Settings.System.getString(context.contentResolver, key)
            SettingSource.SECURE -> Settings.Secure.getString(context.contentResolver, key)
            SettingSource.GLOBAL -> Settings.Global.getString(context.contentResolver, key)
            SettingSource.LINEAGE_SYSTEM -> lineageSettings(context, "system")[key]
            SettingSource.LINEAGE_SECURE -> lineageSettings(context, "secure")[key]
            SettingSource.LINEAGE_GLOBAL -> lineageSettings(context, "global")[key]
            SettingSource.SERVICE -> readServiceValue(key)
            SettingSource.PACKAGE_DATA -> null
        }

    private fun writeValue(
        context: Context,
        source: SettingSource,
        key: String,
        value: String
    ): Boolean = when (source) {
        SettingSource.SYSTEM, SettingSource.SECURE, SettingSource.GLOBAL ->
            Shell.cmd("settings put ${source.wireName} ${quote(key)} ${quote(value)}").exec().isSuccess
        SettingSource.LINEAGE_SYSTEM, SettingSource.LINEAGE_SECURE,
        SettingSource.LINEAGE_GLOBAL -> {
            val namespace = source.wireName.removePrefix("lineage").lowercase()
            Shell.cmd(
                "content insert --user 0 --uri content://lineagesettings/$namespace " +
                    "--bind name:s:${quoteBinding(key)} --bind value:s:${quoteBinding(value)}"
            ).exec().isSuccess
        }
        SettingSource.SERVICE -> when (key) {
            "system_locale" -> Shell.cmd("cmd locale set-device-locale ${quote(value)}")
                .exec().isSuccess
            "theme_overlays" -> writeThemeOverlays(value)
            else -> false
        }
        else -> false
    }

    private fun rollback(context: Context, source: SettingSource, key: String, previous: String?) {
        if (previous != null) {
            writeValue(context, source, key, previous)
            return
        }
        when (source) {
            SettingSource.SYSTEM, SettingSource.SECURE, SettingSource.GLOBAL ->
                Shell.cmd("settings delete ${source.wireName} ${quote(key)}").exec()
            SettingSource.LINEAGE_SYSTEM, SettingSource.LINEAGE_SECURE,
            SettingSource.LINEAGE_GLOBAL -> {
                val namespace = source.wireName.removePrefix("lineage").lowercase()
                Shell.cmd(
                    "content delete --user 0 --uri content://lineagesettings/$namespace " +
                        "--where ${quote("name='$key'")}"
                ).exec()
            }
            SettingSource.SERVICE, SettingSource.PACKAGE_DATA -> Unit
        }
    }

    private fun equivalent(expected: String, actual: String?, kind: SettingValueKind): Boolean {
        if (actual == null) return false
        return when (kind) {
            SettingValueKind.FLOAT -> expected.toDoubleOrNull()?.let { left ->
                actual.toDoubleOrNull()?.let { right -> kotlin.math.abs(left - right) < 0.0001 }
            } == true
            else -> expected == actual
        }
    }

    private fun adaptToTarget(context: Context, rule: SettingRule, value: String): String? {
        if (rule.id == "lineageSystem:display_color_adjustment") {
            val channels = value.trim().split(Regex("\\s+")).mapNotNull(String::toDoubleOrNull)
            if (channels.size != 3 || channels.any { !it.isFinite() || it !in 0.0..1.0 })
                return null
            return channels.joinToString(" ")
        }
        if (rule.id == "service:system_locale") {
            if (!Regex("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$").matches(value)) return null
            val supported = Shell.cmd("cmd locale list-device-locales").exec().out
                .flatMap { line -> line.split(',').map(String::trim) }
                .filter(String::isNotBlank)
            if (supported.isNotEmpty() && value !in supported) return null
        }
        if (rule.id == "service:theme_overlays") {
            val requested = value.split(',').map(String::trim).filter(String::isNotBlank)
            val available = overlayState().second
            if (requested.any { it !in available || !isPortableThemeOverlay(it) }) return null
            return requested.distinct().sorted().joinToString(",")
        }
        if (rule.id == "system:peak_refresh_rate") {
            val rates = Shell.cmd("dumpsys display").exec().out
                .flatMap { line -> Regex("refreshRate=([0-9]+(?:\\.[0-9]+)?)")
                    .findAll(line).mapNotNull { it.groupValues[1].toDoubleOrNull() }.toList() }
                .distinct()
            val requested = value.toDoubleOrNull() ?: return null
            if (rates.isNotEmpty() && rates.none { kotlin.math.abs(it - requested) < 0.1 })
                return null
        }
        if (rule.id == "secure:sysui_qs_tiles") {
            return value.split(',').map(String::trim).filter { tile ->
                val match = Regex("^custom\\(([A-Za-z0-9_.]+)/").find(tile)
                match == null || runCatching {
                    context.packageManager.getPackageInfo(match.groupValues[1], 0)
                }.isSuccess
            }.joinToString(",").takeIf(String::isNotBlank)
        }
        return value
    }

    private fun readServiceValue(key: String): String? = when (key) {
        "system_locale" -> Shell.cmd("cmd locale get-device-locale").exec()
            .takeIf { it.isSuccess }?.out?.firstOrNull()?.trim()?.takeIf(String::isNotBlank)
        "theme_overlays" -> overlayState().first
            .filter(::isPortableThemeOverlay).sorted().joinToString(",")
        else -> null
    }

    private fun writeThemeOverlays(value: String): Boolean {
        val desired = value.split(',').map(String::trim).filter(String::isNotBlank).toSet()
        val (enabled, available) = overlayState()
        if (desired.any { it !in available || !isPortableThemeOverlay(it) }) return false
        val current = enabled.filter(::isPortableThemeOverlay).toSet()
        var success = true
        (current - desired).sorted().forEach { overlay ->
            success = Shell.cmd("cmd overlay disable --user 0 ${quote(overlay)}")
                .exec().isSuccess && success
        }
        (desired - current).sorted().forEach { overlay ->
            success = Shell.cmd("cmd overlay enable --user 0 ${quote(overlay)}")
                .exec().isSuccess && success
        }
        return success
    }

    private fun overlayState(): Pair<Set<String>, Set<String>> {
        val result = Shell.cmd("cmd overlay list --user 0").exec()
        if (!result.isSuccess) return emptySet<String>() to emptySet()
        val pattern = Regex("^\\[(x| )]\\s+([A-Za-z0-9_.]+(?::[A-Za-z0-9_.-]+)?)$")
        val enabled = mutableSetOf<String>()
        val available = mutableSetOf<String>()
        result.out.forEach { line ->
            val match = pattern.matchEntire(line.trim()) ?: return@forEach
            val overlay = match.groupValues[2]
            available += overlay
            if (match.groupValues[1] == "x") enabled += overlay
        }
        return enabled to available
    }

    private fun isPortableThemeOverlay(name: String) = listOf(
        "com.android.theme.", "com.android.system.theme.",
        "com.android.systemui.clocks.", "com.android.internal.systemui.navbar."
    ).any(name::startsWith)

    private fun exportRoles(context: Context): JSONObject {
        val manager = context.getSystemService(RoleManager::class.java)
        val roles = listOf(
            RoleManager.ROLE_BROWSER, RoleManager.ROLE_DIALER, RoleManager.ROLE_SMS,
            RoleManager.ROLE_HOME, RoleManager.ROLE_ASSISTANT
        )
        return JSONObject().apply {
            roles.forEach { role ->
                if (runCatching { manager.isRoleAvailable(role) }.getOrDefault(false)) {
                    put(role, JSONArray(roleHolders(role).sorted()))
                }
            }
        }
    }

    private fun restoreRoles(
        context: Context,
        roles: JSONObject?,
        confirmed: Set<String>,
        outcomes: JSONArray,
        needsConfirmation: JSONArray,
        failures: JSONArray
    ) {
        if (roles == null) return
        val roleManager = context.getSystemService(RoleManager::class.java)
        roles.keys().forEach { role ->
            val id = "role:$role"
            val holder = roles.optJSONArray(role)?.optString(0).orEmpty()
            if (!roleManager.isRoleAvailable(role) || !packagePattern.matches(holder)) return@forEach
            if (id !in confirmed) {
                needsConfirmation.put(id)
                outcomes.put(JSONObject().put("id", id).put("status", "needs_user")
                    .put("reason", "confirmation_required"))
                return@forEach
            }
            val installed = runCatching { context.packageManager.getPackageInfo(holder, 0) }.isSuccess
            val previous = roleHolders(role)
            val applied = installed && Shell.cmd(
                "cmd role add-role-holder --user 0 ${quote(role)} ${quote(holder)}"
            ).exec().isSuccess
            val readBack = roleHolders(role)
            val success = applied && holder in readBack
            if (!success) {
                if (holder !in previous) Shell.cmd(
                    "cmd role remove-role-holder --user 0 ${quote(role)} ${quote(holder)}"
                ).exec()
                previous.forEach { oldHolder ->
                    Shell.cmd(
                        "cmd role add-role-holder --user 0 ${quote(role)} ${quote(oldHolder)}"
                    ).exec()
                }
                failures.put(id)
            }
            outcomes.put(JSONObject().put("id", id)
                .put("status", if (success) "succeeded" else "rolled_back")
                .put("reason", if (success) "readback_verified" else "apply_or_readback_failed"))
        }
    }

    private fun roleHolders(role: String): Set<String> {
        val result = Shell.cmd(
            "cmd role get-role-holders --user 0 ${quote(role)}"
        ).exec()
        if (!result.isSuccess) return emptySet()
        return result.out.map(String::trim).filter(packagePattern::matches).toSet()
    }

    private fun outcome(rule: SettingRule, status: String, reason: String) = JSONObject()
        .put("id", rule.id).put("adapter", rule.adapter).put("status", status).put("reason", reason)

    private fun jsonStrings(array: JSONArray?): Set<String> = buildSet {
        if (array != null) for (index in 0 until array.length()) add(array.optString(index))
    }

    private fun isSensitiveInventoryKey(key: String) =
        neverInventoryPatterns.any { it.matches(key) }

    private fun quote(value: String): String = "'${value.replace("'", "'\\''")}'"
    private fun quoteBinding(value: String): String = value.replace("\\", "\\\\")
        .replace(":", "\\:").replace(" ", "\\ ")
}
