package com.vex.phonebackup.agent

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

data class RestoreApproval(
    val token: String,
    val snapshotId: String,
    val itemCount: Int,
    val expiresAtMillis: Long
)

object AgentState {
    var serviceRunning by mutableStateOf(false)
    var fastTransferActive by mutableStateOf(false)
    var connectedClient by mutableStateOf<String?>(null)
    var pendingDesktopKey by mutableStateOf<String?>(null)
    var pendingRestore by mutableStateOf<RestoreApproval?>(null)
    var statusText by mutableStateOf("Agent stopped")
    var progress by mutableStateOf(0f)

    fun fingerprint(key: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(key.toByteArray())
        return digest.take(6).joinToString(":") { "%02X".format(it) }
    }

    private val approvedRestoreTokens = ConcurrentHashMap<String, Long>()
    private val rejectedRestoreTokens = ConcurrentHashMap<String, Long>()

    fun approveRestore() {
        val request = pendingRestore ?: return
        if (request.expiresAtMillis >= System.currentTimeMillis())
            approvedRestoreTokens[request.token] = request.expiresAtMillis
        pendingRestore = null
        statusText = "Restore confirmed"
    }

    fun rejectRestore() {
        pendingRestore?.let { rejectedRestoreTokens[it.token] = it.expiresAtMillis }
        pendingRestore = null
        statusText = "Restore rejected"
    }

    fun consumeRestoreApproval(token: String): Boolean {
        val expires = approvedRestoreTokens.remove(token) ?: return false
        return expires >= System.currentTimeMillis()
    }

    fun consumeRestoreRejection(token: String): Boolean {
        val expires = rejectedRestoreTokens.remove(token) ?: return false
        return expires >= System.currentTimeMillis()
    }
}
