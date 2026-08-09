package com.vex.phonebackup.agent

import org.bouncycastle.crypto.digests.Blake3Digest
import java.io.FilterOutputStream
import java.io.OutputStream

internal class Blake3OutputStream(output: OutputStream) : FilterOutputStream(output) {
    private val digest = Blake3Digest(256)
    private var finalized = false

    override fun write(value: Int) {
        digest.update(value.toByte())
        out.write(value)
    }

    override fun write(buffer: ByteArray, offset: Int, length: Int) {
        digest.update(buffer, offset, length)
        out.write(buffer, offset, length)
    }

    fun finalHex(): String {
        check(!finalized) { "BLAKE3 digest already finalized" }
        finalized = true
        val result = ByteArray(32)
        digest.doFinal(result, 0)
        return result.joinToString("") { "%02x".format(it) }
    }
}
