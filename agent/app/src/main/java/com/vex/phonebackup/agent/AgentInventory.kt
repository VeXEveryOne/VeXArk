package com.vex.phonebackup.agent

import android.content.Context
import android.annotation.SuppressLint
import android.content.pm.ApplicationInfo
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import android.app.LocaleManager
import android.os.Environment
import android.os.StatFs
import android.provider.Settings
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest

object AgentInventory {
    fun device(context: Context): JSONObject {
        val stat = StatFs(Environment.getDataDirectory().absolutePath)
        val root = RootCapabilities.probe(false)
        return JSONObject()
            .put("stableId", getStableId(context))
            .put("model", Build.MODEL)
            .put("device", Build.DEVICE)
            .put("androidVersion", Build.VERSION.RELEASE)
            .put("sdk", Build.VERSION.SDK_INT)
            .put("fingerprint", Build.FINGERPRINT)
            .put("abi", Build.SUPPORTED_ABIS.firstOrNull().orEmpty())
            .put("selinux", readSelinux())
            .put("rootAvailable", root.available)
            .put("rootGranted", root.granted)
            .put("rootProvider", root.provider)
            .put("dataTotalBytes", stat.totalBytes)
            .put("dataAvailableBytes", stat.availableBytes)
            .put("romIdentity", RomIdentityCollector.collect(context))
            .put("agentPackage", context.packageName)
    }

    fun packages(
        context: Context,
        includeSystemApps: Boolean,
        requestedPackages: Set<String>? = null,
        hashArtifacts: Boolean = true
    ): JSONArray {
        val manager = context.packageManager
        val packages = if (Build.VERSION.SDK_INT >= 33) {
            manager.getInstalledPackages(PackageManager.PackageInfoFlags.of(
                (PackageManager.GET_PERMISSIONS or PackageManager.GET_SIGNING_CERTIFICATES).toLong()
            ))
        } else {
            @Suppress("DEPRECATION")
            manager.getInstalledPackages(PackageManager.GET_PERMISSIONS or PackageManager.GET_SIGNING_CERTIFICATES)
        }
        return JSONArray().apply {
            packages.asSequence()
                .filter { requestedPackages == null || it.packageName in requestedPackages }
                .filter {
                    includeSystemApps ||
                        it.applicationInfo?.flags?.and(ApplicationInfo.FLAG_SYSTEM) == 0
                }
                .sortedBy { it.packageName }
                .forEach { put(packageJson(context, manager, it, hashArtifacts)) }
        }
    }

