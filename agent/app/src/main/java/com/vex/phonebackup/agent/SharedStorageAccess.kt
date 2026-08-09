package com.vex.phonebackup.agent

import android.os.Environment
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.nio.file.FileVisitResult
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.SimpleFileVisitor
import java.nio.file.attribute.BasicFileAttributes

object SharedStorageAccess {
    private val root = File(Environment.getExternalStorageDirectory().absolutePath)
    private val allowedNames = listOf(
        "Download", "Documents", "Music", "Podcasts", "Recordings", "Ringtones",
        "Notifications", "Alarms", "Audiobooks", "Telegram", "Android/media"
    )
    private val excludedDirectories = setOf(
        "DCIM", "Pictures", "Movies", ".thumbnails", ".Trash", "Trash"
    )
    private val excludedExtensions = setOf(
        "jpg", "jpeg", "png", "webp", "gif", "heic", "heif", "raw", "dng",
        "mp4", "mkv", "webm", "avi", "mov", "3gp", "m4v"
    )

    fun availableRoots(): List<String> =
        allowedNames.map { File(root, it) }.filter(File::isDirectory).map(File::getAbsolutePath)

    fun scan(rootPath: String, emit: (String) -> Unit): Boolean = runCatching {
        requireAccess()
        val scanRoot = validateRoot(rootPath)
        Files.walkFileTree(scanRoot.toPath(), object : SimpleFileVisitor<Path>() {
            override fun preVisitDirectory(
                directory: Path,
                attributes: BasicFileAttributes
            ): FileVisitResult {
                if (directory != scanRoot.toPath()) {
                    val relative = normalize(scanRoot.toPath().relativize(directory))
                    emit(entry(relative, "directory", 0, attributes.lastModifiedTime().toMillis(), false))
                }
                return FileVisitResult.CONTINUE
            }

            override fun visitFile(file: Path, attributes: BasicFileAttributes): FileVisitResult {
                val relative = normalize(scanRoot.toPath().relativize(file))
                val excluded = shouldExclude(relative)
                emit(entry(
                    relative,
                    if (excluded) "excluded" else if (attributes.isSymbolicLink) "symlink" else "file",
                    attributes.size(),
                    attributes.lastModifiedTime().toMillis(),
                    excluded
                ))
                return FileVisitResult.CONTINUE
            }
        })
    }.isSuccess

    fun read(rootPath: String, relative: String, emit: (ByteArray) -> Unit): Boolean = runCatching {
        requireAccess()
        require(isSafeRelative(relative)) { "unsafe relative path" }
        val scanRoot = validateRoot(rootPath).canonicalFile
        val candidate = File(scanRoot, relative).canonicalFile
        require(candidate.path.startsWith(scanRoot.path + File.separator)) { "path escapes root" }
        require(candidate.isFile && !Files.isSymbolicLink(candidate.toPath())) { "not a regular file" }
        candidate.inputStream().use { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                emit(if (count == buffer.size) buffer.copyOf() else buffer.copyOf(count))
            }
        }
    }.isSuccess

    fun restore(
        rootPath: String,
        relative: String,
        kind: String,
        modifiedUnixNanos: Long,
        expectedSize: Long,
        expectedBlake3: String?,
        consume: (OutputStream) -> Unit
    ): Boolean = runCatching {
        requireAccess()
        require(isSafeRelative(relative)) { "unsafe relative path" }
        require(!shouldExclude(relative)) { "media path is excluded" }
        val scanRoot = validateRoot(rootPath).canonicalFile
        val target = File(scanRoot, relative).canonicalFile
        require(target.path.startsWith(scanRoot.path + File.separator)) { "path escapes root" }
        var parent = target.parentFile
        while (parent != null && parent != scanRoot) {
            require(!Files.isSymbolicLink(parent.toPath())) { "symlink parent rejected" }
            parent = parent.parentFile
        }
        when (kind) {
            "directory" -> {
                object : OutputStream() {
                    override fun write(value: Int) = Unit
                }.use(consume)
                require(target.mkdirs() || target.isDirectory)
            }
            "file" -> {
                require(target.parentFile?.mkdirs() == true || target.parentFile?.isDirectory == true)
                val temporary = File(target.parentFile, ".${target.name}.phonebackup-${System.nanoTime()}")
                try {
                    val actualHash = FileOutputStream(temporary).buffered(256 * 1024).use { output ->
                        val hashing = Blake3OutputStream(output)
                        consume(hashing)
                        hashing.flush()
                        hashing.finalHex()
                    }
                    require(expectedSize < 0 || temporary.length() == expectedSize) {
                        "restored file size mismatch"
                    }
                    require(expectedBlake3.isNullOrBlank() ||
                        expectedBlake3.matches(Regex("^[0-9a-fA-F]{64}$")) &&
                        actualHash.equals(expectedBlake3, ignoreCase = true)) {
                        "restored file BLAKE3 mismatch"
                    }
                    require(temporary.renameTo(target)) { "cannot publish restored file" }
                } finally {
                    temporary.delete()
                }
            }
            else -> error("unsupported shared entry kind")
        }
        target.setLastModified(modifiedUnixNanos / 1_000_000L)
    }.isSuccess

    private fun entry(
        relative: String,
        kind: String,
        size: Long,
        modifiedMillis: Long,
        excluded: Boolean
    ): String = JSONObject()
        .put("relativePath", relative)
        .put("kind", kind)
        .put("size", size)
        .put("modifiedUnixNanos", modifiedMillis * 1_000_000L)
        .put("mode", 0)
        .put("uid", 0)
        .put("gid", 0)
        .put("selinuxLabel", JSONObject.NULL)
        .put("linkTarget", JSONObject.NULL)
        .put("contentHash", JSONObject.NULL)
        .put("excluded", excluded)
        .toString()

    private fun validateRoot(value: String): File {
        val candidate = File(value).canonicalFile
        require(availableRoots().map { File(it).canonicalPath }.contains(candidate.path)) {
            "shared root is not allow-listed"
        }
        return candidate
    }

    private fun requireAccess() {
        require(android.os.Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager()) {
            "all-files access is not granted"
        }
    }

    private fun shouldExclude(relative: String): Boolean {
        val parts = relative.replace('\\', '/').split('/').filter(String::isNotEmpty)
        if (parts.any(excludedDirectories::contains)) return true
        return parts.lastOrNull()?.substringAfterLast('.', "")?.lowercase() in excludedExtensions
    }

    private fun isSafeRelative(value: String): Boolean =
        value.isNotBlank() && !value.startsWith('/') && !value.startsWith('\\') &&
            value.replace('\\', '/').split('/').all { it.isNotEmpty() && it != "." && it != ".." }

    private fun normalize(path: Path): String = path.toString().replace('\\', '/')
}
