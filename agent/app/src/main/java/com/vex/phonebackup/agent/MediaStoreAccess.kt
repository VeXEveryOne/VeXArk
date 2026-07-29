package com.vex.phonebackup.agent

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.core.content.ContextCompat
import org.json.JSONObject
import java.io.FileInputStream
import java.security.MessageDigest
import kotlin.math.min

object MediaStoreAccess {
    data class ReadResult(
        val sourceSize: Long,
        val modifiedUnixNanos: Long,
        val acceptedOffset: Long,
        val transferredBytes: Long,
        val sha256: String
    )

    fun status(context: Context): JSONObject {
        val allFiles = Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager()
        val images = allFiles || granted(
            context,
            if (Build.VERSION.SDK_INT >= 33) {
                Manifest.permission.READ_MEDIA_IMAGES
            } else {
                Manifest.permission.READ_EXTERNAL_STORAGE
            }
        )
        val videos = allFiles || granted(
            context,
            if (Build.VERSION.SDK_INT >= 33) {
                Manifest.permission.READ_MEDIA_VIDEO
            } else {
                Manifest.permission.READ_EXTERNAL_STORAGE
            }
        )
        return JSONObject()
            .put("images", images)
            .put("videos", videos)
            .put(
                "selected",
                Build.VERSION.SDK_INT >= 34 &&
                    granted(context, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) &&
                    !images &&
                    !videos
            )
            .put("allFiles", allFiles)
    }