    private fun packageJson(
        context: Context,
        manager: PackageManager,
        info: PackageInfo,
        hashArtifacts: Boolean
    ): JSONObject {
        val app = info.applicationInfo
        val userId = (app?.uid ?: 0) / 100_000
        val sourceDir = app?.sourceDir.orEmpty()
        val splits = app?.splitSourceDirs?.toList().orEmpty()
        val signatures = info.signingInfo?.apkContentsSigners?.toList().orEmpty()
        val digest = MessageDigest.getInstance("SHA-256")
        val certificate = signatures.firstOrNull()?.toByteArray()?.let(digest::digest)
            ?.joinToString("") { "%02x".format(it) }.orEmpty()
        val signingLineage = info.signingInfo?.signingCertificateHistory?.map { signer ->
            MessageDigest.getInstance("SHA-256").digest(signer.toByteArray())
                .joinToString("") { "%02x".format(it) }
        }.orEmpty()
        val apkArtifacts = JSONArray().apply {
            (listOf(sourceDir) + splits).filter(String::isNotBlank)
                .forEach { put(apkJson(it, hashArtifacts)) }
        }
        val dataRoots = buildList {
            app?.dataDir?.let { add("credential_encrypted" to it) }
            app?.deviceProtectedDataDir?.let { add("device_encrypted" to it) }
            val emulated = "/storage/emulated/$userId"
            val externalData = "$emulated/Android/data/${info.packageName}"
            val obb = "$emulated/Android/obb/${info.packageName}"
            if (File(externalData).isDirectory) add("external_data" to externalData)
            if (File(obb).isDirectory) add("obb" to obb)
        }
        val dataPaths = JSONArray().apply {
            dataRoots.forEach { (_, path) -> put(path) }
        }
        val typedDataRoots = JSONArray().apply {
            dataRoots.forEach { (kind, path) ->
                put(JSONObject().put("kind", kind).put("path", path).put("userId", userId))
            }
        }
        val runtimePermissions = JSONArray().apply {
            info.requestedPermissions.orEmpty().sorted().filter { permission ->
                runCatching {
                    manager.getPermissionInfo(permission, 0).protection and 0xF ==
                        android.content.pm.PermissionInfo.PROTECTION_DANGEROUS
                }.getOrDefault(false)
            }.forEach { permission ->
                put(JSONObject()
                    .put("name", permission)
                    .put(
                        "granted",
                        manager.checkPermission(permission, info.packageName) ==
                            PackageManager.PERMISSION_GRANTED
                    )
                    .put("flags", -1))
            }
        }
        val batteryExempt = (context.getSystemService(Context.POWER_SERVICE) as PowerManager)
            .isIgnoringBatteryOptimizations(info.packageName)
        val appLocales = if (Build.VERSION.SDK_INT >= 33) runCatching {
            context.getSystemService(LocaleManager::class.java)
                .getApplicationLocales(info.packageName).toLanguageTags()
        }.getOrDefault("") else ""
        return JSONObject()
            .put("packageName", info.packageName)
            .put("label", app?.loadLabel(manager)?.toString().orEmpty())
            .put("versionCode", info.longVersionCode)
            .put("versionName", info.versionName.orEmpty())
            .put("signingCertificateSha256", certificate)
            .put("installer", runCatching {
                if (Build.VERSION.SDK_INT >= 30) manager.getInstallSourceInfo(info.packageName).installingPackageName
                else @Suppress("DEPRECATION") manager.getInstallerPackageName(info.packageName)
            }.getOrNull())
            .put("userId", userId)
            .put("uid", app?.uid ?: -1)
            .put("isSystem", app?.flags?.and(ApplicationInfo.FLAG_SYSTEM) != 0)
            .put("isEnabled", app?.enabled == true)
            .put("apkArtifacts", apkArtifacts)
            .put("dataPaths", dataPaths)
            .put("dataRoots", typedDataRoots)
            .put("signingLineageSha256", JSONArray(signingLineage))
            .put("targetSdk", app?.targetSdkVersion ?: 0)
            .put("appLocales", appLocales)
            .put("runtimePermissions", runtimePermissions)
            .put("batteryOptimizationExempt", batteryExempt)
    }

    private fun apkJson(path: String, hashArtifact: Boolean): JSONObject {
        val file = File(path)
        val digest = MessageDigest.getInstance("SHA-256")
        if (hashArtifact) runCatching {
            FileInputStream(file).use { input ->
                val buffer = ByteArray(256 * 1024)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    digest.update(buffer, 0, count)
                }
            }
        }
        return JSONObject()
            .put("path", path)
            .put("fileName", file.name)
            .put("size", file.length())
            .put("modifiedUnixNanos", file.lastModified() * 1_000_000L)
            .put("sha256", if (hashArtifact)
                digest.digest().joinToString("") { "%02x".format(it) } else "")
    }

    private fun readSelinux(): String = runCatching {
        ProcessBuilder("getenforce").start().inputStream.bufferedReader().readText().trim()
    }.getOrDefault("unknown")

    private fun String?.orEmpty() = this ?: ""
    @SuppressLint("HardwareIds")
    private fun getStableId(context: Context): String =
        Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
            ?.takeIf { it.isNotBlank() }
            ?: Build.DEVICE
}
