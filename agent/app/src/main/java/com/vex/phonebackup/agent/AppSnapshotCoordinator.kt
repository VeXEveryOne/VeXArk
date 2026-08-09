package com.vex.phonebackup.agent

import android.content.Context
import android.content.pm.ApplicationInfo
import android.os.SystemClock
import com.topjohnwu.superuser.Shell
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

object AppSnapshotCoordinator {
    private const val TRANSACTION_TTL_MILLIS = 15 * 60 * 1000L
    private val packagePattern = Regex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$")
    private val launcherComponentPattern = Regex("^[A-Za-z0-9_.$]+/[A-Za-z0-9_.$]+$")
    private val permissionPattern = Regex("^[A-Za-z0-9_.]+$")
    private val originalStoppedState = ConcurrentHashMap<String, Boolean>()
    private val restores = ConcurrentHashMap<String, RestoreTransaction>()
    @Volatile private var journalFile: File? = null

    fun initialize(context: Context) {
        journalFile = File(context.filesDir, "restore-transactions-v2.json")
        recoverJournal()
    }

    fun begin(context: Context, packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        val application = runCatching {
            context.packageManager.getApplicationInfo(packageName, 0)
        }.getOrNull() ?: return false
        val wasStopped = application.flags and ApplicationInfo.FLAG_STOPPED != 0
        val result = Shell.cmd("am force-stop --user 0 $packageName").exec()
        if (result.isSuccess) originalStoppedState[packageName] = wasStopped
        return result.isSuccess
    }

    fun end(packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        val wasStopped = originalStoppedState.remove(packageName) ?: return false
        if (wasStopped) return true
        return unstop(packageName)
    }

    fun prepareRestore(
        context: Context,
        packageName: String,
        requestedRoots: List<String>,
        completeSnapshot: Boolean
    ): JSONObject {
        cleanupExpired()
        if (!packagePattern.matches(packageName))
            return JSONObject().put("ok", false).put("error", "invalid_package")
        if (restores.containsKey(packageName))
            return JSONObject().put("ok", false).put("error", "restore_already_active")
        val application = runCatching {
            context.packageManager.getApplicationInfo(packageName, 0)
        }.getOrNull() ?: return JSONObject().put("ok", false).put("error", "package_missing")
        val allowedRoots = allowedRoots(application, packageName)
        val roots = requestedRoots.distinct().filter { it in allowedRoots }
        if (roots.isEmpty() || roots.size != requestedRoots.distinct().size)
            return JSONObject().put("ok", false).put("error", "invalid_restore_roots")
        val wasStopped = application.flags and ApplicationInfo.FLAG_STOPPED != 0
        if (!Shell.cmd("am force-stop --user 0 $packageName").exec().isSuccess)
            return JSONObject().put("ok", false).put("error", "force_stop_failed")

        val token = UUID.randomUUID().toString().replace("-", "")
        val mappings = mutableListOf<RootMapping>()
        try {
            roots.forEachIndexed { index, root ->
                val source = File(root)
                val parent = source.parentFile
                    ?: throw IllegalArgumentException("restore root has no parent")
                val safePackage = packageName.replace('.', '_')
                val stage = File(parent, ".vexark-stage-$safePackage-${token.take(12)}-$index")
                val rollback = File(parent, ".vexark-rollback-$safePackage-${token.take(12)}-$index")
                require(stage.absolutePath.startsWith(parent.absolutePath + File.separator))
                require(rollback.absolutePath.startsWith(parent.absolutePath + File.separator))
                Shell.cmd("rm -rf ${quote(stage.absolutePath)} ${quote(rollback.absolutePath)}").exec()
                val prepared = if (!completeSnapshot && rootExists(source.absolutePath)) {
                    Shell.cmd(
                        "mkdir -p ${quote(stage.absolutePath)} && " +
                            "cp -a ${quote(source.absolutePath + "/.")} ${quote(stage.absolutePath + "/")}"
                    ).exec()
                } else {
                    Shell.cmd("mkdir -p ${quote(stage.absolutePath)}").exec()
                }
                if (!prepared.isSuccess) throw IllegalStateException("stage_create_failed")
                if (completeSnapshot && source.absolutePath.startsWith("/data/") &&
                    Shell.cmd(
                        "if [ -e ${quote(source.absolutePath + "/lib")} ] || " +
                            "[ -L ${quote(source.absolutePath + "/lib")} ]; then " +
                            "cp -a ${quote(source.absolutePath + "/lib")} " +
                            "${quote(stage.absolutePath + "/lib")}; fi"
                    ).exec().isSuccess.not()
                ) throw IllegalStateException("managed_entry_preserve_failed")
                mappings += RootMapping(source.absolutePath, stage.absolutePath, rollback.absolutePath)
            }
        } catch (error: Exception) {
            mappings.forEach { Shell.cmd("rm -rf ${quote(it.stage)} ${quote(it.rollback)}").exec() }
            if (!wasStopped) unstop(packageName)
            return JSONObject().put("ok", false)
                .put("error", error.message ?: "stage_create_failed")
        }

        val transaction = RestoreTransaction(
            token, packageName, application.uid, wasStopped, mappings,
            System.currentTimeMillis() + TRANSACTION_TTL_MILLIS
        )
        restores[packageName] = transaction
        persistJournal()
        return JSONObject().put("ok", true).put("transactionId", token)
            .put("rootCount", mappings.size)
    }

