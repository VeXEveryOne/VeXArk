package com.vex.phonebackup.agent

import android.content.Context
import android.system.Os
import com.topjohnwu.superuser.Shell
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream

object RootHelper {
    private const val ASSET = "helper/arm64-v8a/phonebackup-helper"

    fun probe(context: Context): JSONObject = runCatching {
        withExtracted(context) { helper ->
            val result = Shell.cmd("${quote(helper.absolutePath)} probe").exec()
            val stdout = result.out.joinToString("\n").trim()
            val stderr = result.err.joinToString("\n").trim()
            val response = if (result.isSuccess && stdout.isNotBlank()) {
                runCatching { JSONObject(stdout) }.getOrElse {
                    JSONObject().put("raw", stdout)
                }
            } else {
                JSONObject()
            }
            val helperUid = response.optInt("uid", -1).takeIf { it >= 0 }
            val helperReady = result.isSuccess && helperUid == 0
            val diagnosticStderr = when {
                stderr.isNotBlank() -> stderr
                result.isSuccess && helperUid != 0 ->
                    "Root helper returned uid=${helperUid ?: "unknown"}"
                else -> ""
            }
            response
                .put("ok", helperReady)
                .put("exitCode", result.code)
                .put("uid", helperUid ?: JSONObject.NULL)
                .put("stdout", stdout)
                .put("stderr", diagnosticStderr)
                .also {
                    AgentDiagnostics.log(
                        level = if (helperReady) "info" else "warn",
                        component = "root-helper",
                        eventCode = "probe",
                        message = if (helperReady)
                            "Root helper probe completed"
                        else
                            "Root helper probe failed",
                        result = if (helperReady) "ok" else "failed",
                        fields = mapOf(
                            "exitCode" to result.code,
                            "uid" to helperUid,
                            "stdout" to stdout,
                            "stderr" to diagnosticStderr
                        )
                    )
                }
        }
    }.getOrElse { failure ->
        AgentDiagnostics.log(
            level = "error",
            component = "root-helper",
            eventCode = "probe_exception",
            message = "Root helper probe raised an exception",
            error = failure
        )
        JSONObject()
            .put("ok", false)
            .put("exitCode", JSONObject.NULL)
            .put("uid", JSONObject.NULL)
            .put("stdout", "")
            .put("stderr", failure.message.orEmpty())
            .put("exceptionType", failure.javaClass.name)
    }

    fun scan(
        context: Context,
        root: String,
        includeCaches: Boolean,
        fullHash: Boolean,
        emit: (String) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val command = buildString {
            append(quote(helper.absolutePath))
            append(" scan --root ").append(quote(root))
            if (includeCaches) append(" --include-caches")
            if (fullHash) append(" --full-hash")
        }
        val outcome = streamFromRootPipe(context, "scan", command) { input ->
            input.reader(Charsets.UTF_8).buffered(256 * 1024).useLines { lines ->
                lines.forEach(emit)
            }
        }
        val success = outcome.result?.isSuccess == true && outcome.failure == null
        AgentDiagnostics.log(
            level = if (success) "info" else "warn",
            component = "root-helper",
            eventCode = "scan",
            message = if (success) "Root helper scan completed" else "Root helper scan failed",
            result = if (success) "ok" else "failed",
            error = outcome.failure,
            fields = mapOf(
                "exitCode" to outcome.result?.code,
                "stderr" to outcome.result?.err?.joinToString("\n")?.trim().orEmpty(),
                "pipeFailure" to outcome.failure?.javaClass?.name
            )
        )
        success
    }

