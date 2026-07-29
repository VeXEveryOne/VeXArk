package com.vex.phonebackup.agent

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.SystemClock
import android.util.Base64
import android.util.Log
import org.json.JSONObject
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

object FastMediaSessionManager {
    private val gate = Any()
    private var active: Session? = null

    fun open(
        context: Context,
        desktopKey: String,
        encodedKey: String,
        requestedWorkers: Int
    ): JSONObject = synchronized(gate) {
        closeLocked()
        require(desktopKey.isNotBlank()) { "desktop key is required" }
        val sessionKey = Base64.decode(encodedKey, Base64.DEFAULT)
        require(sessionKey.size == FastMediaProtocol.SESSION_KEY_BYTES) {
            "fast media session key is invalid"
        }
        val address = wifiAddress(context)
        val workers = requestedWorkers.coerceIn(1, MAX_WORKERS)
        val server = ServerSocket()
        server.reuseAddress = false
        server.soTimeout = 1000
        server.bind(InetSocketAddress(address, 0), workers)
        val sessionId = ByteArray(FastMediaProtocol.SESSION_ID_BYTES)
            .also(SecureRandom()::nextBytes)
        val session = Session(
            context.applicationContext,
            desktopKey,
            sessionId,
            sessionKey,
            workers,
            server
        )
        active = session
        session.start()
        JSONObject()
            .put("sessionId", Base64.encodeToString(sessionId, Base64.NO_WRAP))
            .put("host", address.hostAddress)
            .put("port", server.localPort)
            .put("expiresAtUtcMillis", System.currentTimeMillis() + IDLE_TIMEOUT_MILLIS)
            .put("maxWorkers", workers)
    }

    fun close(desktopKey: String, sessionId: String?): Boolean = synchronized(gate) {
        val session = active ?: return false
        if (session.desktopKey != desktopKey ||
            (sessionId != null &&
                sessionId != Base64.encodeToString(session.sessionId, Base64.NO_WRAP))
        ) return false
        closeLocked()
        true
    }

    fun closeAll() = synchronized(gate) { closeLocked() }

    private fun closeLocked() {
        active?.close()
        active = null
    }

    private fun wifiAddress(context: Context): Inet4Address {
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val network = connectivity.activeNetwork ?: error("Wi-Fi is not connected")
        val capabilities = connectivity.getNetworkCapabilities(network)
            ?: error("Network capabilities are unavailable")
        require(capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
            "active network is not Wi-Fi"
        }
        val properties = connectivity.getLinkProperties(network)
            ?: error("Wi-Fi link properties are unavailable")
        return properties.linkAddresses
            .map { it.address }
            .filterIsInstance<Inet4Address>()
            .firstOrNull { !it.isLoopbackAddress && !it.isLinkLocalAddress && it.isSiteLocalAddress }
            ?: error("Wi-Fi has no private IPv4 address")
    }

