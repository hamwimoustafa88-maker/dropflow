// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import dev.scouthub.dropflow.core.MediaPlans.VideoCodec
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class SmallLogicTest {
    @Test
    fun `page ranges`() {
        assertEquals(listOf(1..10), PageRanges.parse("", 10))
        assertEquals(listOf(1..3, 5..5, 8..10), PageRanges.parse("1-3, 5, 8-", 10))
        assertEquals(listOf(1..2), PageRanges.parse("-2", 10))
        assertEquals(listOf(2..4, 6..6), PageRanges.parse("٢-٤، ٦", 10))
        assertEquals(listOf(1..3), PageRanges.parse("1–3", 10))
        for (bad in listOf("0", "11", "3-2", "a", "1-2-3", ",")) {
            assertThrows("'$bad'", PageRanges.InvalidRange::class.java) { PageRanges.parse(bad, 10) }
        }
        assertEquals(listOf(0, 1, 2, 4), PageRanges.pageIndexes(listOf(1..3, 2..3, 5..5)))
    }

    @Test
    fun `remux only when the codecs already fit`() {
        assertTrue(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/avc"), listOf("audio/mp4a-latm")))
        assertTrue(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/avc"), emptyList()))
        assertTrue(MediaPlans.canRemuxToMp4(VideoCodec.HEVC, listOf("video/hevc"), listOf("audio/mp4a-latm")))
        assertFalse(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/hevc"), listOf("audio/mp4a-latm")))
        assertFalse(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/avc"), listOf("audio/opus")))
        assertFalse(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/x-vnd.on2.vp9"), emptyList()))
        assertFalse(MediaPlans.canRemuxToMp4(VideoCodec.H264, emptyList(), listOf("audio/mp4a-latm")))
        assertFalse(MediaPlans.canRemuxToMp4(VideoCodec.H264, listOf("video/avc"), listOf("audio/mp4a-latm", "audio/mp4a-latm")))
        assertTrue(MediaPlans.canCopyAudioToM4a(listOf("audio/mp4a-latm")))
        assertFalse(MediaPlans.canCopyAudioToM4a(listOf("audio/mpeg")))
    }

    @Test
    fun `scaled video sizes never upscale and stay even`() {
        assertEquals(1280 to 720, MediaPlans.scaledSize(1920, 1080, 720))
        assertEquals(720 to 1280, MediaPlans.scaledSize(1080, 1920, 720))
        assertEquals(640 to 360, MediaPlans.scaledSize(640, 360, 720))
        assertEquals(1918 to 1080, MediaPlans.scaledSize(1919, 1081, null))
        val (w, h) = MediaPlans.scaledSize(1001, 1777, 720)
        assertEquals(0, w % 2); assertEquals(0, h % 2)
        assertEquals(720, w)
    }

    @Test
    fun `video presets`() {
        assertEquals(MediaPlans.VideoPreset.SMALL, MediaPlans.VideoPreset.of("small"))
        assertEquals(MediaPlans.VideoPreset.BALANCED, MediaPlans.VideoPreset.of(null))
        assertEquals((60 * (1_500_000 + 96_000) / 8).toLong(), MediaPlans.estimateBytes(60_000, MediaPlans.VideoPreset.SMALL))
    }

    @Test
    fun `wav header fields`() {
        val h = WavHeader.build(dataBytes = 1000, sampleRate = 44100, channels = 2)
        assertEquals(44, h.size)
        val b = ByteBuffer.wrap(h).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals("RIFF", String(h, 0, 4, Charsets.US_ASCII))
        assertEquals(1036, b.getInt(4))
        assertEquals("WAVE", String(h, 8, 4, Charsets.US_ASCII))
        assertEquals(1, b.getShort(20).toInt())
        assertEquals(2, b.getShort(22).toInt())
        assertEquals(44100, b.getInt(24))
        assertEquals(44100 * 4, b.getInt(28))
        assertEquals(4, b.getShort(32).toInt())
        assertEquals(16, b.getShort(34).toInt())
        assertEquals("data", String(h, 36, 4, Charsets.US_ASCII))
        assertEquals(1000, b.getInt(40))
        assertThrows(IllegalArgumentException::class.java) { WavHeader.build(5_000_000_000L, 44100, 2) }
    }

    @Test
    fun `float pcm becomes clamped 16-bit`() {
        val src = ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN)
        src.putFloat(0f).putFloat(1f).putFloat(-2f).flip()
        val dst = ByteArray(6)
        assertEquals(6, WavHeader.floatTo16(src, dst))
        assertArrayEquals(byteArrayOf(0, 0, 0xFF.toByte(), 0x7F, 0x01, 0x80.toByte()), dst)
    }

    @Test
    fun `file kinds from mime then extension`() {
        assertEquals(FileKind.IMAGE, FileKind.of("x.bin", "image/heic"))
        assertEquals(FileKind.PDF, FileKind.of("x", "application/pdf"))
        assertEquals(FileKind.VIDEO, FileKind.of("clip.MKV", null))
        assertEquals(FileKind.AUDIO, FileKind.of("song.opus", "application/octet-stream"))
        assertEquals(FileKind.ZIP, FileKind.of("a.zip", null))
        assertEquals(FileKind.TEXT, FileKind.of("note", "text/plain; charset=utf-8"))
        assertEquals(FileKind.OTHER, FileKind.of("drawing.svg", "image/svg+xml"))
        assertEquals("", FileKind.extensionOf("noext"))
        assertEquals("gz", FileKind.extensionOf("a.tar.GZ"))
        assertEquals("image/jpeg", FileKind.mimeFor("JPG"))
    }
}