    fun read(
        context: Context,
        root: String,
        relative: String,
        emit: (ByteArray) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val command = "${quote(helper.absolutePath)} read " +
            "--root ${quote(root)} --relative ${quote(relative)}"
        val outcome = streamFromRootPipe(context, "read", command) { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                if (count > 0) emit(buffer.copyOf(count))
            }
        }
        val success = outcome.result?.isSuccess == true && outcome.failure == null
        AgentDiagnostics.log(
            level = if (success) "info" else "warn",
            component = "root-helper",
            eventCode = "read",
            message = if (success) "Root helper read completed" else "Root helper read failed",
            result = if (success) "ok" else "failed",
            error = outcome.failure,
            fields = mapOf(
                "exitCode" to outcome.result?.code,
                "stderr" to outcome.result?.err?.joinToString("\n")?.trim().orEmpty(),
                "pipeFailure" to outcome.failure?.javaClass?.name
            )
        )
        success
    }

    fun readWallpaper(
        context: Context,
        kind: String,
        emit: (ByteArray) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        if (kind !in setOf("system", "lock")) return@withExtracted false
        val command = "${quote(helper.absolutePath)} read-wallpaper --kind ${quote(kind)}"
        val outcome = streamFromRootPipe(context, "wallpaper", command) { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                if (count > 0) emit(buffer.copyOf(count))
            }
        }
        outcome.result?.isSuccess == true && outcome.failure == null
    }

    fun restore(
        context: Context,
        root: String,
        relative: String,
        mode: Int,
        uid: Int,
        gid: Int,
        modifiedUnixNanos: Long,
        expectedSize: Long,
        expectedBlake3: String?,
        selinuxLabel: String?,
        consume: (OutputStream) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val directory = File(context.noBackupFilesDir, "root-pipes").apply { mkdirs() }
        val fifo = File(directory, "restore-${System.nanoTime()}.fifo")
        runCatching {
            Os.mkfifo(fifo.absolutePath, 0b110000000)
            val command = buildString {
                append(quote(helper.absolutePath))
                append(" restore --root ").append(quote(root))
                append(" --relative ").append(quote(relative))
                append(" --mode ").append(mode)
                append(" --uid ").append(uid)
                append(" --gid ").append(gid)
                append(" --modified-unix-nanos ").append(modifiedUnixNanos)
                append(" --expected-size ").append(expectedSize)
                if (!expectedBlake3.isNullOrBlank()) {
                    append(" --expected-blake3 ").append(quote(expectedBlake3))
                }
                if (!selinuxLabel.isNullOrBlank()) {
                    append(" --selinux-label ").append(quote(selinuxLabel))
                }
                append(" < ").append(quote(fifo.absolutePath))
            }
            val future = Shell.cmd(command).enqueue()
            FileOutputStream(fifo).buffered(256 * 1024).use(consume)
            future.get().isSuccess
        }.getOrDefault(false).also {
            fifo.delete()
        }
    }

    fun restoreDirectory(
        context: Context,
        root: String,
        relative: String,
        mode: Int,
        uid: Int,
        gid: Int,
        modifiedUnixNanos: Long,
        selinuxLabel: String?
    ): Boolean = withExtracted(context) { helper ->
        val command = buildString {
            append(quote(helper.absolutePath))
            append(" restore-directory --root ").append(quote(root))
            append(" --relative ").append(quote(relative))
            append(" --mode ").append(mode)
            append(" --uid ").append(uid)
            append(" --gid ").append(gid)
            append(" --modified-unix-nanos ").append(modifiedUnixNanos)
            if (!selinuxLabel.isNullOrBlank()) {
                append(" --selinux-label ").append(quote(selinuxLabel))
            }
        }
        Shell.cmd(command).exec().isSuccess
    }

    fun <T> withExtracted(context: Context, block: (File) -> T): T =
        block(ensureExtracted(context))

    @Synchronized
    private fun ensureExtracted(context: Context): File {
        val directory = File(context.noBackupFilesDir, "root-helper").apply { mkdirs() }
        val target = File(directory, "phonebackup-helper-${BuildConfig.VERSION_CODE}")
        if (!target.isFile) {
            val temporary = File(directory, "${target.name}.${System.nanoTime()}.tmp")
            try {
                context.assets.open(ASSET).use { input ->
                    temporary.outputStream().use { output -> input.copyTo(output, 256 * 1024) }
                }
                Os.chmod(temporary.absolutePath, 0b111000000)
                if (!temporary.renameTo(target))
                    throw IllegalStateException("Cannot publish root-helper")
            } finally {
                temporary.delete()
            }
        }
        return target
    }

    fun cleanupStalePipes(context: Context) {
        val cutoff = System.currentTimeMillis() - 30 * 60 * 1000L
        File(context.noBackupFilesDir, "root-pipes").listFiles()
            ?.filter { it.lastModified() < cutoff }
            ?.forEach(File::delete)
    }

    private fun streamFromRootPipe(
        context: Context,
        prefix: String,
        command: String,
        consume: (InputStream) -> Unit
    ): PipeOutcome {
        val directory = File(context.noBackupFilesDir, "root-pipes").apply { mkdirs() }
        val fifo = File(directory, "$prefix-${System.nanoTime()}.fifo")
        var result: Shell.Result? = null
        var failure: Throwable? = null
        try {
            Os.mkfifo(fifo.absolutePath, 0b110000000)
            val future = Shell.cmd("$command > ${quote(fifo.absolutePath)}").enqueue()
            try {
                FileInputStream(fifo).buffered(256 * 1024).use { input ->
                    consume(input)
                }
            } catch (error: Throwable) {
                failure = error
            }
            result = runCatching { future.get() }
                .onFailure { if (failure == null) failure = it }
                .getOrNull()
        } catch (error: Throwable) {
            failure = error
        } finally {
            fifo.delete()
        }
        return PipeOutcome(result, failure)
    }

    private data class PipeOutcome(
        val result: Shell.Result?,
        val failure: Throwable?
    )

    private fun quote(value: String): String = "'${value.replace("'", "'\\''")}'"
}
