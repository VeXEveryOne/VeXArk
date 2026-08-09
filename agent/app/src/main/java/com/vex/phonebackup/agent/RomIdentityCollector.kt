package com.vex.phonebackup.agent

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

object RomIdentityCollector {
    private val identityPackages = listOf(
        "com.android.settings",
        "com.android.systemui",
        "com.android.launcher3",
        "org.lineageos.lineagesettings",
        "org.lineageos.lineageparts",
        "org.lineageos.settings",
        "io.chaldeaprjkt.gamespace",
        "com.android.axion.axthemestore"
    )

    fun collect(context: Context): JSONObject {
        val crDroidVersion = property("ro.crdroid.version")
        val crDroidBuild = property("ro.crdroid.build.version")
        val lineageDevice = property("ro.lineage.device")
        val packageSignatures = JSONObject()
        val packageVersions = JSONObject()
        identityPackages.forEach { packageName ->
            packageIdentity(context, packageName)?.let { identity ->
                packageSignatures.put(packageName, identity.first)
                packageVersions.put(packageName, identity.second)
            }
        }
        val schemas = JSONObject()
        property("sys.lineage_settings_system_version").toIntOrNull()?.let {
            schemas.put("lineage.system", it)
        }
        property("sys.lineage_settings_secure_version").toIntOrNull()?.let {
            schemas.put("lineage.secure", it)
        }
        property("sys.lineage_settings_global_version").toIntOrNull()?.let {
            schemas.put("lineage.global", it)
        }
        val capabilities = buildList {
            add("aosp-settings")
            if (crDroidVersion.isNotBlank()) add("crdroid-settings")
            if (lineageDevice.isNotBlank() || hasPackage(context, "org.lineageos.lineagesettings")) {
                add("lineage-settings")
            }
            if (hasPackage(context, "com.android.launcher3")) add("launcher3")
            if (hasPackage(context, "io.chaldeaprjkt.gamespace")) add("gamespace")
            if (hasPackage(context, "com.android.axion.axthemestore")) add("theme-overlays")
            if (Build.DEVICE == "dada" && hasPackage(context, "org.lineageos.settings")) {
                add("xiaomi-dada-parts")
            }
            if (serviceAvailable("lineagehealth")) add("lineage-health")
            if (serviceAvailable("lineagelivedisplay")) add("lineage-livedisplay")
        }
        return JSONObject()
            .put("sdk", Build.VERSION.SDK_INT)
            .put("device", Build.DEVICE)
            .put("buildIncremental", Build.VERSION.INCREMENTAL)
            .put("fingerprint", Build.FINGERPRINT)
            .put("crDroidVersion", crDroidVersion.ifBlank { JSONObject.NULL })
            .put("crDroidBuildVersion", crDroidBuild.ifBlank { JSONObject.NULL })
            .put("lineageDevice", lineageDevice.ifBlank { JSONObject.NULL })
            .put("providerSchemaVersions", schemas)
            .put("packageSignatures", packageSignatures)
            .put("packageVersions", packageVersions)
            .put("capabilities", JSONArray(capabilities))
    }

    fun isExactRomMatch(source: JSONObject?, target: JSONObject): Boolean {
        if (source == null) return false
        val scalarKeys = listOf(
            "sdk", "device", "buildIncremental", "crDroidVersion", "crDroidBuildVersion",
            "lineageDevice"
        )
        if (scalarKeys.any { source.optString(it) != target.optString(it) }) return false
        if (!sameObject(source.optJSONObject("providerSchemaVersions"),
                target.optJSONObject("providerSchemaVersions"))) return false
        val sourceSignatures = source.optJSONObject("packageSignatures") ?: return false
        val targetSignatures = target.optJSONObject("packageSignatures") ?: return false
        if (sourceSignatures.length() != targetSignatures.length()) return false
        sourceSignatures.keys().forEach { packageName ->
            if (!sourceSignatures.optString(packageName)
                    .equals(targetSignatures.optString(packageName), ignoreCase = true)) return false
        }
        val sourceVersions = source.optJSONObject("packageVersions") ?: return false
        val targetVersions = target.optJSONObject("packageVersions") ?: return false
        if (sourceVersions.length() != targetVersions.length()) return false
        sourceVersions.keys().forEach { packageName ->
            if (!targetVersions.has(packageName) ||
                sourceVersions.optLong(packageName, -1) != targetVersions.optLong(packageName, -2))
                return false
        }
        return true
    }

    private fun sameObject(left: JSONObject?, right: JSONObject?): Boolean {
        if (left == null || right == null || left.length() != right.length()) return false
        left.keys().forEach { key -> if (left.optString(key) != right.optString(key)) return false }
        return true
    }

    private fun property(name: String): String = runCatching {
        ProcessBuilder("getprop", name).start().inputStream.bufferedReader().readText().trim()
    }.getOrDefault("")

    private fun serviceAvailable(name: String): Boolean = runCatching {
        val output = ProcessBuilder("service", "check", name).start()
            .inputStream.bufferedReader().readText()
        output.contains("found", ignoreCase = true)
    }.getOrDefault(false)

    private fun hasPackage(context: Context, packageName: String): Boolean = runCatching {
        context.packageManager.getPackageInfo(packageName, 0)
    }.isSuccess

    private fun packageIdentity(context: Context, packageName: String): Pair<String, Long>? = runCatching {
        val info = if (Build.VERSION.SDK_INT >= 33) {
            context.packageManager.getPackageInfo(
                packageName,
                PackageManager.PackageInfoFlags.of(PackageManager.GET_SIGNING_CERTIFICATES.toLong())
            )
        } else {
            @Suppress("DEPRECATION")
            context.packageManager.getPackageInfo(packageName, PackageManager.GET_SIGNING_CERTIFICATES)
        }
        val signer = info.signingInfo?.apkContentsSigners?.firstOrNull() ?: return@runCatching null
        val signature = MessageDigest.getInstance("SHA-256").digest(signer.toByteArray())
            .joinToString("") { "%02x".format(it) }
        signature to info.longVersionCode
    }.getOrNull()
}
