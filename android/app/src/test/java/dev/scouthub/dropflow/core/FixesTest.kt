// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.zip.ZipException

/** Rules behind the bug fixes of the 2026-11 Android review (one test per rule, each failed before its fix). */
class FixesTest {
    // ---- content:// URIs without a usable file name (MediaStore ids, "image:34", dotted dates) ----

    @Test
    fun `mime types map back to the extension DropFlow writes`() {
        assertEquals("jpg", FileKind.extensionForMime("image/jpeg"))
        assertEquals("png", FileKind.extensionForMime("IMAGE/PNG"))
        assertEquals("heic", FileKind.extensionForMime("image/heic"))
        assertEquals("heif", FileKind.extensionForMime("image/heif"))
        assertEquals("pdf", FileKind.extensionForMime("application/pdf"))
        assertEquals("zip", FileKind.extensionForMime("application/x-zip-compressed"))
        assertEquals("mp4", FileKind.extensionForMime("video/mp4"))
        assertEquals("m4a", FileKind.extensionForMime("audio/mp4"))
        assertEquals("m4a", FileKind.extensionForMime("audio/x-m4a"))
        assertEquals("wav", FileKind.extensionForMime("audio/x-wav"))
        assertEquals("mp3", FileKind.extensionForMime("audio/mpeg"))
        assertEquals("txt", FileKind.extensionForMime("text/plain; charset=utf-8"))
        assertNull(FileKind.extensionForMime("application/octet-stream"))
        assertNull(FileKind.extensionForMime(null))
        // Every extension DropFlow writes maps back to itself (or its short form).
        for (ext in listOf("jpg", "png", "webp", "heic", "pdf", "zip", "txt", "mp4", "m4a", "wav", "mp3", "flac", "ogg", "webm", "mkv")) {
            assertEquals(ext, FileKind.extensionForMime(FileKind.mimeFor(ext)))
        }
    }

    @Test
    fun `a shared name without an extension gets one from its type`() {
        assertEquals("1000000034.jpg", OutputNaming.withTypeExtension("1000000034", "image/jpeg"))
        assertEquals("image_34.png", OutputNaming.withTypeExtension("image_34", "image/png"))
        // "Screenshot_2024.10.12" has a dot but no real extension.
        assertEquals("Screenshot_2024.10.12.png", OutputNaming.withTypeExtension("Screenshot_2024.10.12", "image/png"))
        assertEquals("doc.pdf", OutputNaming.withTypeExtension("doc", "application/pdf"))
        // Names that already carry an extension of the same kind are left alone, even when the exact type differs.
        assertEquals("photo.jpeg", OutputNaming.withTypeExtension("photo.jpeg", "image/jpeg"))
        assertEquals("photo.png", OutputNaming.withTypeExtension("photo.png", "image/jpeg"))
        assertEquals("song.opus", OutputNaming.withTypeExtension("song.opus", "audio/ogg"))
        assertEquals("data.csv", OutputNaming.withTypeExtension("data.csv", "text/plain"))
        assertEquals("IMG at 5.30 PM.jpg", OutputNaming.withTypeExtension("IMG at 5.30 PM", "image/jpeg"))
        // Unknown types change nothing.
        assertEquals("README", OutputNaming.withTypeExtension("README", "application/octet-stream"))
        assertEquals("README", OutputNaming.withTypeExtension("README", null))
        // The new name is still recognised by kind (this is what broke lossless JPEG rotation and zip names).
        assertEquals("jpg", FileKind.extensionOf(OutputNaming.withTypeExtension("1000000034", "image/jpeg")))
    }

    // ---- PDF to text on a scanned PDF wrote an empty .txt ----

    @Test
    fun `a text layer with only spacing counts as no text`() {
        assertFalse(TextLayer.hasText(""))
        assertFalse(TextLayer.hasText("\n\n\u000c\r\n \t \n"))
        assertTrue(TextLayer.hasText("مرحبا"))
        assertTrue(TextLayer.hasText("\n 7 \n"))
        assertTrue(TextLayer.hasText("Hello"))
    }

    // ---- image compress: never above the target, never bigger than the original ----

    @Test
    fun `target size shrinks the picture until it fits`() {
        // Size grows with quality and shrinks by 0.75^2 per step (the picture gets 0.75 x smaller each side).
        fun size(step: Int, q: Int): Long = ((20_000L + q * 2_000L) * Math.pow(0.5625, step.toDouble())).toLong()
        val r = TargetSize.searchShrinking(10_000L, maxSteps = 8, encodeAt = { s, q -> s to q }, sizeOf = { size(it.first, it.second) })
        assertTrue(r.fits)
        assertTrue(r.size <= 10_000L)
        assertTrue("needed a smaller picture", r.value.first > 0)
    }

