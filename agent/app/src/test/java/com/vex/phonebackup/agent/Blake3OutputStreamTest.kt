package com.vex.phonebackup.agent

import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.ByteArrayOutputStream

class Blake3OutputStreamTest {
    @Test
    fun `hashes the exact bytes written to the target`() {
        val target = ByteArrayOutputStream()
        val hashing = Blake3OutputStream(target)

        hashing.write("abc".toByteArray())
        hashing.flush()

        assertEquals("abc", target.toString(Charsets.UTF_8.name()))
        assertEquals(
            "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85",
            hashing.finalHex()
        )
    }
}
