package com.vex.phonebackup.agent

import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

enum class FrameType(val wireValue: Int) {
    COMMAND(1), RESPONSE(2), FILE_META(3), DATA(4), PROGRESS(5), END(6), ERROR(7);

    companion object {
        fun from(value: Int): FrameType =
            entries.firstOrNull { it.wireValue == value }
                ?: throw IllegalArgumentException("Unknown frame type $value")
    }
}

data class AgentFrame(val type: FrameType, val payload: ByteArray) {
    fun json(): JSONObject = JSONObject(payload.toString(Charsets.UTF_8))
}

object ProtocolFrameIo {
    const val MAX_JSON_BYTES = 1024 * 1024
    const val MAX_DATA_BYTES = 1024 * 1024

    fun read(input: InputStream): AgentFrame {
        val data = DataInputStream(input)
        val type = FrameType.from(data.readUnsignedByte())
        val length = data.readInt()
        val max = if (type == FrameType.DATA) MAX_DATA_BYTES else MAX_JSON_BYTES
        if (length < 0 || length > max) throw EOFException("Invalid frame length $length")
        return AgentFrame(type, ByteArray(length).also(data::readFully))
    }

    fun write(
        output: OutputStream,
        type: FrameType,
        payload: ByteArray,
        length: Int = payload.size
    ) {
        require(length in 0..payload.size)
        val data = DataOutputStream(output)
        data.writeByte(type.wireValue)
        data.writeInt(length)
        data.write(payload, 0, length)
    }

    fun writeJson(output: OutputStream, type: FrameType, payload: JSONObject) =
        write(output, type, payload.toString().toByteArray())
}
