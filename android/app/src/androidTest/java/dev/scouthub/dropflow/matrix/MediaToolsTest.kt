// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import android.media.MediaFormat
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder

@RunWith(AndroidJUnit4::class)
class MediaToolsTest {
    private val m = Matrix()
    private val s = Samples(m.context)

    private data class Video(val label: String, val file: File, val w: Int, val h: Int, val audio: Boolean, val codec: String)

    private fun videos(): List<Video> = buildList {
        s.mp4("phone clip.mp4")?.let { add(Video("MP4 H.264+AAC 320x240 3 s", it, 320, 240, true, MediaFormat.MIMETYPE_VIDEO_AVC)) }
        s.mp4("portrait.mp4", rotation = 90)?.let { add(Video("MP4 H.264 rotated 90° (shown 240x320)", it, 240, 320, true, MediaFormat.MIMETYPE_VIDEO_AVC)) }
        s.mp4("hevc clip.mp4", MediaFormat.MIMETYPE_VIDEO_HEVC)?.let { add(Video("MP4 HEVC+AAC 320x240", it, 320, 240, true, MediaFormat.MIMETYPE_VIDEO_HEVC)) }
            ?: m.skip("MP4 HEVC", "*", "no HEVC encoder")
        s.mp4("camera 6 Mbps.mp4", w = 640, h = 480, bitrate = 6_000_000, noise = true)?.let {
            add(Video("MP4 H.264 640x480 6 Mbit/s (noisy, like a camera)", it, 640, 480, true, MediaFormat.MIMETYPE_VIDEO_AVC))
        }
        s.mp4("silent clip.mp4", withAudio = false)?.let { add(Video("MP4 H.264 without sound", it, 320, 240, false, MediaFormat.MIMETYPE_VIDEO_AVC)) }
        s.webm()?.let { add(Video("WebM VP8+Opus 320x240", it, 320, 240, true, MediaFormat.MIMETYPE_VIDEO_VP8)) } ?: m.skip("WebM", "*", "no VP8/Opus encoder")
    }