    fun resolveRestoreRoot(packageName: String, requestedRoot: String): String? {
        cleanupExpired()
        val transaction = restores[packageName] ?: return null
        transaction.expiresAt = System.currentTimeMillis() + TRANSACTION_TTL_MILLIS
        return transaction.roots.firstOrNull { it.original == requestedRoot }?.stage
    }

    fun finishRestore(packageName: String): JSONObject {
        val transaction = restores[packageName]
            ?: return JSONObject().put("ok", false).put("error", "restore_not_active")
        val swapped = mutableListOf<RootMapping>()
        try {
            transaction.phase = "committing"
            persistJournal()
            transaction.roots.forEach { mapping ->
                val originalExists = rootExists(mapping.original)
                mapping.hadOriginal = originalExists
                persistJournal()
                if (originalExists && !Shell.cmd(
                        "mv ${quote(mapping.original)} ${quote(mapping.rollback)}"
                    ).exec().isSuccess) throw IllegalStateException("rollback_rename_failed")
                if (!Shell.cmd("mv ${quote(mapping.stage)} ${quote(mapping.original)}")
                        .exec().isSuccess) {
                    if (originalExists) Shell.cmd(
                        "mv ${quote(mapping.rollback)} ${quote(mapping.original)}"
                    ).exec()
                    throw IllegalStateException("stage_commit_failed")
                }
                mapping.swapped = true
                persistJournal()
                swapped += mapping
                val metadata = if (mapping.original.startsWith("/data/")) {
                    normalizeInternalMetadata(mapping.original, transaction.uid)
                } else {
                    // Emulated storage is owned/mapped by FUSE; forcing app UID labels is invalid.
                    Shell.cmd("test -d ${quote(mapping.original)}").exec()
                }
                if (!metadata.isSuccess) throw IllegalStateException("metadata_restore_failed")
            }
            if (!transaction.wasStopped && !unstop(packageName))
                throw IllegalStateException("package_unstop_failed")
            val launchComponent = Shell.cmd(
                "cmd package resolve-activity --brief --user 0 " +
                    "-a android.intent.action.MAIN -c android.intent.category.LAUNCHER $packageName"
            ).exec().out
                .map(String::trim)
                .firstOrNull { launcherComponentPattern.matches(it) }
            if (launchComponent != null) {
                val previousFailure = latestCrashOrAnr(packageName)
                val launched = Shell.cmd(
                    "am start --user 0 -W -n $launchComponent"
                ).exec().isSuccess
                if (!launched) throw IllegalStateException("health_launch_failed")
                // WorkManager, DataStore and WebView often open their files shortly after
                // the first activity is displayed. Give those background initializers time
                // to fail while the rollback copy still exists.
                SystemClock.sleep(4_000)
                val currentFailure = latestCrashOrAnr(packageName)
                if (currentFailure != null && currentFailure != previousFailure)
                    throw IllegalStateException("health_crash_or_anr")
            }
            transaction.roots.forEach { mapping ->
                Shell.cmd("rm -rf ${quote(mapping.rollback)} ${quote(mapping.stage)}").exec()
            }
            restores.remove(packageName)
            persistJournal()
            return JSONObject().put("ok", true).put("rolledBack", false)
        } catch (error: Exception) {
            var rollbackOk = true
            swapped.asReversed().forEach { mapping ->
                rollbackOk = Shell.cmd("rm -rf ${quote(mapping.original)}")
                    .exec().isSuccess && rollbackOk
                if (mapping.hadOriginal) rollbackOk = Shell.cmd(
                    "mv ${quote(mapping.rollback)} ${quote(mapping.original)}"
                ).exec().isSuccess && rollbackOk
                if (mapping.hadOriginal && mapping.original.startsWith("/data/"))
                    rollbackOk = normalizeInternalMetadata(mapping.original, transaction.uid)
                        .isSuccess && rollbackOk
            }
            transaction.roots.filterNot { it in swapped }.forEach { mapping ->
                rollbackOk = Shell.cmd("rm -rf ${quote(mapping.stage)}")
                    .exec().isSuccess && rollbackOk
            }
            if (!transaction.wasStopped) rollbackOk = unstop(packageName) && rollbackOk
            if (rollbackOk) restores.remove(packageName)
            else transaction.phase = "rollback_failed"
            persistJournal()
            return JSONObject().put("ok", false)
                .put("rolledBack", rollbackOk)
                .put("error", error.message ?: "commit_failed")
        }
    }

