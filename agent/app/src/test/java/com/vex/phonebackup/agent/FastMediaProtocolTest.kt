package com.vex.phonebackup.agent

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream

class FastMediaProtocolTest {
    @Test
    fun matchesDesktopProtocolVector() {
        val sessionKey = ByteArray(32) { it.toByte() }
        val sessionId = ByteArray(16) { (0xa0 + it).toByte() }
        val expectedKey =
            "8d1fa2f3e4fc7d4358446cb51bd6b4c2461435ad9026bbc58c2a3b176069d973".hex()
        val handshake =
            ("565846310102a0a1a2a3a4a5a6a7a8a9aaabacadaeaf" +
                "bffd3fdd126c517fd2a012fffa5e702d0ff9bb3c73b377e7f647585eafa39777").hex()
        val expectedRecord =
            ("03000000210000000000000000" +
                "418d3c3d54a6881e9cb79ed40338a3ad43f34d2cae2c3be581f97a3d1bb1991b16").hex()

        val key = FastMediaProtocol.deriveKey(sessionKey, sessionId, 2, true)
        assertArrayEquals(expectedKey, key)
        val verifiedWorker =
            FastMediaProtocol.verifyHandshake(handshake, sessionId, sessionKey)
        assertNotNull(verifiedWorker)
        assertEquals(2, verifiedWorker)

        val output = ByteArrayOutputStream()
        FastRecordWriter(output, key, sessionId, 2, true).write(
            FastRecordType.DATA,
            "VeXArk fast media".toByteArray()
        )
        assertArrayEquals(expectedRecord, output.toByteArray())

        val record = FastRecordReader(
            ByteArrayInputStream(expectedRecord),
            key,
            sessionId,
            2,
            true
        ).read()
        assertEquals(FastRecordType.DATA, record.type)
        assertEquals("VeXArk fast media", record.payload.toString(Charsets.UTF_8))
    }

    private fun String.hex(): ByteArray {
        require(length % 2 == 0)
        return chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }
}