    private class Session(
        val context: Context,
        val desktopKey: String,
        val sessionId: ByteArray,
        val sessionKey: ByteArray,
        val maxWorkers: Int,
        val server: ServerSocket
    ) {
        private val running = AtomicBoolean(true)
        private val lastActivity = AtomicLong(SystemClock.elapsedRealtime())
        private val workers = ConcurrentHashMap.newKeySet<Int>()
        private val sockets = ConcurrentHashMap.newKeySet<Socket>()
        private val acceptor = Executors.newSingleThreadExecutor()
        private val clients = Executors.newFixedThreadPool(maxWorkers)

        fun start() {
            AgentState.fastTransferActive = true
            acceptor.execute {
                try {
                    while (running.get()) {
                        if (SystemClock.elapsedRealtime() - lastActivity.get() > IDLE_TIMEOUT_MILLIS) {
                            close()
                            break
                        }
                        val socket = try {
                            server.accept()
                        } catch (_: SocketTimeoutException) {
                            continue
                        }
                        clients.execute {
                            runCatching { handle(socket) }
                                .onFailure { failure ->
                                    runCatching { socket.close() }
                                    if (running.get()) {
                                        Log.w(
                                            LOG_TAG,
                                            "Fast media worker closed after an error",
                                            failure
                                        )
                                    }
                                }
                        }
                    }
                } catch (failure: Exception) {
                    if (running.get() && !server.isClosed) {
                        Log.w(LOG_TAG, "Fast media listener stopped unexpectedly", failure)
                    }
                } finally {
                    synchronized(gate) {
                        if (active === this) active = null
                    }
                    close()
                }
            }
        }

        private fun handle(socket: Socket) {
            sockets.add(socket)
            socket.use {
                it.tcpNoDelay = true
                it.soTimeout = HANDSHAKE_TIMEOUT_MILLIS.toInt()
                val handshake = ByteArray(FastMediaProtocol.HANDSHAKE_BYTES)
                java.io.DataInputStream(it.getInputStream()).readFully(handshake)
                val workerId = FastMediaProtocol.verifyHandshake(
                    handshake,
                    sessionId,
                    sessionKey
                ) ?: error("fast media handshake failed")
                require(workerId in 0 until maxWorkers && workers.add(workerId)) {
                    "fast media worker is invalid or already connected"
                }
                it.soTimeout = IDLE_TIMEOUT_MILLIS.toInt()
                try {
                    val reader = FastRecordReader(
                        it.getInputStream(),
                        FastMediaProtocol.deriveKey(sessionKey, sessionId, workerId, true),
                        sessionId,
                        workerId,
                        true
                    )
                    val writer = FastRecordWriter(
                        it.getOutputStream(),
                        FastMediaProtocol.deriveKey(sessionKey, sessionId, workerId, false),
                        sessionId,
                        workerId,
                        false
                    )
                    while (running.get()) {
                        lastActivity.set(SystemClock.elapsedRealtime())
                        val request = reader.read()
                        when (request.type) {
                            FastRecordType.OPEN -> sendMedia(
                                writer,
                                JSONObject(request.payload.toString(Charsets.UTF_8))
                            )
                            FastRecordType.PROBE -> sendProbe(
                                writer,
                                JSONObject(request.payload.toString(Charsets.UTF_8))
                            )
                            else -> error("expected OPEN or PROBE record")
                        }
                    }
                } catch (_: java.io.EOFException) {
                    // Normal worker shutdown.
                } catch (_: SocketTimeoutException) {
                    // An idle worker is closed without keeping the LAN port alive.
                } finally {
                    workers.remove(workerId)
                    sockets.remove(socket)
                }
            }
        }

        private fun sendMedia(writer: FastRecordWriter, request: JSONObject) {
            runCatching {
                val result = MediaStoreAccess.readV2(
                    context,
                    request.getString("uri"),
                    request.optLong("offset", 0),
                    request.optLong("expectedSize", -1).takeIf { it >= 0 },
                    request.optLong("expectedModifiedUnixNanos", 0).takeIf { it > 0 }
                ) { buffer, count ->
                    lastActivity.set(SystemClock.elapsedRealtime())
                    writer.write(FastRecordType.DATA, buffer, count)
                }
                writer.write(
                    FastRecordType.END,
                    result.toJson().toString().toByteArray(Charsets.UTF_8)
                )
            }.onFailure { failure ->
                writer.write(
                    FastRecordType.ERROR,
                    JSONObject().put("message", failure.message.orEmpty())
                        .toString().toByteArray(Charsets.UTF_8)
                )
            }
        }

        private fun sendProbe(writer: FastRecordWriter, request: JSONObject) {
            runCatching {
                val result = MediaStoreAccess.probe(request.optLong("length", 0)) { buffer, count ->
                    lastActivity.set(SystemClock.elapsedRealtime())
                    writer.write(FastRecordType.DATA, buffer, count)
                }
                writer.write(
                    FastRecordType.END,
                    result.toJson().toString().toByteArray(Charsets.UTF_8)
                )
            }.onFailure { failure ->
                writer.write(
                    FastRecordType.ERROR,
                    JSONObject().put("message", failure.message.orEmpty())
                        .toString().toByteArray(Charsets.UTF_8)
                )
            }
        }

        fun close() {
            if (!running.compareAndSet(true, false)) return
            runCatching { server.close() }
            sockets.forEach { socket -> runCatching { socket.close() } }
            sockets.clear()
            clients.shutdownNow()
            acceptor.shutdownNow()
            sessionKey.fill(0)
            AgentState.fastTransferActive = false
        }
    }

    private fun MediaStoreAccess.ReadResult.toJson(): JSONObject = JSONObject()
        .put("sourceSize", sourceSize)
        .put("modifiedUnixNanos", modifiedUnixNanos)
        .put("acceptedOffset", acceptedOffset)
        .put("transferredBytes", transferredBytes)
        .put("sha256", sha256)

    private const val MAX_WORKERS = 4
    private const val HANDSHAKE_TIMEOUT_MILLIS = 5_000L
    private const val IDLE_TIMEOUT_MILLIS = 30_000L
    private const val LOG_TAG = "VeXArkFastMedia"
}
