// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.ImageDecoder
import androidx.exifinterface.media.ExifInterface
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.scouthub.dropflow.ops.ImageOps
import dev.scouthub.dropflow.ops.Qr
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ImageToolsTest {
    private val m = Matrix()
    private val s = Samples(m.context)

    private fun decode(f: File): Bitmap = ImageDecoder.decodeBitmap(ImageDecoder.createSource(f)) { d, _, _ -> d.allocator = ImageDecoder.ALLOCATOR_SOFTWARE }

    private fun isRed(c: Int) = Color.red(c) > 180 && Color.green(c) < 90 && Color.blue(c) < 90

    /** Displayed size of each sample (after EXIF orientation). */
    private data class Sample(val label: String, val file: File, val w: Int, val h: Int, val alpha: Boolean = false, val exif: Boolean = false)

    private fun samples(): List<Sample> = buildList {
        add(Sample("JPEG 400x300 EXIF orientation 6 + GPS + XMP", s.jpegWithExif(), 300, 400, exif = true))
        add(Sample("JPEG 400x300 without EXIF", s.jpegPlain(), 400, 300))
        add(Sample("PNG 320x240 transparent", s.pngAlpha(), 320, 240, alpha = true))
        add(Sample("WebP 256x256", s.webp(), 256, 256))
        s.heic()?.let { add(Sample("HEIC 512x384", it, 512, 384)) } ?: m.skip("HEIC", "*", "no HEVC encoder on this device")
        add(Sample("GIF 1x1", s.gif(), 1, 1))
        add(Sample("BMP 64x48", s.bmp(), 64, 48))
    }

    /** Size after DropFlow's 36 MP decoding cap. */
    private fun capped(smp: Sample): Pair<Int, Int> {
        val px = smp.w.toLong() * smp.h
        if (px <= 36_000_000L) return smp.w to smp.h
        val sc = Math.sqrt(36_000_000.0 / px)
        return Math.round(smp.w * sc).toInt() to Math.round(smp.h * sc).toInt()
    }

    private fun assertSize(expected: Pair<Int, Int>, img: Matrix.Img) = assertTrue(
        "~${expected.first}x${expected.second}, got ${img.width}x${img.height}",
        kotlin.math.abs(img.width - expected.first) <= 2 && kotlin.math.abs(img.height - expected.second) <= 2,
    )

    @Test
    fun convertTargets() {
        for (smp in samples()) {
            val input = m.input(smp.file)
            for (target in listOf("jpg", "png", "webp", "heic")) {
                if (target == "heic" && !ImageOps.heicEncoderAvailable) { m.skip(smp.label, "convert.image.heic", "no HEIC encoder"); continue }
                m.row(smp.label, "convert.image.$target") {
                    val out = m.ok(m.run("convert.image.$target", listOf(input))).files.single()
                    assertTrue(out.name, out.name.endsWith(" (converted).$target"))
                    val img = m.image(out.file)
                    assertEquals("size as shown", smp.w to smp.h, img.width to img.height)
                    val expectedMime = mapOf("jpg" to "image/jpeg", "png" to "image/png", "webp" to "image/webp", "heic" to "image/heif")[target]
                    assertEquals("real format", expectedMime, img.mime)
                    val exif = ExifInterface(out.file)
                    // 0 (undefined) and 1 (normal) both mean "show as stored": the turn is in the pixels, not repeated.
                    val orientation = exif.getAttributeInt(ExifInterface.TAG_ORIENTATION, 1)
                    assertTrue("orientation is baked into the pixels, not repeated (tag $orientation)", orientation == 0 || orientation == 1)
                    assertNull("no GPS copied", exif.latLong)
                    var extra = ""
                    if (smp.exif) {
                        val b = decode(out.file)
                        // Stored red top-left turns to top-right once the 90 degree orientation is applied.
                        assertTrue("red quadrant top-right after orientation", isRed(b.getPixel(b.width * 3 / 4, b.height / 8)))
                        if (target == "jpg") assertEquals("date kept", "2026:11:12 10:20:30", exif.getAttribute(ExifInterface.TAG_DATETIME_ORIGINAL))
                        extra = ", turned once, red top-right"
                    }
                    if (smp.alpha) {
                        val px = decode(out.file).getPixel(4, img.height - 4)
                        if (target == "jpg") assertTrue("transparent -> white in JPEG", Color.red(px) > 240 && Color.green(px) > 240)
                        if (target == "png" || target == "webp") assertEquals("transparency kept", 0, Color.alpha(px))
                        extra += ", alpha handled"
                    }
                    "${img.width}x${img.height} ${img.mime}, orientation 1, no GPS$extra"
                }
            }
            m.row(smp.label, "convert.image.pdf") {
                val out = m.ok(m.run("convert.image.pdf", listOf(input), mapOf("page_size" to "a4"))).files.single()
                assertEquals(1, m.pdfPages(out.file))
                "1-page PDF (${out.file.length() / 1024} KB)"
            }
        }
        m.finish()
    }

    @Test
    fun compressResizeRotate() {
        val all = samples() + Sample("JPEG 7000x5600 (39 MP)", s.hugeJpeg(), 7000, 5600)
        for (smp in all) {
            val input = m.input(smp.file)
            m.row(smp.label, "img.compress quality 75") {
                val r = m.run("img.compress", listOf(input), mapOf("compress_by" to "quality", "quality" to "75", "format" to "auto"))
                if (r.isFailure) return@row m.refused(r, "error_already_small")
                val out = r.getOrThrow().files.single()
                assertTrue("smaller (${out.file.length()} < ${smp.file.length()})", out.file.length() < smp.file.length())
                val img = m.image(out.file)
                assertSize(capped(smp), img)
                if (smp.alpha) assertEquals("transparent input -> WebP", "image/webp", img.mime)
                "${smp.file.length() / 1024} KB -> ${out.file.length() / 1024} KB ${img.mime}"
            }
            m.row(smp.label, "img.compress target 30 KB") {
                val r = m.run("img.compress", listOf(input), mapOf("compress_by" to "size", "target_kb" to "30", "format" to "jpg"))
                if (r.isFailure) return@row m.refused(r, if (smp.file.length() <= 30 * 1024) "error_already_small" else "error_target_unreachable")
                val out = r.getOrThrow().files.single()
                assertTrue("<= 30 KB (got ${out.file.length()})", out.file.length() <= 30 * 1024)
                "${out.file.length() / 1024} KB, ${m.image(out.file).let { "${it.width}x${it.height}" }}"
            }
            m.row(smp.label, "img.compress target 1 KB") {
                val r = m.run("img.compress", listOf(input), mapOf("compress_by" to "size", "target_kb" to "1", "format" to "jpg"))
                if (r.isSuccess) {
                    val out = r.getOrThrow().files.single()
                    assertTrue("never above the target (got ${out.file.length()})", out.file.length() <= 1024)
                    "${out.file.length()} B"
                } else {
                    m.refused(r, if (smp.file.length() <= 1024) "error_already_small" else "error_target_unreachable")
                }
            }
            m.row(smp.label, "img.resize long edge 1600") {
                val out = m.ok(m.run("img.resize", listOf(input), mapOf("resize_by" to "long_edge", "long_edge" to "1600"))).files.single()
                val img = m.image(out.file)
                val long = maxOf(smp.w, smp.h)
                if (long > 1600) assertEquals(1600, maxOf(img.width, img.height)) else assertEquals("never upscaled", smp.w to smp.h, img.width to img.height)
                "${img.width}x${img.height}"
            }
            m.row(smp.label, "img.resize 50%") {
                val out = m.ok(m.run("img.resize", listOf(input), mapOf("resize_by" to "percent", "percent" to "50"))).files.single()
                val img = m.image(out.file)
                // Images above 36 MP are scaled down while decoding first.
                val (ew, eh) = if (smp.w.toLong() * smp.h > 36_000_000L) {
                    val sc = Math.sqrt(36_000_000.0 / (smp.w.toLong() * smp.h)); (smp.w * sc * 0.5).toInt() to (smp.h * sc * 0.5).toInt()
                } else maxOf(1, smp.w / 2) to maxOf(1, smp.h / 2)
                assertTrue("~${ew}x$eh, got ${img.width}x${img.height}", kotlin.math.abs(img.width - ew) <= 2 && kotlin.math.abs(img.height - eh) <= 2)
                "${img.width}x${img.height}"
            }
            m.row(smp.label, "img.rotate 90") {
                val out = m.ok(m.run("img.rotate", listOf(input), mapOf("angle" to "90"))).files.single()
                val img = m.image(out.file)
                assertEquals("turned a quarter", smp.h to smp.w, img.width to img.height)
                if (smp.exif) {
                    assertEquals("lossless: only the tag turned (6 + 90 = 3)", ExifInterface.ORIENTATION_ROTATE_180, ExifInterface(out.file).getAttributeInt(ExifInterface.TAG_ORIENTATION, 1))
                    assertTrue("same picture bytes", kotlin.math.abs(smp.file.length() - out.file.length()) < 4096)
                }
                "${img.width}x${img.height}${if (smp.exif || smp.file.name.endsWith(".jpg")) " lossless" else ""}"
            }
        }
        m.finish()
    }

    @Test
    fun metadataPdfAndQr() {
        val all = samples()
        for (smp in all) {
            val input = m.input(smp.file)
            m.row(smp.label, "img.strip-gps location") {
                val out = m.ok(m.run("img.strip-gps", listOf(input), mapOf("strip" to "location"))).files.single()
                val exif = ExifInterface(out.file)
                assertNull("no GPS", exif.latLong)
                val xmp = exif.getAttributeBytes(ExifInterface.TAG_XMP)?.toString(Charsets.UTF_8).orEmpty()
                assertFalse("no GPS in XMP: $xmp", xmp.contains("GPS"))
                val img = m.image(out.file)
                assertEquals("still shown upright", smp.w to smp.h, img.width to img.height)
                if (smp.exif) assertEquals("date kept", "2026:11:12 10:20:30", exif.getAttribute(ExifInterface.TAG_DATETIME_ORIGINAL))
                "no GPS (EXIF+XMP), ${img.width}x${img.height}"
            }
            m.row(smp.label, "img.strip-gps all") {
                val out = m.ok(m.run("img.strip-gps", listOf(input), mapOf("strip" to "all"))).files.single()
                val exif = ExifInterface(out.file)
                assertNull(exif.latLong)
                assertNull("camera gone", exif.getAttribute(ExifInterface.TAG_MAKE))
                assertNull("date gone", exif.getAttribute(ExifInterface.TAG_DATETIME_ORIGINAL))
                val img = m.image(out.file)
                assertEquals("orientation kept, so not sideways", smp.w to smp.h, img.width to img.height)
                "nothing left, ${img.width}x${img.height}"
            }
            m.row(smp.label, "img.qr (read, no code)") { m.refused(m.run("img.qr", listOf(input)), "error_no_qr") }
        }
        m.row("all ${all.size} images", "img.to-pdf (one PDF)") {
            val out = m.ok(m.run("img.to-pdf", all.map { m.input(it.file) }, mapOf("page_size" to "image"))).files.single()
            assertEquals(all.size, m.pdfPages(out.file))
            "${all.size} pages"
        }
        m.row("QR PNG (URL + Arabic)", "img.qr") {
            val text = m.ok(m.run("img.qr", listOf(m.input(s.qrPng())))).text
            assertEquals(s.qrText, text)
            "read back: $text"
        }
        m.row("shared text (Arabic)", "qr.create") {
            val t = s.text("text.txt", "رابط: https://scouthub.dev")
            val out = m.ok(m.run("qr.create", listOf(m.input(t, "text/plain")), mapOf("qr_text" to "رابط: https://scouthub.dev"))).files.single()
            assertEquals("رابط: https://scouthub.dev", Qr.read(decode(out.file)))
            "PNG ${m.image(out.file).width}px, scans back to the same text"
        }
        m.finish()
    }
}
