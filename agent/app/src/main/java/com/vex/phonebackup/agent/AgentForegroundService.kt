package com.vex.phonebackup.agent

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import org.json.JSONObject
import org.json.JSONArray
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.UUID

class AgentForegroundService : Service() {
    private val running = AtomicBoolean(false)
    private val acceptor = Executors.newSingleThreadExecutor()
    private val clients = Executors.newCachedThreadPool()
    private var server: ServerSocket? = null

    override fun onCreate() {
        super.onCreate()
        startForeground(
            NOTIFICATION_ID,
            notification(uiText("Waiting for VeXArk on the PC", "Ожидание VeXArk на ПК"))
        )
        startServer()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startServer()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        running.set(false)
        runCatching { server?.close() }
        clients.shutdownNow()
        acceptor.shutdownNow()
        AgentState.serviceRunning = false
        AgentState.connectedClient = null
        AgentState.statusText = "Agent stopped"
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun startServer() {
        if (!running.compareAndSet(false, true)) return
        AgentState.serviceRunning = true
        AgentState.statusText = "Waiting for VeXArk Desktop"
        acceptor.execute {
            try {
                server = ServerSocket(PORT, 4, InetAddress.getLoopbackAddress())
                while (running.get()) {
                    val socket = server?.accept() ?: break
                    clients.execute {
                        runCatching { handleClient(socket) }
                            .onFailure {
                                AgentState.statusText = "Connection closed: ${it.message.orEmpty()}"
                            }
                    }
                }
            } catch (error: Exception) {
                if (running.get()) AgentState.statusText = "Agent error: ${error.message}"
            }
        }
    }

    private fun handleClient(socket: Socket) {
        socket.use {
            it.tcpNoDelay = true
            AgentState.connectedClient = it.inetAddress.hostAddress
            AgentState.statusText = "PC connected"
            while (running.get() && !it.isClosed) {
                val frame = runCatching { ProtocolFrameIo.read(it.getInputStream()) }.getOrNull() ?: break
                if (frame.type != FrameType.COMMAND) {
                    error(it, "expected_command", "Expected COMMAND frame")
                    continue
                }
                val request = try {
                    frame.json()
                } catch (failure: Exception) {
                    error(it, "invalid_json", failure.message.orEmpty())
                    continue
                }
                if (request.optString("command") in setOf(
                        "root_scan", "root_read", "shared_scan", "shared_read",
                        "personal_export", "root_restore", "shared_restore",
                        "media_scan", "media_read"
                    )) {
                    runCatching { dispatchStreaming(it, request) }
                        .onFailure { failure ->
                            AgentState.statusText = "Command error: ${failure.message.orEmpty()}"
                            runCatching {
                                error(it, "command_failed", failure.message.orEmpty())
                            }
                        }
                    continue
                }
                runCatching { dispatch(it, request) }
                    .onFailure { failure ->
                        AgentState.statusText = "Command error: ${failure.message.orEmpty()}"
                        runCatching {
                            error(it, "command_failed", failure.message.orEmpty())
                        }
                    }
            }
            RootHelper.cleanup(this)
            AgentState.connectedClient = null
            AgentState.statusText = "Waiting for VeXArk Desktop"
        }
    }

    private fun dispatch(socket: Socket, request: JSONObject) {
        val requestId = request.optString("requestId")
        val command = request.optString("command")
        val desktopKey = request.optString("desktopKey")
        val response = JSONObject()
            .put("protocolVersion", PROTOCOL_VERSION)
            .put("requestId", requestId)

        val authenticationError = RequestSecurity.verify(request)
        if (authenticationError != null) {
            response.put("ok", false).put("error", authenticationError)
            ProtocolFrameIo.writeJson(socket.getOutputStream(), FrameType.RESPONSE, response)
            return
        }

        when (command) {
            "hello" -> {
                response.put("ok", true)
                    .put("agentVersion", BuildConfig.VERSION_NAME)
                    .put("helperVersion", "rust-0.1.0")
                    .put("capabilities", JSONArray(listOf(
                        "inventory", "packages", "pairing", "no-root", "shared-storage",
                        "personal-data", "root-scan", "root-read", "root-restore",
                        "shared-restore", "media-export", "account-inventory",
                        "package-policy", "restore-approval"
                    )))
            }
            "pair" -> {
                if (desktopKey.isBlank()) {
                    response.put("ok", false).put("error", "desktop_key_required")
                } else if (isTrusted(desktopKey)) {
                    response.put("ok", true).put("paired", true)
                } else {
                    AgentState.pendingDesktopKey = desktopKey
                    response.put("ok", false)
                        .put("paired", false)
                        .put("approvalRequired", true)
                        .put("fingerprint", AgentState.fingerprint(desktopKey))
                }
            }
            "inventory" -> authorized(desktopKey, response) {
                response.put("ok", true).put("inventory", AgentInventory.device(this))
            }
            "packages" -> authorized(desktopKey, response) {
                val includeSystemApps = request.optJSONObject("payload")
                    ?.optBoolean("includeSystemApps", false) == true
                val requested = request.optJSONObject("payload")
                    ?.optJSONArray("packageNames")
                    ?.let { array ->
                        buildSet {
                            for (index in 0 until array.length()) add(array.optString(index))
                        }
                    }
                response.put("ok", true)
                    .put("packages", AgentInventory.packages(this, includeSystemApps, requested))
            }
            "root_request" -> authorized(desktopKey, response) {
                val root = RootCapabilities.probe(requestGrant = true)
                val helper = if (root.granted) RootHelper.probe(this) else null
                response.put("ok", true)
                    .put("available", root.available)
                    .put("granted", root.granted)
                    .put("provider", root.provider)
                    .put("detail", root.detail)
                    .put("helper", helper ?: JSONObject.NULL)
            }
            "shared_roots" -> authorized(desktopKey, response) {
                response.put("ok", true)
                    .put("roots", JSONArray(SharedStorageAccess.availableRoots()))
                    .put(
                        "accessGranted",
                        android.os.Build.VERSION.SDK_INT < 30 ||
                            android.os.Environment.isExternalStorageManager()
                    )
            }
            "personal_status" -> authorized(desktopKey, response) {
                response.put("ok", true).put("permissions", PersonalDataExporter.status(this))
            }
            "account_inventory" -> authorized(desktopKey, response) {
                response.put("ok", true).put("inventory", AccountInventory.export(this))
            }
            "media_status" -> authorized(desktopKey, response) {
                response.put("ok", true).put("permissions", MediaStoreAccess.status(this))
            }
            "system_state" -> authorized(desktopKey, response) {
                response.put("ok", true).put("state", SystemStateExporter.export(this))
            }
            "restore_prepare" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                val root = RootCapabilities.probe(requestGrant = true)
                val prepared = root.granted && AppSnapshotCoordinator.prepareRestore(packageName)
                response.put("ok", prepared)
                    .put("error", if (prepared) JSONObject.NULL else "restore_prepare_failed")
            }
            "restore_finish" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                val root = RootCapabilities.probe(requestGrant = true)
                val finished = root.granted && AppSnapshotCoordinator.finishRestore(packageName)
                response.put("ok", finished)
                    .put("error", if (finished) JSONObject.NULL else "restore_finish_failed")
            }
            "restore_policy" -> authorized(desktopKey, response) {
                val payload = request.optJSONObject("payload") ?: JSONObject()
                val root = RootCapabilities.probe(requestGrant = true)
                if (!root.granted) {
                    response.put("ok", false).put("error", "root_required")
                } else {
                    val result = AppSnapshotCoordinator.applyPolicy(
                        payload.optString("packageName"),
                        payload.optBoolean("enabled"),
                        payload.optBoolean("batteryOptimizationExempt"),
                        payload.optJSONArray("runtimePermissions") ?: JSONArray()
                    )
                    response.put("ok", result.optBoolean("ok"))
                        .put("failures", result.optJSONArray("failures") ?: JSONArray())
                }
            }
            "restore_system_state" -> authorized(desktopKey, response) {
                val payload = request.optJSONObject("payload") ?: JSONObject()
                val root = RootCapabilities.probe(requestGrant = true)
                if (!root.granted) {
                    response.put("ok", false).put("error", "root_required")
                } else {
                    val result = SystemStateExporter.restore(
                        this,
                        payload.optJSONObject("state") ?: JSONObject()
                    )
                    response.put("ok", result.optBoolean("ok"))
                        .put("failures", result.optJSONArray("failures") ?: JSONArray())
                }
            }
            "snapshot_begin" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                val root = RootCapabilities.probe(requestGrant = true)
                val started = root.granted && AppSnapshotCoordinator.begin(this, packageName)
                response.put("ok", started)
                    .put("error", if (started) JSONObject.NULL else "snapshot_begin_failed")
            }
            "snapshot_end" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                val ended = AppSnapshotCoordinator.end(packageName)
                response.put("ok", ended)
                    .put("error", if (ended) JSONObject.NULL else "snapshot_end_failed")
            }
            "request_restore" -> authorized(desktopKey, response) {
                val payload = request.optJSONObject("payload") ?: JSONObject()
                val token = UUID.randomUUID().toString()
                AgentState.pendingRestore = RestoreApproval(
                    token,
                    payload.optString("snapshotId"),
                    payload.optInt("itemCount"),
                    System.currentTimeMillis() + RESTORE_APPROVAL_MILLIS
                )
                AgentState.statusText = "Restore confirmation required"
                response.put("ok", true).put("approvalToken", token)
            }
            "restore_status" -> authorized(desktopKey, response) {
                val token = request.optJSONObject("payload")?.optString("approvalToken").orEmpty()
                response.put("ok", true)
                    .put("approved", AgentState.consumeRestoreApproval(token))
                    .put("rejected", AgentState.consumeRestoreRejection(token))
            }
            "ping" -> response.put("ok", true).put("pong", System.currentTimeMillis())
            else -> response.put("ok", false).put("error", "unknown_command")
        }
        ProtocolFrameIo.writeJson(socket.getOutputStream(), FrameType.RESPONSE, response)
    }

    private fun dispatchStreaming(socket: Socket, request: JSONObject) {
        val authenticationError = RequestSecurity.verify(request)
        if (authenticationError != null) {
            error(socket, authenticationError, "Signed request validation failed")
            return
        }
        val desktopKey = request.optString("desktopKey")
        if (!isTrusted(desktopKey)) {
            error(socket, "not_paired", "Desktop is not paired")
            return
        }
        val command = request.optString("command")
        if (command.startsWith("root_")) {
            val root = RootCapabilities.probe(requestGrant = true)
            if (!root.granted) {
                error(socket, "root_required", root.detail)
                return
            }
        }
        if (command == "root_restore" || command == "shared_restore") {
            ProtocolFrameIo.writeJson(
                socket.getOutputStream(),
                FrameType.RESPONSE,
                JSONObject().put("ok", true).put("ready", true)
            )
        }
        val payload = request.optJSONObject("payload") ?: JSONObject()
        val rootPath = payload.optString("root")
        val success = when (command) {
            "root_scan" -> RootHelper.scan(
                this,
                rootPath,
                payload.optBoolean("includeCaches"),
                payload.optBoolean("fullHash")
            ) { line ->
                ProtocolFrameIo.write(
                    socket.getOutputStream(),
                    FrameType.FILE_META,
                    line.toByteArray(Charsets.UTF_8)
                )
            }
            "root_read" -> RootHelper.read(
                this,
                rootPath,
                payload.optString("relative")
            ) { bytes ->
                ProtocolFrameIo.write(socket.getOutputStream(), FrameType.DATA, bytes)
            }
            "root_restore" -> {
                val kind = payload.optString("kind")
                if (kind == "directory") {
                    val frame = ProtocolFrameIo.read(socket.getInputStream())
                    if (frame.type != FrameType.END) {
                        throw IllegalArgumentException("Directory restore requires an empty stream")
                    }
                    RootHelper.restoreDirectory(
                        this,
                        rootPath,
                        payload.optString("relative"),
                        payload.optInt("mode"),
                        payload.optInt("uid"),
                        payload.optInt("gid"),
                        payload.optLong("modifiedUnixNanos"),
                        payload.optString("selinuxLabel").takeIf { it.isNotBlank() }
                    )
                } else if (kind == "file") {
                    RootHelper.restore(
                        this,
                        rootPath,
                        payload.optString("relative"),
                        payload.optInt("mode"),
                        payload.optInt("uid"),
                        payload.optInt("gid"),
                        payload.optLong("modifiedUnixNanos"),
                        payload.optString("selinuxLabel").takeIf { it.isNotBlank() }
                    ) { output ->
                        while (true) {
                            val frame = ProtocolFrameIo.read(socket.getInputStream())
                            when (frame.type) {
                                FrameType.DATA -> output.write(frame.payload)
                                FrameType.END -> break
                                else -> throw IllegalArgumentException(
                                    "Expected DATA or END during root restore"
                                )
                            }
                        }
                    }
                } else {
                    false
                }
            }
            "shared_scan" -> SharedStorageAccess.scan(rootPath) { line ->
                ProtocolFrameIo.write(
                    socket.getOutputStream(),
                    FrameType.FILE_META,
                    line.toByteArray(Charsets.UTF_8)
                )
            }
            "shared_read" -> SharedStorageAccess.read(
                rootPath,
                payload.optString("relative")
            ) { bytes ->
                ProtocolFrameIo.write(socket.getOutputStream(), FrameType.DATA, bytes)
            }
            "shared_restore" -> SharedStorageAccess.restore(
                rootPath,
                payload.optString("relative"),
                payload.optString("kind"),
                payload.optLong("modifiedUnixNanos")
            ) { output ->
                while (true) {
                    val frame = ProtocolFrameIo.read(socket.getInputStream())
                    when (frame.type) {
                        FrameType.DATA -> output.write(frame.payload)
                        FrameType.END -> break
                        else -> throw IllegalArgumentException(
                            "Expected DATA or END during shared restore"
                        )
                    }
                }
            }
            "personal_export" -> PersonalDataExporter.export(
                this,
                payload.optString("kind")
            ) { bytes ->
                ProtocolFrameIo.write(socket.getOutputStream(), FrameType.DATA, bytes)
            }
            "media_scan" -> MediaStoreAccess.scan(this) { line ->
                ProtocolFrameIo.write(
                    socket.getOutputStream(),
                    FrameType.FILE_META,
                    line.toByteArray(Charsets.UTF_8)
                )
            }
            "media_read" -> MediaStoreAccess.read(
                this,
                payload.optString("uri")
            ) { bytes ->
                ProtocolFrameIo.write(socket.getOutputStream(), FrameType.DATA, bytes)
            }
            else -> false
        }
        if (success) {
            ProtocolFrameIo.writeJson(
                socket.getOutputStream(),
                FrameType.END,
                JSONObject().put("ok", true)
            )
        } else {
            error(socket, "helper_failed", "Root helper command failed")
        }
    }

    private inline fun authorized(key: String, response: JSONObject, block: () -> Unit) {
        if (!isTrusted(key)) {
            response.put("ok", false).put("error", "not_paired")
        } else {
            block()
        }
    }

    private fun isTrusted(key: String): Boolean =
        key.isNotBlank() && getSharedPreferences(PREFS, MODE_PRIVATE)
            .getStringSet(KEY_TRUSTED, emptySet()).orEmpty().contains(key)

    private fun error(socket: Socket, code: String, message: String) {
        ProtocolFrameIo.writeJson(
            socket.getOutputStream(),
            FrameType.ERROR,
            JSONObject().put("error", code).put("message", message)
        )
    }

    private fun notification(text: String): Notification {
        val intent = Intent(this, MainActivity::class.java)
        val pending = PendingIntent.getActivity(
            this, 0, intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("VeXArk Agent")
            .setContentText(text)
            .setContentIntent(pending)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
    }

    private fun uiText(english: String, russian: String): String {
        val language = getSharedPreferences("vexark_ui", MODE_PRIVATE)
            .getString("language", "en")
        return if (language == "ru") russian else english
    }

    companion object {
        const val CHANNEL_ID = "phonebackup_operations"
        const val PORT = 49321
        const val PROTOCOL_VERSION = 1
        const val PREFS = "agent_security"
        const val KEY_TRUSTED = "trusted_desktop_keys"
        private const val NOTIFICATION_ID = 49321
        private const val RESTORE_APPROVAL_MILLIS = 120_000L
    }
}
