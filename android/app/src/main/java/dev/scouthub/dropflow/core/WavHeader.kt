// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** The 44-byte RIFF/WAVE header for 16-bit PCM. */
object WavHeader {
    const val SIZE = 44
    private const val MAX_DATA = 0xFFFFFFFFL - 36

    fun build(dataBytes: Long, sampleRate: Int, channels: Int, bitsPerSample: Int = 16): ByteArray {
        require(dataBytes in 0..MAX_DATA) { "WAV cannot hold more than 4 GB" }
        require(sampleRate > 0 && channels in 1..8 && bitsPerSample in setOf(8, 16, 24, 32))
        val blockAlign = channels * bitsPerSample / 8
        return ByteBuffer.allocate(SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("RIFF".toByteArray(Charsets.US_ASCII))
            putInt((36 + dataBytes).toInt())
            put("WAVE".toByteArray(Charsets.US_ASCII))
            put("fmt ".toByteArray(Charsets.US_ASCII))
            putInt(16)
            putShort(1) // PCM
            putShort(channels.toShort())
            putInt(sampleRate)
            putInt(sampleRate * blockAlign)
            putShort(blockAlign.toShort())
            putShort(bitsPerSample.toShort())
            put("data".toByteArray(Charsets.US_ASCII))
            putInt(dataBytes.toInt())
        }.array()
    }

    /** 32-bit float PCM -> 16-bit PCM (little endian), clamped. */
    fun floatTo16(src: ByteBuffer, dst: ByteArray): Int {
        val floats = src.order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
        var o = 0
        while (floats.hasRemaining() && o + 1 < dst.size) {
            val s = (floats.get().coerceIn(-1f, 1f) * 32767f).toInt()
            dst[o++] = (s and 0xFF).toByte()
            dst[o++] = ((s shr 8) and 0xFF).toByte()
        }
        return o
    }
}