    @Test
    fun `an unreachable target is reported, not returned as if it fitted`() {
        val r = TargetSize.searchShrinking(10L, maxSteps = 8, encodeAt = { s, q -> s to q }, sizeOf = { 5_000L })
        assertFalse(r.fits)
        assertTrue(r.size > 10L)
    }

    @Test
    fun `a result that is not smaller than the original is not kept`() {
        assertTrue(TargetSize.isSmaller(outputBytes = 99, inputBytes = 100))
        assertFalse(TargetSize.isSmaller(outputBytes = 100, inputBytes = 100))
        assertFalse(TargetSize.isSmaller(outputBytes = 150, inputBytes = 100))
    }

    // ---- video compress: the preset bitrate made low-bitrate videos bigger ----

    @Test
    fun `video compression never asks for more than the source has`() {
        val balanced = MediaPlans.VideoPreset.BALANCED
        // A 1 Mbit/s phone clip must not be re-encoded at 4 Mbit/s.
        val low = MediaPlans.videoBitrateFor(balanced, sourceBitrate = 1_000_000L, sourcePixels = 1280L * 720, outputPixels = 1280L * 720)
        assertTrue("got $low", low < 1_000_000)
        // A 40 Mbit/s 4K clip scaled to 1080p gets the preset rate.
        assertEquals(4_000_000, MediaPlans.videoBitrateFor(balanced, 40_000_000L, 3840L * 2160, 1920L * 1080))
        // A 12 Mbit/s 4K clip scaled to 1080p keeps a quarter of its pixels, so a quarter of its rate (x 0.8).
        assertTrue(MediaPlans.videoBitrateFor(balanced, 12_000_000L, 3840L * 2160, 1920L * 1080) < 12_000_000 / 4)
        // Unknown source bitrate: the preset.
        assertEquals(4_000_000, MediaPlans.videoBitrateFor(balanced, null, null, null))
        // Never absurdly low.
        assertTrue(MediaPlans.videoBitrateFor(MediaPlans.VideoPreset.SMALL, 50_000L, 100L, 100L) >= MediaPlans.MIN_VIDEO_BITRATE)
    }

    // ---- archives: Android 14+ refuses "../" entries itself, with a ZipException ----

    @Test
    fun `the platform's own zip path refusal is recognised as an unsafe archive`() {
        assertTrue(ZipGuard.isPlatformRefusal(ZipException("Invalid zip entry path: ../../evil.txt")))
        assertFalse(ZipGuard.isPlatformRefusal(ZipException("invalid CEN header (bad signature)")))
        assertFalse(ZipGuard.isPlatformRefusal(ZipException(null as String?)))
    }

    // ---- remove location: GPS in XMP survived ----

    @Test
    fun `location inside XMP is found`() {
        val lightroom = """<x:xmpmeta><rdf:Description exif:GPSLatitude="33,53.2N" exif:GPSLongitude="35,30.1E"/></x:xmpmeta>"""
        val element = "<exif:GPSAltitude>12/1</exif:GPSAltitude>"
        val iptc = """<rdf:Description photoshop:City="Beirut" Iptc4xmpExt:LocationShown="x"/>"""
        assertTrue(Xmp.hasLocation(lightroom))
        assertTrue(Xmp.hasLocation(element))
        assertTrue(Xmp.hasLocation(iptc))
        assertFalse(Xmp.hasLocation("""<rdf:Description xmp:CreatorTool="Camera" GCamera:MicroVideo="1"/>"""))
    }
}

/** Bugs found by running the tool matrix on the Android 15 emulator (each test failed before its fix). */
class DeviceFoundFixesTest {
    // ---- img.rotate on a JPEG without an orientation tag did nothing (ExifInterface.rotate ignores "undefined") ----

    @Test
    fun `turning a JPEG without an orientation tag starts from upright`() {
        assertEquals(6, ExifOrientation.rotated(0, 90))   // no tag (0 = undefined) + 90 -> rotate 90
        assertEquals(6, ExifOrientation.rotated(1, 90))
        assertEquals(3, ExifOrientation.rotated(6, 90))   // 90 + 90 = 180
        assertEquals(8, ExifOrientation.rotated(1, 270))
        assertEquals(1, ExifOrientation.rotated(8, 90))   // 270 + 90 = upright
        assertEquals(3, ExifOrientation.rotated(1, 180))
        assertEquals(1, ExifOrientation.rotated(1, 360))
        // Mirrored orientations keep the mirror and turn: 2 (flip H) + 90 = 7 (transverse), as ExifInterface defines it.
        assertEquals(7, ExifOrientation.rotated(2, 90))
        assertEquals(4, ExifOrientation.rotated(2, 180))
        assertEquals(5, ExifOrientation.rotated(4, 90))
        // Every orientation turned four times comes back.
        for (o in 1..8) assertEquals(o, (1..4).fold(o) { acc, _ -> ExifOrientation.rotated(acc, 90) })
    }

