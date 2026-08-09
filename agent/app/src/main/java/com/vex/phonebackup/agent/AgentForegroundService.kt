package com.vex.phonebackup.agent

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationCompat
import org.json.JSONObject
import org.json.JSONArray
import java.io.EOFException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.UUID

class AgentForegroundService : Service() {
    private val running = AtomicBoolean(false)
    private val acceptor = Executors.newSingleThreadExecutor()
    private val clients = Executors.newCachedThreadPool()
    private val restoreWatchdog = Executors.newSingleThreadScheduledExecutor()
    private var server: ServerSocket? = null

    override fun onCreate() {
        super.onCreate()
        RootHelper.cleanupStalePipes(this)
        AppSnapshotCoordinator.initialize(this)
        restoreWatchdog.scheduleWithFixedDelay(
            { AppSnapshotCoordinator.cleanupExpiredTransactions() },
            1,
            1,
            TimeUnit.MINUTES
        )
        AgentDiagnostics.log(
            level = "info",
            component = "service",
            eventCode = "create",
            message = "Foreground service created",
            fields = mapOf("port" to PORT, "channel" to BuildConfig.BUILD_CHANNEL)
        )
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
        AgentDiagnostics.log(
            level = "info",
            component = "service",
            eventCode = "destroy",
            message = "Foreground service is stopping"
        )
        running.set(false)
        runCatching { server?.close() }
        FastMediaSessionManager.closeAll()
        AppSnapshotCoordinator.abortAll()
        restoreWatchdog.shutdownNow()
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
                AgentDiagnostics.log(
                    level = "info",
                    component = "service",
                    eventCode = "listening",
                    message = "Agent protocol server is listening",
                    fields = mapOf("port" to PORT)
                )
                while (running.get()) {
                    val socket = server?.accept() ?: break
                    clients.execute {
                        runCatching { handleClient(socket) }
                            .onFailure { failure ->
                                AgentState.statusText =
                                    "Connection closed: ${failure.message.orEmpty()}"
                                AgentDiagnostics.log(
                                    level = "error",
                                    component = "protocol",
                                    eventCode = "client_failed",
                                    message = "Agent client loop failed",
                                    error = failure
                                )
                            }
                    }
                }
            } catch (error: Exception) {
                if (running.get()) AgentState.statusText = "Agent error: ${error.message}"
                AgentDiagnostics.log(
                    level = "error",
                    component = "service",
                    eventCode = "listen_failed",
                    message = "Agent protocol server failed",
                    error = error,
                    fields = mapOf("port" to PORT)
                )
            }
        }
    }

    private fun handleClient(socket: Socket) {
        socket.use {
            it.tcpNoDelay = true
            // Every streamed operation emits frames continuously. Expire abandoned ADB-forwarded
            // clients so a dead desktop process cannot retain a protocol worker forever.
            it.soTimeout = 120_000
            AgentState.connectedClient = it.inetAddress.hostAddress
            AgentState.statusText = "PC connected"
            AgentDiagnostics.log(
                level = "info",
                component = "protocol",
                eventCode = "client_connected",
                message = "Desktop connected to Agent"
            )
            while (running.get() && !it.isClosed) {
                val frame = try {
                    ProtocolFrameIo.read(it.getInputStream())
                } catch (_: EOFException) {
                    break
                } catch (failure: Exception) {
                    Log.w(LOG_TAG, "Failed to read protocol frame", failure)
                    AgentState.statusText = "Connection closed: invalid frame"
                    AgentDiagnostics.log(
                        level = "error",
                        component = "protocol",
                        eventCode = "frame_read_failed",
                        message = "Failed to read protocol frame",
                        error = failure
                    )
                    break
                }
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
                        "media_scan", "media_read", "media_read_v2", "media_probe",
                        "root_wallpaper_read", "wallpaper_restore", "personal_restore"
                    )) {
                    runCatching { dispatchStreaming(it, request) }
                        .onFailure { failure ->
                            AgentState.statusText = "Command error: ${failure.message.orEmpty()}"
                            AgentDiagnostics.log(
                                level = "error",
                                component = "protocol",
                                eventCode = "stream_command_failed",
                                message = "Streaming command failed",
                                requestId = request.optString("requestId"),
                                error = failure,
                                fields = mapOf("command" to request.optString("command"))
                            )
                            runCatching {
                                error(it, "command_failed", failure.message.orEmpty(),
                                    request.optString("requestId"))
                            }
                        }
                    continue
                }
                runCatching { dispatch(it, request) }
                    .onFailure { failure ->
                        AgentState.statusText = "Command error: ${failure.message.orEmpty()}"
                        AgentDiagnostics.log(
                            level = "error",
                            component = "protocol",
                            eventCode = "command_failed",
                            message = "Protocol command failed",
                            requestId = request.optString("requestId"),
                            error = failure,
                            fields = mapOf("command" to request.optString("command"))
                        )
                        runCatching {
                            error(it, "command_failed", failure.message.orEmpty(),
                                request.optString("requestId"))
                        }
                    }
            }
            AgentState.connectedClient = null
            AgentState.statusText = "Waiting for VeXArk Desktop"
            AgentDiagnostics.log(
                level = "info",
                component = "protocol",
                eventCode = "client_disconnected",
                message = "Desktop disconnected from Agent"
            )
        }
    }

    private fun dispatch(socket: Socket, request: JSONObject) {
        val startedAt = SystemClock.elapsedRealtime()
        val requestId = request.optString("requestId")
        val command = request.optString("command")
        val desktopKey = request.optString("desktopKey")
        val response = JSONObject()
            .put("protocolVersion", PROTOCOL_VERSION)
            .put("requestId", requestId)
        AgentDiagnostics.log(
            level = "info",
            component = "protocol",
            eventCode = "command_received",
            message = "Command received",
            requestId = requestId,
            fields = mapOf("command" to command)
        )

        val authenticationError = RequestSecurity.verify(request)
        if (authenticationError != null) {
            response.put("ok", false).put("error", authenticationError)
            ProtocolFrameIo.writeJson(socket.getOutputStream(), FrameType.RESPONSE, response)
            AgentDiagnostics.log(
                level = "warn",
                component = "protocol",
                eventCode = "authentication_failed",
                message = "Signed request validation failed",
                requestId = requestId,
                durationMs = SystemClock.elapsedRealtime() - startedAt,
                result = authenticationError,
                fields = mapOf("command" to command)
            )
            return
        }

        when (command) {
            "hello" -> {
                response.put("ok", true)
                    .put("agentVersion", BuildConfig.VERSION_NAME)
                    .put("helperVersion", "rust-0.2.0")
                    .put(
                        "build",
                        JSONObject()
                            .put("channel", BuildConfig.BUILD_CHANNEL)
                            .put("packageName", BuildConfig.APPLICATION_ID)
                            .put("versionName", BuildConfig.VERSION_NAME)
                            .put("versionCode", BuildConfig.VERSION_CODE)
                            .put("buildId", BuildConfig.BUILD_ID)
                            .put("agentPort", BuildConfig.AGENT_PORT)
                            .put("diagnosticsEnabled", BuildConfig.DIAGNOSTICS_ENABLED)
                    )
                    .put("capabilities", JSONArray(listOf(
                        "inventory", "packages", "pairing", "no-root", "shared-storage",
                        "personal-data", "root-scan", "root-read", "root-restore",
                        "shared-restore", "media-export", "account-inventory",
                        "package-policy", "restore-approval", "media-export-v2",
                        "fast-lan-aead-v1", "diagnostics-v1", "rom-identity-v1",
                        "settings-catalog-v2", "typed-data-roots-v1",
                        "transactional-app-restore-v1", "stream-request-id-v1",
                        "wallpaper-api-restore-v1"
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
                val hashArtifacts = request.optJSONObject("payload")
                    ?.optBoolean("hashArtifacts", true) != false
                val requested = request.optJSONObject("payload")
                    ?.optJSONArray("packageNames")
                    ?.let { array ->
                        buildSet {
                            for (index in 0 until array.length()) add(array.optString(index))
                        }
                    }
                response.put("ok", true)
                    .put("packages", AgentInventory.packages(
                        this, includeSystemApps, requested, hashArtifacts))
            }
            "root_request" -> authorized(desktopKey, response) {
                val root = RootCapabilities.probe(requestGrant = true)
                val helper = if (root.granted) RootHelper.probe(this) else null
                response.put("ok", true)
                    .put("available", root.available)
                    .put("granted", root.granted)
                    .put("provider", root.provider)
                    .put("detail", root.detail)
                    .put("diagnostics", root.diagnostics?.toJson() ?: JSONObject.NULL)
                    .put("helper", helper ?: JSONObject.NULL)
            }
            "diagnostics_snapshot" -> authorized(desktopKey, response) {
                response.put("ok", true)
                    .put("snapshot", AgentDiagnostics.snapshotJson())
            }
            "diagnostics_clear" -> authorized(desktopKey, response) {
                AgentDiagnostics.clear()
                response.put("ok", true)
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
            "media_session_open" -> authorized(desktopKey, response) {
                val payload = request.optJSONObject("payload") ?: JSONObject()
                val session = FastMediaSessionManager.open(
                    this,
                    desktopKey,
                    payload.optString("sessionKey"),
                    payload.optInt("workers", 4)
                )
                response.put("ok", true).put("session", session)
                AgentState.statusText = "Fast Wi-Fi transfer ready"
            }
            "media_session_close" -> authorized(desktopKey, response) {
                val sessionId = request.optJSONObject("payload")
                    ?.optString("sessionId")
                    ?.takeIf { it.isNotBlank() }
                response.put(
                    "ok",
                    FastMediaSessionManager.close(desktopKey, sessionId)
                )
            }
            "system_state" -> authorized(desktopKey, response) {
                response.put("ok", true).put("state", SystemStateExporter.export(this))
            }
            "restore_prepare" -> authorized(desktopKey, response) {
                val payload = request.optJSONObject("payload") ?: JSONObject()
                val packageName = payload.optString("packageName")
                val roots = payload.optJSONArray("roots")?.let { array ->
                    (0 until array.length()).map { array.optString(it) }.filter(String::isNotBlank)
                }.orEmpty()
                val root = RootCapabilities.probe(requestGrant = true)
                val prepared = if (root.granted) AppSnapshotCoordinator.prepareRestore(
                    this, packageName, roots, payload.optBoolean("completeSnapshot", false)
                ) else JSONObject().put("ok", false).put("error", "root_required")
                response.put("ok", prepared.optBoolean("ok"))
                    .put("transactionId", prepared.opt("transactionId") ?: JSONObject.NULL)
                    .put("rootCount", prepared.optInt("rootCount", 0))
                    .put("error", prepared.opt("error") ?: JSONObject.NULL)
            }
            "restore_finish" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                val root = RootCapabilities.probe(requestGrant = true)
                val finished = if (root.granted) AppSnapshotCoordinator.finishRestore(packageName)
                    else JSONObject().put("ok", false).put("error", "root_required")
                response.put("ok", finished.optBoolean("ok"))
                    .put("rolledBack", finished.optBoolean("rolledBack", false))
                    .put("error", finished.opt("error") ?: JSONObject.NULL)
            }
            "restore_abort" -> authorized(desktopKey, response) {
                val packageName = request.optJSONObject("payload")?.optString("packageName").orEmpty()
                response.put("ok", AppSnapshotCoordinator.abortRestore(packageName))
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
                        payload.optJSONArray("runtimePermissions") ?: JSONArray(),
                        payload.optString("appLocales")
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
                    val confirmed = payload.optJSONArray("confirmedSettingIds")?.let { array ->
                        buildSet { for (index in 0 until array.length()) add(array.optString(index)) }
                    }.orEmpty()
                    val result = SystemStateExporter.restore(
                        this,
                        payload.optJSONObject("state") ?: JSONObject(),
                        confirmed
                    )
                    response.put("ok", true)
                        .put("appliedWithoutFailures", result.optBoolean("ok"))
                        .put("failures", result.optJSONArray("failures") ?: JSONArray())
                        .put("needsConfirmation", result.optJSONArray("needsConfirmation") ?: JSONArray())
                        .put("outcomes", result.optJSONArray("outcomes") ?: JSONArray())
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
        val ok = response.optBoolean("ok", false)
        AgentDiagnostics.log(
            level = if (ok) "info" else "warn",
            component = "protocol",
            eventCode = "command_completed",
            message = "Command completed",
            requestId = requestId,
            durationMs = SystemClock.elapsedRealtime() - startedAt,
            result = if (ok) "ok" else response.optString("error", "failed"),
            fields = mapOf("command" to command)
        )
    }

    private fun dispatchStreaming(socket: Socket, request: JSONObject) {
        val startedAt = SystemClock.elapsedRealtime()
        val requestId = request.optString("requestId")
        val command = request.optString("command")
        AgentDiagnostics.log(
            level = "info",
            component = "protocol",
            eventCode = "stream_started",
            message = "Streaming command started",
            requestId = requestId,
            fields = mapOf("command" to command)
        )
        val authenticationError = RequestSecurity.verify(request)
        if (authenticationError != null) {
            error(socket, authenticationError, "Signed request validation failed", requestId)
            AgentDiagnostics.log(
                level = "warn",
                component = "protocol",
                eventCode = "stream_authentication_failed",
                message = "Streaming request signature validation failed",
                requestId = requestId,
                durationMs = SystemClock.elapsedRealtime() - startedAt,
                result = authenticationError,
                fields = mapOf("command" to command)
            )
            return
        }
        val desktopKey = request.optString("desktopKey")
        if (!isTrusted(desktopKey)) {
            error(socket, "not_paired", "Desktop is not paired", requestId)
            AgentDiagnostics.log(
                level = "warn",
                component = "protocol",
                eventCode = "stream_not_paired",
                message = "Streaming request rejected because Desktop is not paired",
                requestId = requestId,
                durationMs = SystemClock.elapsedRealtime() - startedAt,
                result = "not_paired",
                fields = mapOf("command" to command)
            )
            return
        }
        if (command.startsWith("root_")) {
            val root = if (RootCapabilities.hasCachedRootShell()) {
                null
            } else {
                RootCapabilities.probe(requestGrant = true)
            }
            if (root?.granted == false) {
                error(socket, "root_required", root.detail, requestId)
                AgentDiagnostics.log(
                    level = "warn",
                    component = "protocol",
                    eventCode = "stream_root_denied",
                    message = root.detail,
                    requestId = requestId,
                    durationMs = SystemClock.elapsedRealtime() - startedAt,
                    result = "root_required",
                    fields = mapOf("command" to command)
                )
                return
            }
        }
        if (command in setOf(
                "root_restore", "shared_restore", "wallpaper_restore", "personal_restore")) {
            ProtocolFrameIo.writeJson(
                socket.getOutputStream(),
                FrameType.RESPONSE,
                JSONObject()
                    .put("protocolVersion", PROTOCOL_VERSION)
                    .put("requestId", requestId)
                    .put("ok", true)
                    .put("ready", true)
            )
        }
        val payload = request.optJSONObject("payload") ?: JSONObject()
        var inboundTerminalConsumed = false
        val requestedRootPath = payload.optString("root")
        val rootPath = if (command == "root_restore") {
            AppSnapshotCoordinator.resolveRestoreRoot(
                payload.optString("packageName"), requestedRootPath
            ) ?: requestedRootPath
        } else requestedRootPath
        if (command == "personal_restore") {
            val result = PersonalDataImporter.restore(this, payload.optString("kind")) { output ->
                while (true) {
                    val frame = ProtocolFrameIo.read(socket.getInputStream())
                    when (frame.type) {
                        FrameType.DATA -> output.write(frame.payload)
                        FrameType.END -> {
                            inboundTerminalConsumed = true
                            break
                        }
                        FrameType.ABORT -> {
                            inboundTerminalConsumed = true
                            throw IllegalStateException("stream_aborted")
                        }
                        else -> throw IllegalArgumentException(
                            "Expected DATA, END or ABORT during personal restore")
                    }
                }
            }
            ProtocolFrameIo.writeJson(socket.getOutputStream(), FrameType.END, result)
            return
        }
        if (command == "media_read_v2" || command == "media_probe") {
            val result = if (command == "media_probe") {
                MediaStoreAccess.probe(payload.optLong("length", 0)) { buffer, count ->
                    ProtocolFrameIo.write(
                        socket.getOutputStream(),
                        FrameType.DATA,
                        buffer,
                        count
                    )
                }
            } else {
                MediaStoreAccess.readV2(
                    this,
                    payload.optString("uri"),
                    payload.optLong("offset", 0),
                    payload.optLong("expectedSize", -1).takeIf { it >= 0 },
                    payload.optLong("expectedModifiedUnixNanos", 0).takeIf { it > 0 }
                ) { buffer, count ->
                    ProtocolFrameIo.write(
                        socket.getOutputStream(),
                        FrameType.DATA,
                        buffer,
                        count
                    )
                }
            }
            ProtocolFrameIo.writeJson(
                socket.getOutputStream(),
                FrameType.END,
                JSONObject()
                    .put("ok", true)
                    .put("sourceSize", result.sourceSize)
                    .put("modifiedUnixNanos", result.modifiedUnixNanos)
                    .put("acceptedOffset", result.acceptedOffset)
                    .put("transferredBytes", result.transferredBytes)
                    .put("sha256", result.sha256)
            )
            AgentDiagnostics.log(
                level = "info",
                component = "protocol",
                eventCode = "stream_completed",
                message = "Streaming command completed",
                requestId = requestId,
                durationMs = SystemClock.elapsedRealtime() - startedAt,
                result = "ok",
                fields = mapOf(
                    "command" to command,
                    "transferredBytes" to result.transferredBytes
                )
            )
            return
        }
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
            "root_wallpaper_read" -> RootHelper.readWallpaper(
                this,
                payload.optString("kind")
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
                    inboundTerminalConsumed = true
                    RootHelper.restoreDirectory(
                        this,
                        rootPath,
                        payload.optString("relative"),
                        payload.optInt("mode"),
                        payload.optInt("uid"),
                        payload.optInt("gid"),
                        payload.optLong("modifiedUnixNanos"),
                        payloadNullableString(payload, "selinuxLabel")
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
                        payload.optLong("expectedSize", -1),
                        payloadNullableString(payload, "expectedBlake3"),
                        payloadNullableString(payload, "selinuxLabel")
                    ) { output ->
                        while (true) {
                            val frame = ProtocolFrameIo.read(socket.getInputStream())
                            when (frame.type) {
                                FrameType.DATA -> output.write(frame.payload)
                                FrameType.END -> {
                                    inboundTerminalConsumed = true
                                    break
                                }
                                FrameType.ABORT -> {
                                    inboundTerminalConsumed = true
                                    throw IllegalStateException("stream_aborted")
                                }
                                else -> throw IllegalArgumentException(
                                    "Expected DATA, END or ABORT during root restore"
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
                payload.optLong("modifiedUnixNanos"),
                payload.optLong("expectedSize", -1),
                payloadNullableString(payload, "expectedBlake3")
            ) { output ->
                while (true) {
                    val frame = ProtocolFrameIo.read(socket.getInputStream())
                    when (frame.type) {
                        FrameType.DATA -> output.write(frame.payload)
                        FrameType.END -> {
                            inboundTerminalConsumed = true
                            break
                        }
                        FrameType.ABORT -> {
                            inboundTerminalConsumed = true
                            throw IllegalStateException("stream_aborted")
                        }
                        else -> throw IllegalArgumentException(
                            "Expected DATA, END or ABORT during shared restore"
                        )
                    }
                }
            }
            "wallpaper_restore" -> WallpaperAccess.restore(
                this,
                payload.optString("kind"),
                payload.optLong("expectedSize", -1),
                payloadNullableString(payload, "expectedBlake3")
            ) { output ->
                while (true) {
                    val frame = ProtocolFrameIo.read(socket.getInputStream())
                    when (frame.type) {
                        FrameType.DATA -> output.write(frame.payload)
                        FrameType.END -> {
                            inboundTerminalConsumed = true
                            break
                        }
                        FrameType.ABORT -> {
                            inboundTerminalConsumed = true
                            throw IllegalStateException("stream_aborted")
                        }
                        else -> throw IllegalArgumentException(
                            "Expected DATA, END or ABORT during wallpaper restore"
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
            if (command in setOf(
                    "root_restore", "shared_restore", "wallpaper_restore") &&
                !inboundTerminalConsumed
            ) {
                drainInboundRestore(socket)
            }
            error(socket, "helper_failed", "Root helper command failed", requestId)
        }
        AgentDiagnostics.log(
            level = if (success) "info" else "warn",
            component = "protocol",
            eventCode = "stream_completed",
            message = "Streaming command completed",
            requestId = requestId,
            durationMs = SystemClock.elapsedRealtime() - startedAt,
            result = if (success) "ok" else "helper_failed",
            fields = mapOf("command" to command)
        )
    }

    private fun drainInboundRestore(socket: Socket) {
        while (true) {
            when (ProtocolFrameIo.read(socket.getInputStream()).type) {
                FrameType.DATA -> Unit
                FrameType.END, FrameType.ABORT -> return
                else -> throw IllegalArgumentException(
                    "Expected DATA, END or ABORT while draining failed restore")
            }
        }
    }

    private fun payloadNullableString(payload: JSONObject, name: String): String? =
        payload.optString(name).takeIf {
            it.isNotBlank() && !it.equals("null", ignoreCase = true)
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

    private fun error(socket: Socket, code: String, message: String, requestId: String = "") {
        ProtocolFrameIo.writeJson(
            socket.getOutputStream(),
            FrameType.ERROR,
            JSONObject()
                .put("protocolVersion", PROTOCOL_VERSION)
                .put("requestId", requestId)
                .put("error", code)
                .put("message", message)
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
        val PORT: Int = BuildConfig.AGENT_PORT
        const val PROTOCOL_VERSION = 2
        const val PREFS = "agent_security"
        const val KEY_TRUSTED = "trusted_desktop_keys"
        private val NOTIFICATION_ID: Int = BuildConfig.AGENT_PORT
        private const val RESTORE_APPROVAL_MILLIS = 120_000L
        private const val LOG_TAG = "VeXArkAgent"
    }
}
