package com.vex.phonebackup.agent

import android.app.WallpaperManager
import android.content.Context
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream

object WallpaperAccess {
    fun restore(
        context: Context,
        kind: String,
        expectedSize: Long,
        expectedBlake3: String?,
        consume: (OutputStream) -> Unit
    ): Boolean = runCatching {
        require(kind in setOf("system", "lock")) { "invalid wallpaper kind" }
        val temporary = File(context.noBackupFilesDir,
            "wallpaper-${kind}-${System.nanoTime()}.tmp")
        try {
            val actualHash = FileOutputStream(temporary).buffered(256 * 1024).use { output ->
                val hashing = Blake3OutputStream(output)
                consume(hashing)
                hashing.flush()
                hashing.finalHex()
            }
            require(expectedSize < 0 || temporary.length() == expectedSize) {
                "wallpaper size mismatch"
            }
            require(expectedBlake3.isNullOrBlank() ||
                expectedBlake3.matches(Regex("^[0-9a-fA-F]{64}$")) &&
                actualHash.equals(expectedBlake3, ignoreCase = true)) {
                "wallpaper BLAKE3 mismatch"
            }
            val which = if (kind == "lock")
                WallpaperManager.FLAG_LOCK else WallpaperManager.FLAG_SYSTEM
            temporary.inputStream().buffered(256 * 1024).use { input ->
                WallpaperManager.getInstance(context).setStream(input, null, true, which)
            }
        } finally {
            temporary.delete()
        }
    }.isSuccess
}