    fun abortRestore(packageName: String): Boolean {
        val transaction = restores[packageName] ?: return false
        var success = true
        transaction.roots.forEach { mapping ->
            success = Shell.cmd("rm -rf ${quote(mapping.stage)}").exec().isSuccess && success
            if (rootExists(mapping.rollback) && !rootExists(mapping.original)) {
                success = Shell.cmd("mv ${quote(mapping.rollback)} ${quote(mapping.original)}")
                    .exec().isSuccess && success
            }
        }
        if (!transaction.wasStopped) success = unstop(packageName) && success
        if (success) restores.remove(packageName)
        else transaction.phase = "abort_failed"
        persistJournal()
        return success
    }

    fun abortAll() {
        restores.keys.toList().forEach(::abortRestore)
        originalStoppedState.keys.toList().forEach(::end)
    }

    fun cleanupExpiredTransactions() = cleanupExpired()

    fun applyPolicy(
        packageName: String,
        enabled: Boolean,
        batteryExempt: Boolean,
        permissions: JSONArray,
        appLocales: String
    ): JSONObject {
        if (!packagePattern.matches(packageName))
            return JSONObject().put("ok", false).put("error", "invalid_package")
        val failures = JSONArray()
        for (index in 0 until permissions.length()) {
            val item = permissions.optJSONObject(index) ?: continue
            val name = item.optString("name")
            if (!permissionPattern.matches(name)) {
                failures.put(name)
                continue
            }
            val action = if (item.optBoolean("granted")) "grant" else "revoke"
            if (!Shell.cmd("pm $action --user 0 $packageName $name").exec().isSuccess) {
                failures.put(name)
                continue
            }
            val flags = item.optInt("flags", 0)
            if (flags >= 0) AgentDiagnostics.log(
                level = "info",
                component = "restore-policy",
                eventCode = "permission_flags_archived",
                message = "Permission flags are not forced without a privileged semantic adapter",
                fields = mapOf("packageName" to packageName, "permission" to name, "flags" to flags)
            )
        }
        val enabledAction = if (enabled) "enable" else "disable-user"
        if (!Shell.cmd("pm $enabledAction --user 0 $packageName").exec().isSuccess)
            failures.put("enabled-state")
        val whitelistAction = if (batteryExempt) "+$packageName" else "-$packageName"
        if (!Shell.cmd("dumpsys deviceidle whitelist $whitelistAction").exec().isSuccess)
            failures.put("battery-whitelist")
        if (appLocales.matches(Regex("^[A-Za-z0-9,-]{0,512}$"))) {
            val localeCommand = buildString {
                append("cmd locale set-app-locales ").append(packageName).append(" --user 0")
                if (appLocales.isNotBlank()) append(" --locales ").append(appLocales)
            }
            if (!Shell.cmd(localeCommand).exec().isSuccess) failures.put("app-locales")
        } else {
            failures.put("app-locales-invalid")
        }
        return JSONObject().put("ok", true).put("failures", failures)
    }