    // ---- archives from old Windows (no UTF-8 flag) were extracted with U+FFFD in the names ----

    @Test
    fun `names that do not decode as UTF-8 send the archive to the DOS code page`() {
        assertTrue(ZipGuard.needsLegacyNames(listOf("ok.txt", "R�sum�.txt")))
        assertFalse(ZipGuard.needsLegacyNames(listOf("ok.txt", "ملاحظات.txt", "Résumé.txt")))
        assertFalse(ZipGuard.needsLegacyNames(emptyList()))
    }

    // ---- PDF text: Arabic words came out in reverse order (PdfBox applies bidi per word, not per line) ----

    @Test
    fun `words of a right-to-left line are put back in reading order`() {
        // PdfBox hands over each word already in logical order, but the words in left-to-right page order.
        assertEquals(listOf("مرحبا", "بالعالم"), BidiWords.logicalOrder(listOf("بالعالم", "مرحبا")))
        assertEquals(
            listOf("هذا", "ملف", "تجريبي", "باللغة", "العربية"),
            BidiWords.logicalOrder(listOf("العربية", "باللغة", "تجريبي", "ملف", "هذا")),
        )
        // English lines are untouched.
        assertEquals(listOf("The", "quick", "fox"), BidiWords.logicalOrder(listOf("The", "quick", "fox")))
        // An Arabic phrase inside an English line is turned on its own.
        assertEquals(
            listOf("Hello", "مرحبا", "بالعالم", "there"),
            BidiWords.logicalOrder(listOf("Hello", "بالعالم", "مرحبا", "there")),
        )
        // English words inside an Arabic line keep their own order: "هذا DropFlow app جميل" is shown
        // "جميل DropFlow app هذا" from left to right.
        assertEquals(listOf("هذا", "DropFlow", "app", "جميل"), BidiWords.logicalOrder(listOf("جميل", "DropFlow", "app", "هذا")))
        // A number in an Arabic line: shown "1" left of "صفحة", read "صفحة 1".
        assertEquals(listOf("صفحة", "1"), BidiWords.logicalOrder(listOf("1", "صفحة")))
        assertEquals(emptyList<String>(), BidiWords.logicalOrder(emptyList()))
    }
}

class HevcOfferTest {
    // ---- "MP4 (HEVC)" was offered on phones without an HEVC encoder; Media3 then quietly made H.264 or shrank the video ----

    @Test
    fun `no HEVC target without an HEVC video encoder`() {
        val caps = dev.scouthub.dropflow.catalog.AndroidTools.Capabilities(heicEncoder = false, hevcEncoder = false)
        val w = dev.scouthub.dropflow.catalog.AndroidTools.wheelFor(listOf(FileKind.VIDEO), caps)
        assertFalse(w[dev.scouthub.dropflow.catalog.Ring.CONVERT]!!.any { it.key == "convert.video.mp4-hevc" })
        val with = dev.scouthub.dropflow.catalog.AndroidTools.wheelFor(listOf(FileKind.VIDEO), caps.copy(hevcEncoder = true))
        assertTrue(with[dev.scouthub.dropflow.catalog.Ring.CONVERT]!!.any { it.key == "convert.video.mp4-hevc" })
    }
}

class ForegroundTypeTest {
    // ---- every job crashed the app on Android 15+: "Starting FGS with type none ... has been prohibited" ----
    // androidx.core 1.18 ServiceCompat.startForeground masks the type with 0x40000FFF (the types known in API 34),
    // which turns mediaProcessing (0x2000, API 35) into 0.

    @Test
    fun `the foreground type survives and is declared in the manifest`() {
        val serviceCompatMask = 0x40000FFF
        for (sdk in 29..36) {
            val type = ForegroundPlan.type(sdk)
            assertTrue("sdk $sdk: a real type", type != 0)
            if (sdk >= 35) {
                assertEquals(0x2000, type) // mediaProcessing
                assertEquals("ServiceCompat would drop it, so DropFlow must not use ServiceCompat", 0, type and serviceCompatMask)
            } else {
                assertEquals(0x1, type) // dataSync
            }
        }
        assertFalse("WorkService must call Service.startForeground directly", ForegroundPlan.USE_SERVICE_COMPAT)
        val manifest = generateSequence(java.io.File("").absoluteFile) { it.parentFile }
            .flatMap { sequenceOf(java.io.File(it, "src/main/AndroidManifest.xml"), java.io.File(it, "app/src/main/AndroidManifest.xml")) }
            .first { it.exists() }.readText()
        assertTrue(manifest.contains("android:foregroundServiceType=\"dataSync|mediaProcessing\""))
    }
}