    private fun hevcFits(w: Int, h: Int) = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS).codecInfos
        .filter { it.isEncoder && it.supportedTypes.any { t -> t.equals(MediaFormat.MIMETYPE_VIDEO_HEVC, true) } }
        .any { val c = it.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_HEVC).videoCapabilities; c != null && (c.isSizeSupported(w, h) || c.isSizeSupported(h, w)) }

    /** Shown size: width/height swapped when the rotation tag is 90/270. */
    private fun shown(md: Matrix.Media): Pair<Int?, Int?> = if (md.rotation % 180 != 0) md.height to md.width else md.width to md.height

    private fun wavSeconds(f: File): Double = RandomAccessFile(f, "r").use { raf ->
        val h = ByteArray(44); raf.readFully(h)
        val b = ByteBuffer.wrap(h).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals("RIFF", String(h, 0, 4)); assertEquals("WAVE", String(h, 8, 4)); assertEquals(1, b.getShort(20).toInt())
        val rate = b.getInt(24); val ch = b.getShort(22).toInt(); val data = b.getInt(40).toLong()
        assertEquals("data size matches the file", f.length() - 44, data)
        data.toDouble() / (rate * ch * 2)
    }

    @Test
    fun videoTools() {
        for (v in videos()) {
            val input = m.input(v.file)
            for ((key, want) in listOf("convert.video.mp4" to MediaFormat.MIMETYPE_VIDEO_AVC, "convert.video.mp4-hevc" to MediaFormat.MIMETYPE_VIDEO_HEVC)) {
                m.row(v.label, key) {
                    val r = m.run(key, listOf(input))
                    if (r.isFailure && want == MediaFormat.MIMETYPE_VIDEO_HEVC && !hevcFits(v.w, v.h)) {
                        return@row m.refused(r, "error_hevc_size") + " (no HEVC encoder for ${v.w}x${v.h} here)"
                    }
                    val out = m.ok(r).files.single()
                    val md = m.media(out.file)
                    assertEquals("one video track", 1, md.video.size)
                    if (want == MediaFormat.MIMETYPE_VIDEO_AVC || s.hasEncoder(want)) assertEquals("codec", want, md.video.single())
                    assertEquals("sound kept", v.audio, md.audio.isNotEmpty())
                    m.assertNear("duration", 3000, md.durationMs, 250)
                    assertEquals("shown size", v.w to v.h, shown(md))
                    val path = if (v.codec == want && (!v.audio || v.codec != MediaFormat.MIMETYPE_VIDEO_VP8)) "stream copy" else "re-encoded"
                    "${md.video.single()} ${shown(md).first}x${shown(md).second} ${md.durationMs} ms, audio=${md.audio} ($path)"
                }
            }
            m.row(v.label, "convert.video.m4a") {
                val r = m.run("convert.video.m4a", listOf(input))
                if (!v.audio) return@row m.refused(r, "error_no_audio")
                val out = m.ok(r).files.single()
                val md = m.media(out.file)
                assertTrue("no video", md.video.isEmpty()); assertEquals(listOf(MediaFormat.MIMETYPE_AUDIO_AAC), md.audio)
                m.assertNear("duration", 3000, md.durationMs, 250)
                assertEquals("audio/mp4", out.mime)
                "AAC ${md.durationMs} ms"
            }
            m.row(v.label, "convert.video.wav") {
                val r = m.run("convert.video.wav", listOf(input))
                if (!v.audio) return@row m.refused(r, "error_no_audio")
                val out = m.ok(r).files.single()
                val sec = wavSeconds(out.file)
                assertTrue("~3 s, got $sec", sec in 2.8..3.3)
                "PCM 16-bit %.2f s".format(sec)
            }
            for (preset in listOf("small", "balanced")) {
                m.row(v.label, "vid.compress $preset") {
                    val r = m.run("vid.compress", listOf(input), mapOf("preset" to preset))
                    // A camera-like clip must really shrink; a clip that is already tiny may be refused as "already small".
                    if (r.isFailure && !v.label.contains("6 Mbit/s")) return@row m.refused(r, "error_already_small") + " (${v.file.length() / 1024} KB source)"
                    val out = m.ok(r).files.single()
                    val md = m.media(out.file)
                    assertEquals(listOf(MediaFormat.MIMETYPE_VIDEO_AVC), md.video)
                    m.assertNear("duration", 3000, md.durationMs, 250)
                    assertTrue("smaller than the original: ${v.file.length()} -> ${out.file.length()}", out.file.length() < v.file.length())
                    "${v.file.length() / 1024} KB -> ${out.file.length() / 1024} KB"
                }
            }
            m.row(v.label, "vid.trim 0.5-2.0 s") {
                val out = m.ok(m.run("vid.trim", listOf(input), mapOf("start_ms" to "500", "end_ms" to "2000"))).files.single()
                val md = m.media(out.file)
                assertEquals(1, md.video.size)
                m.assertNear("duration", 1500, md.durationMs, 250)
                assertEquals("shown size", v.w to v.h, shown(md))
                "${md.durationMs} ms"
            }
            m.row(v.label, "vid.trim end before start") {
                m.refused(m.run("vid.trim", listOf(input), mapOf("start_ms" to "2000", "end_ms" to "1000")), "error_range")
            }
            m.row(v.label, "vid.mute") {
                val out = m.ok(m.run("vid.mute", listOf(input))).files.single()
                val md = m.media(out.file)
                assertTrue("no sound", md.audio.isEmpty()); assertEquals(1, md.video.size)
                m.assertNear("duration", 3000, md.durationMs, 250)
                assertEquals("shown size", v.w to v.h, shown(md))
                "video only ${md.video.single()} ${md.durationMs} ms"
            }
            m.row(v.label, "vid.snapshot at 1 s") {
                val out = m.ok(m.run("vid.snapshot", listOf(input), mapOf("at_ms" to "1000"))).files.single()
                val img = m.image(out.file)
                assertEquals("image/png", img.mime)
                assertEquals("frame as shown", v.w to v.h, img.width to img.height)
                "${out.name} ${img.width}x${img.height}"
            }
        }
        m.finish()
    }

    @Test
    fun audioTools() {
        val audios = buildList {
            add("WAV 16-bit stereo 3 s" to s.wav())
            add("M4A AAC 4 s" to s.m4a())
            s.ogg()?.let { add("OGG Opus mono 3 s" to it) } ?: m.skip("OGG Opus", "*", "no Opus encoder")
            s.amr()?.let { add("3GP AMR-NB 3 s" to it) } ?: m.skip("3GP AMR", "*", "no AMR encoder")
        }
        for ((label, file) in audios) {
            val input = m.input(file)
            val seconds = if (label.contains("4 s")) 4000L else 3000L
            m.row(label, "convert.audio.m4a") {
                val out = m.ok(m.run("convert.audio.m4a", listOf(input))).files.single()
                val md = m.media(out.file)
                assertEquals(listOf(MediaFormat.MIMETYPE_AUDIO_AAC), md.audio)
                m.assertNear("duration", seconds, md.durationMs, 300)
                "AAC ${md.durationMs} ms"
            }
            m.row(label, "convert.audio.wav") {
                val out = m.ok(m.run("convert.audio.wav", listOf(input))).files.single()
                val sec = wavSeconds(out.file)
                assertTrue("~${seconds / 1000} s, got $sec", kotlin.math.abs(sec * 1000 - seconds) < 300)
                "PCM %.2f s".format(sec)
            }
            m.row(label, "aud.trim 1.0-2.5 s") {
                val out = m.ok(m.run("aud.trim", listOf(input), mapOf("start_ms" to "1000", "end_ms" to "2500"))).files.single()
                val md = m.media(out.file)
                assertEquals(listOf(MediaFormat.MIMETYPE_AUDIO_AAC), md.audio)
                m.assertNear("duration", 1500, md.durationMs, 250)
                assertTrue(out.name.endsWith("(trimmed).m4a"))
                "${md.durationMs} ms"
            }
        }
        m.row("text file named .mp3", "convert.audio.m4a") {
            m.refused(m.run("convert.audio.m4a", listOf(m.input(s.text("broken.mp3", "not audio")))), "error_media_read")
        }
        m.finish()
    }
}