    private fun allowedRoots(application: ApplicationInfo, packageName: String): Set<String> = buildSet {
        application.dataDir?.let(::add)
        application.deviceProtectedDataDir?.let(::add)
        val userId = application.uid / 100_000
        add("/storage/emulated/$userId/Android/data/$packageName")
        add("/storage/emulated/$userId/Android/obb/$packageName")
    }

    private fun cleanupExpired() {
        val now = System.currentTimeMillis()
        restores.values.filter { it.expiresAt < now }.map { it.packageName }
            .forEach(::abortRestore)
    }

    @Synchronized
    private fun persistJournal() {
        val destination = journalFile ?: return
        if (restores.isEmpty()) {
            runCatching { destination.delete() }
            return
        }
        val transactions = JSONArray()
        restores.values.sortedBy { it.packageName }.forEach { transaction ->
            transactions.put(JSONObject()
                .put("id", transaction.id)
                .put("packageName", transaction.packageName)
                .put("uid", transaction.uid)
                .put("wasStopped", transaction.wasStopped)
                .put("expiresAt", transaction.expiresAt)
                .put("phase", transaction.phase)
                .put("roots", JSONArray().apply {
                    transaction.roots.forEach { mapping ->
                        put(JSONObject()
                            .put("original", mapping.original)
                            .put("stage", mapping.stage)
                            .put("rollback", mapping.rollback)
                            .put("hadOriginal", mapping.hadOriginal)
                            .put("swapped", mapping.swapped))
                    }
                }))
        }
        val temporary = File(destination.parentFile, destination.name + ".tmp")
        runCatching {
            temporary.writeText(JSONObject().put("version", 2)
                .put("transactions", transactions).toString())
            if (!temporary.renameTo(destination)) {
                destination.delete()
                check(temporary.renameTo(destination)) { "journal_rename_failed" }
            }
        }.onFailure {
            AgentDiagnostics.log(
                level = "error",
                component = "restore-transaction",
                eventCode = "journal_write_failed",
                message = it.message.orEmpty(),
                error = it
            )
        }
    }