    fun scan(context: Context, emit: (String) -> Unit): Boolean = runCatching {
        val access = status(context)
        require(access.optBoolean("images") || access.optBoolean("videos")) {
            "photo/video permission is not granted"
        }
        val projection = arrayOf(
            MediaStore.Files.FileColumns._ID,
            MediaStore.Files.FileColumns.DISPLAY_NAME,
            MediaStore.Files.FileColumns.RELATIVE_PATH,
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.DATE_MODIFIED,
            MediaStore.Files.FileColumns.MEDIA_TYPE,
            MediaStore.Files.FileColumns.MIME_TYPE
        )
        val allowedTypes = buildList {
            if (access.optBoolean("images")) add(MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE)
            if (access.optBoolean("videos")) add(MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO)
        }
        val placeholders = allowedTypes.joinToString(",") { "?" }
        val collection = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL)
        context.contentResolver.query(
            collection,
            projection,
            "${MediaStore.Files.FileColumns.MEDIA_TYPE} IN ($placeholders)",
            allowedTypes.map(Int::toString).toTypedArray(),
            "${MediaStore.Files.FileColumns.DATE_MODIFIED} ASC"
        )?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID)
            val nameIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME)
            val pathIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.RELATIVE_PATH)
            val sizeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE)
            val modifiedIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED)
            val typeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MEDIA_TYPE)
            val mimeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE)
            while (cursor.moveToNext()) {
                val id = cursor.getLong(idIndex)
                val displayName = safeName(cursor.getString(nameIndex), id)
                val relativeDirectory = cursor.getString(pathIndex)
                    .orEmpty()
                    .replace('\\', '/')
                    .trim('/')
                val relative = listOf(relativeDirectory, displayName)
                    .filter(String::isNotBlank)
                    .joinToString("/")
                val uri = Uri.withAppendedPath(collection, id.toString())
                val kind = if (
                    cursor.getInt(typeIndex) == MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO
                ) "video" else "image"
                emit(JSONObject()
                    .put("relativePath", relative)
                    .put("kind", "file")
                    .put("size", cursor.getLong(sizeIndex).coerceAtLeast(0))
                    .put(
                        "modifiedUnixNanos",
                        cursor.getLong(modifiedIndex).coerceAtLeast(0) * 1_000_000_000L
                    )
                    .put("mode", 0)
                    .put("uid", 0)
                    .put("gid", 0)
                    .put("selinuxLabel", JSONObject.NULL)
                    .put("linkTarget", uri.toString())
                    .put("contentHash", cursor.getString(mimeIndex))
                    .put("mediaKind", kind)
                    .toString())
            }
        }
    }.isSuccess

    fun read(context: Context, value: String, emit: (ByteArray) -> Unit): Boolean = runCatching {
        readV2(context, value, 0) { buffer, count ->
            emit(if (count == buffer.size) buffer.copyOf() else buffer.copyOf(count))
        }
    }.isSuccess

    fun readV2(
        context: Context,
        value: String,
        requestedOffset: Long,
        expectedSize: Long? = null,
        expectedModifiedUnixNanos: Long? = null,
        emit: (ByteArray, Int) -> Unit
    ): ReadResult {
        val uri = validateUri(context, value)
        val metadata = metadata(context, uri)
        if (expectedSize != null && expectedSize >= 0 && metadata.first != expectedSize)
            error("MediaStore item size changed")
        if (expectedModifiedUnixNanos != null &&
            expectedModifiedUnixNanos > 0 &&
            metadata.second != expectedModifiedUnixNanos)
            error("MediaStore item timestamp changed")
        require(requestedOffset in 0..metadata.first) { "resume offset is outside the media file" }

        val digest = MessageDigest.getInstance("SHA-256")
        var transferred = 0L
        context.contentResolver.openFileDescriptor(uri, "r")?.use { descriptor ->
            FileInputStream(descriptor.fileDescriptor).use { input ->
                val channel = input.channel
                val acceptedOffset = runCatching {
                    channel.position(requestedOffset)
                    channel.position()
                }.getOrDefault(0)
                require(acceptedOffset == requestedOffset) { "media file is not seekable" }
                val buffer = ByteArray(DataBufferBytes)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (count == 0) continue
                    digest.update(buffer, 0, count)
                    emit(buffer, count)
                    transferred += count
                }
            }
        } ?: error("MediaStore item cannot be opened")
        return ReadResult(
            metadata.first,
            metadata.second,
            requestedOffset,
            transferred,
            digest.digest().toHex()
        )
    }

    fun probe(length: Long, emit: (ByteArray, Int) -> Unit): ReadResult {
        require(length in 0..ProbeLimitBytes) { "probe length is invalid" }
        val buffer = ByteArray(DataBufferBytes) { index ->
            ((index * 31 + 17) and 0xff).toByte()
        }
        val digest = MessageDigest.getInstance("SHA-256")
        var transferred = 0L
        while (transferred < length) {
            val count = min(buffer.size.toLong(), length - transferred).toInt()
            digest.update(buffer, 0, count)
            emit(buffer, count)
            transferred += count
        }
        return ReadResult(
            length,
            0,
            0,
            transferred,
            digest.digest().toHex()
        )
    }

    private fun validateUri(context: Context, value: String): Uri {
        val uri = Uri.parse(value)
        require(uri.scheme == ContentResolverScheme && uri.authority == MediaAuthority)
        require(uri.pathSegments.firstOrNull() == MediaStore.VOLUME_EXTERNAL)
        val mime = context.contentResolver.getType(uri).orEmpty()
        require(mime.startsWith("image/") || mime.startsWith("video/")) {
            "content URI is not photo/video media"
        }
        return uri
    }

    private fun metadata(context: Context, uri: Uri): Pair<Long, Long> {
        val projection = arrayOf(
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.DATE_MODIFIED
        )
        return context.contentResolver.query(uri, projection, null, null, null)?.use { cursor ->
            require(cursor.moveToFirst()) { "MediaStore item is missing" }
            Pair(
                cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE))
                    .coerceAtLeast(0),
                cursor.getLong(
                    cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED)
                ).coerceAtLeast(0) * 1_000_000_000L
            )
        } ?: error("MediaStore metadata cannot be read")
    }

    private fun ByteArray.toHex(): String =
        joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }

    private fun safeName(value: String?, id: Long): String {
        val sanitized = value.orEmpty()
            .replace('/', '_')
            .replace('\\', '_')
            .trim()
        return sanitized.ifBlank { "media-$id" }
    }

    private fun granted(context: Context, permission: String): Boolean =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

    private const val ContentResolverScheme = "content"
    private const val MediaAuthority = "media"
    const val DataBufferBytes = 1024 * 1024
    const val ProbeLimitBytes = 64L * 1024 * 1024
}
