package com.vex.phonebackup.agent

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

enum class FastRecordType(val value: Int) {
    OPEN(1), METADATA(2), DATA(3), END(4), ERROR(5), PROBE(6);

    companion object {
        fun from(value: Int): FastRecordType =
            entries.firstOrNull { it.value == value }
                ?: throw IllegalArgumentException("Unknown fast media record type $value")
    }
}

data class FastRecord(val type: FastRecordType, val payload: ByteArray)

object FastMediaProtocol {
    const val VERSION = 1
    const val SESSION_ID_BYTES = 16
    const val SESSION_KEY_BYTES = 32
    const val HANDSHAKE_BYTES = 4 + 1 + 1 + SESSION_ID_BYTES + 32
    const val MAX_PLAINTEXT_BYTES = 1024 * 1024
    private val MAGIC = "VXF1".toByteArray(StandardCharsets.US_ASCII)

    fun deriveKey(
        sessionKey: ByteArray,
        sessionId: ByteArray,
        workerId: Int,
        clientToServer: Boolean
    ): ByteArray {
        require(sessionKey.size == SESSION_KEY_BYTES)
        require(sessionId.size == SESSION_ID_BYTES)
        val info = "vexark-fast-media-v1/$workerId/${if (clientToServer) "c2s" else "s2c"}"
            .toByteArray(StandardCharsets.UTF_8)
        return hkdfSha256(sessionKey, sessionId, info)
    }

    fun verifyHandshake(
        handshake: ByteArray,
        expectedSessionId: ByteArray,
        sessionKey: ByteArray
    ): Int? {
        if (handshake.size != HANDSHAKE_BYTES ||
            !handshake.copyOfRange(0, 4).contentEquals(MAGIC) ||
            handshake[4].toInt() != VERSION ||
            !handshake.copyOfRange(6, 6 + SESSION_ID_BYTES).contentEquals(expectedSessionId)
        ) return null
        val workerId = handshake[5].toInt() and 0xff
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(sessionKey, "HmacSHA256"))
        val proof = mac.doFinal(handshake.copyOfRange(0, 6 + SESSION_ID_BYTES))
        return if (MessageDigest.isEqual(
                proof,
                handshake.copyOfRange(6 + SESSION_ID_BYTES, HANDSHAKE_BYTES)
            )
        ) workerId else null
    }

    private fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
        val extract = Mac.getInstance("HmacSHA256")
        extract.init(SecretKeySpec(salt, "HmacSHA256"))
        val pseudoRandomKey = extract.doFinal(ikm)
        val expand = Mac.getInstance("HmacSHA256")
        expand.init(SecretKeySpec(pseudoRandomKey, "HmacSHA256"))
        expand.update(info)
        expand.update(1)
        return expand.doFinal()
    }

    internal fun aad(
        sessionId: ByteArray,
        workerId: Int,
        clientToServer: Boolean,
        type: FastRecordType,
        counter: Long,
        plaintextLength: Int
    ): ByteArray = ByteBuffer.allocate(32)
        .put(VERSION.toByte())
        .put(sessionId)
        .put(workerId.toByte())
        .put(if (clientToServer) 1.toByte() else 2.toByte())
        .put(type.value.toByte())
        .putLong(counter)
        .putInt(plaintextLength)
        .array()

    internal fun nonce(counter: Long): ByteArray =
        ByteBuffer.allocate(12).putInt(0).putLong(counter).array()
}

class FastRecordReader(
    input: InputStream,
    key: ByteArray,
    private val sessionId: ByteArray,
    private val workerId: Int,
    private val clientToServer: Boolean
) {
    private val data = DataInputStream(input)
    private val keySpec = SecretKeySpec(key, "AES")
    private val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private var expectedCounter = 0L

    fun read(): FastRecord {
        val type = FastRecordType.from(data.readUnsignedByte())
        val encryptedLength = data.readInt()
        require(encryptedLength in 16..FastMediaProtocol.MAX_PLAINTEXT_BYTES + 16) {
            "Fast media record length is invalid"
        }
        val counter = data.readLong()
        require(counter == expectedCounter) { "Fast media record counter is invalid" }
        val encrypted = ByteArray(encryptedLength).also(data::readFully)
        val plaintextLength = encryptedLength - 16
        cipher.init(
            Cipher.DECRYPT_MODE,
            keySpec,
            GCMParameterSpec(128, FastMediaProtocol.nonce(counter))
        )
        cipher.updateAAD(
            FastMediaProtocol.aad(
                sessionId,
                workerId,
                clientToServer,
                type,
                counter,
                plaintextLength
            )
        )
        val plaintext = cipher.doFinal(encrypted)
        expectedCounter++
        return FastRecord(type, plaintext)
    }
}

class FastRecordWriter(
    output: OutputStream,
    key: ByteArray,
    private val sessionId: ByteArray,
    private val workerId: Int,
    private val clientToServer: Boolean
) {
    private val data = DataOutputStream(output)
    private val keySpec = SecretKeySpec(key, "AES")
    private val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private var counter = 0L

    @Synchronized
    fun write(type: FastRecordType, payload: ByteArray, length: Int = payload.size) {
        require(length in 0..minOf(payload.size, FastMediaProtocol.MAX_PLAINTEXT_BYTES))
        cipher.init(
            Cipher.ENCRYPT_MODE,
            keySpec,
            GCMParameterSpec(128, FastMediaProtocol.nonce(counter))
        )
        cipher.updateAAD(
            FastMediaProtocol.aad(
                sessionId,
                workerId,
                clientToServer,
                type,
                counter,
                length
            )
        )
        val encrypted = cipher.doFinal(payload, 0, length)
        data.writeByte(type.value)
        data.writeInt(encrypted.size)
        data.writeLong(counter)
        data.write(encrypted)
        counter++
    }
}