    @Synchronized
    private fun recoverJournal() {
        val source = journalFile ?: return
        if (!source.isFile) return
        val recovered = runCatching {
            val root = JSONObject(source.readText())
            val transactions = root.optJSONArray("transactions") ?: JSONArray()
            for (index in 0 until transactions.length()) {
                val item = transactions.optJSONObject(index) ?: continue
                val packageName = item.optString("packageName")
                if (!packagePattern.matches(packageName)) continue
                val roots = item.optJSONArray("roots") ?: continue
                var recoveryOk = true
                for (rootIndex in 0 until roots.length()) {
                    val mapping = roots.optJSONObject(rootIndex) ?: continue
                    val original = mapping.optString("original")
                    val stage = mapping.optString("stage")
                    val rollback = mapping.optString("rollback")
                    if (!journalMappingIsSafe(packageName, original, stage, rollback)) {
                        recoveryOk = false
                        continue
                    }
                    if (rootExists(rollback)) {
                        if (rootExists(original))
                            recoveryOk = Shell.cmd("rm -rf ${quote(original)}").exec().isSuccess && recoveryOk
                        recoveryOk = Shell.cmd("mv ${quote(rollback)} ${quote(original)}")
                            .exec().isSuccess && recoveryOk
                    } else if (mapping.optBoolean("swapped") &&
                        !mapping.optBoolean("hadOriginal") && rootExists(original)
                    ) {
                        recoveryOk = Shell.cmd("rm -rf ${quote(original)}").exec().isSuccess && recoveryOk
                    }
                    if (rootExists(stage))
                        recoveryOk = Shell.cmd("rm -rf ${quote(stage)}").exec().isSuccess && recoveryOk
                }
                if (recoveryOk && !item.optBoolean("wasStopped")) unstop(packageName)
                if (!recoveryOk) throw IllegalStateException("journal_recovery_failed:$packageName")
            }
            source.delete()
        }
        recovered.onFailure {
            AgentDiagnostics.log(
                level = "error",
                component = "restore-transaction",
                eventCode = "journal_recovery_failed",
                message = it.message.orEmpty(),
                error = it
            )
        }
    }

    private fun journalMappingIsSafe(
        packageName: String,
        original: String,
        stage: String,
        rollback: String
    ): Boolean {
        val escaped = Regex.escape(packageName)
        val originalPattern = Regex(
            "^(?:/data/user(?:_de)?/[0-9]+/$escaped|" +
                "/storage/emulated/[0-9]+/Android/(?:data|obb)/$escaped)$"
        )
        if (!originalPattern.matches(original)) return false
        val parent = File(original).parentFile?.absolutePath ?: return false
        return File(stage).parentFile?.absolutePath == parent &&
            File(rollback).parentFile?.absolutePath == parent &&
            File(stage).name.startsWith(".vexark-stage-") &&
            File(rollback).name.startsWith(".vexark-rollback-")
    }

    private fun rootExists(path: String): Boolean =
        Shell.cmd("test -e ${quote(path)}").exec().isSuccess

    private fun unstop(packageName: String): Boolean {
        val commands = listOf(
            "cmd package unstop --user 0 $packageName",
            "cmd package set-stopped-state --user 0 false $packageName"
        )
        return commands.any { Shell.cmd(it).exec().isSuccess }
    }

    private fun normalizeInternalMetadata(path: String, uid: Int) = Shell.cmd(
        "chown -R $uid:$uid ${quote(path)} && " +
            // -D is required for Android app-data paths. Without it toybox uses the
            // generic system_data_file label instead of the package's MCS label.
            "restorecon -RDF ${quote(path)}"
    ).exec()

    private fun latestCrashOrAnr(packageName: String): String? {
        val result = Shell.cmd("dumpsys activity exit-info $packageName").exec()
        if (!result.isSuccess) return null
        return crashOrAnrSignature(result.out)
    }

    internal fun crashOrAnrSignature(lines: List<String>): String? {
        val first = lines.indexOfFirst { it.trim() == "ApplicationExitInfo #0:" }
        if (first < 0) return null
        val entry = lines.drop(first + 1).take(4)
        val reason = entry.firstOrNull { it.contains("reason=") } ?: return null
        if (!Regex("(?:^|\\s)reason=(?:4|5|6)\\b|\\((?:CRASH|CRASH_NATIVE|ANR)\\)",
                RegexOption.IGNORE_CASE)
                .containsMatchIn(reason)) return null
        return entry.joinToString("\n", transform = String::trim)
    }

    private fun quote(value: String): String = "'${value.replace("'", "'\\''")}'"

    private data class RestoreTransaction(
        val id: String,
        val packageName: String,
        val uid: Int,
        val wasStopped: Boolean,
        val roots: List<RootMapping>,
        @Volatile var expiresAt: Long,
        @Volatile var phase: String = "staged"
    )

    private data class RootMapping(
        val original: String,
        val stage: String,
        val rollback: String,
        var hadOriginal: Boolean = false,
        var swapped: Boolean = false
    )
}
