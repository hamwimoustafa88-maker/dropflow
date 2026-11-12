// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.pdf.PdfDocument
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.media.MediaMuxer
import android.util.Base64
import androidx.exifinterface.media.ExifInterface
import androidx.heifwriter.HeifWriter
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.pdmodel.PDDocument
import com.tom_roush.pdfbox.pdmodel.encryption.AccessPermission
import com.tom_roush.pdfbox.pdmodel.encryption.StandardProtectionPolicy
import dev.scouthub.dropflow.core.WavHeader
import dev.scouthub.dropflow.ops.Qr
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.Charset
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.math.PI
import kotlin.math.sin

/**
 * Real sample files made on the device (nothing is downloaded): images in every format Android can write plus a
 * hand-made GIF and BMP, PDFs with English, Arabic and no text, protected PDFs, encoded video/audio, and archives.
 */
class Samples(private val context: Context) {
    val dir: File = File(context.cacheDir, "matrix-samples").apply { deleteRecursively(); mkdirs() }

    // ------------------------------------------------------------------------------------------------ images

    /** A picture that is clearly not symmetric: red top-left quadrant, so rotation and orientation can be checked. */
    fun picture(w: Int, h: Int, alpha: Boolean = false): Bitmap {
        val b = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        val c = Canvas(b)
        c.drawColor(if (alpha) Color.TRANSPARENT else Color.WHITE)
        val p = Paint(Paint.ANTI_ALIAS_FLAG)
        p.color = Color.RED; c.drawRect(0f, 0f, w / 2f, h / 2f, p)
        p.color = Color.BLUE; c.drawRect(w / 2f, h / 2f, w.toFloat(), h.toFloat(), p)
        p.color = Color.argb(if (alpha) 128 else 255, 0, 160, 0); c.drawCircle(w * 0.75f, h * 0.25f, h / 6f, p)
        // Noise so lossy encoders have something to chew on (target-size compression needs a non-trivial file).
        val rnd = java.util.Random(7)
        for (i in 0 until w * h / 40) {
            p.color = Color.rgb(rnd.nextInt(256), rnd.nextInt(256), rnd.nextInt(256))
            c.drawPoint(rnd.nextInt(w).toFloat(), rnd.nextInt(h).toFloat(), p)
        }
        return b
    }

    private fun write(name: String, bytes: ByteArray) = File(dir, name).apply { writeBytes(bytes) }

    private fun compress(b: Bitmap, format: Bitmap.CompressFormat, q: Int): ByteArray =
        java.io.ByteArrayOutputStream().also { b.compress(format, q, it) }.toByteArray()

    /** 400x300 pixels stored, EXIF orientation 6 (shown 300x400), GPS position, date, camera and an XMP block with GPS. */
    fun jpegWithExif(): File {
        val f = write("camera photo.jpg", compress(picture(400, 300), Bitmap.CompressFormat.JPEG, 92))
        ExifInterface(f).apply {
            setAttribute(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_ROTATE_90.toString())
            setLatLong(33.8938, 35.5018)
            setAttribute(ExifInterface.TAG_DATETIME_ORIGINAL, "2026:11:12 10:20:30")
            setAttribute(ExifInterface.TAG_MAKE, "DropFlowCam")
            setAttribute(ExifInterface.TAG_MODEL, "Matrix 1")
            setAttribute(
                ExifInterface.TAG_XMP,
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                    "<rdf:Description xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" exif:GPSLatitude=\"33,53.6N\" exif:GPSLongitude=\"35,30.1E\"/>" +
                    "</rdf:RDF></x:xmpmeta>",
            )
            saveAttributes()
        }
        return f
    }

    /** A JPEG with no EXIF at all (screenshots, chat apps and the web mostly look like this). */
    fun jpegPlain(): File = write("chat image.jpg", compress(picture(400, 300), Bitmap.CompressFormat.JPEG, 90))

    fun pngAlpha(): File = write("logo transparent.png", compress(picture(320, 240, alpha = true), Bitmap.CompressFormat.PNG, 100))

    @Suppress("DEPRECATION")
    fun webp(): File = write("sticker.webp", compress(picture(256, 256), Bitmap.CompressFormat.WEBP_LOSSY, 80))

    /** HEIC through HeifWriter, null when the device has no HEVC encoder. */
    fun heic(): File? = runCatching {
        val f = File(dir, "iphone photo.heic")
        val b = picture(512, 384)
        val w = HeifWriter.Builder(f.absolutePath, b.width, b.height, HeifWriter.INPUT_MODE_BITMAP).setQuality(85).setMaxImages(1).build()
        w.start(); w.addBitmap(b); w.stop(30_000)
        (w as AutoCloseable).close()
        f.takeIf { it.length() > 0 }
    }.getOrNull()

    /** A 1x1 GIF (Android has no GIF encoder; this is the classic transparent pixel). */
    fun gif(): File = write("pixel.gif", Base64.decode("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7", Base64.DEFAULT))

    /** 24-bit BMP written by hand, 64x48. */
    fun bmp(): File {
        val w = 64; val h = 48
        val row = (w * 3 + 3) / 4 * 4
        val size = 54 + row * h
        val buf = ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)
        buf.put('B'.code.toByte()).put('M'.code.toByte()).putInt(size).putInt(0).putInt(54)
        buf.putInt(40).putInt(w).putInt(h).putShort(1).putShort(24).putInt(0).putInt(row * h).putInt(2835).putInt(2835).putInt(0).putInt(0)
        for (y in 0 until h) {
            for (x in 0 until w) { buf.put((x * 4).toByte()); buf.put((y * 5).toByte()); buf.put(200.toByte()) }
            repeat(row - w * 3) { buf.put(0) }
        }
        return write("scan.bmp", buf.array())
    }

    /** 7000x5600 = 39.2 MP, above DropFlow's 36 MP decoding cap. */
    fun hugeJpeg(): File {
        val b = Bitmap.createBitmap(7000, 5600, Bitmap.Config.ARGB_8888)
        Canvas(b).apply { drawColor(Color.rgb(30, 90, 160)); drawCircle(3500f, 2800f, 2000f, Paint().apply { color = Color.YELLOW }) }
        val f = write("panorama.jpg", compress(b, Bitmap.CompressFormat.JPEG, 85))
        b.recycle()
        return f
    }

    val qrText = "https://dropflow.scouthub.dev/ar ‏مرحبا بالعالم"

    fun qrPng(): File = write("qr code.png", compress(Qr.create(qrText, 600), Bitmap.CompressFormat.PNG, 100))

    // ------------------------------------------------------------------------------------------------ PDF

    private fun pdf(name: String, pages: Int, draw: (Canvas, Int) -> Unit): File {
        val doc = PdfDocument()
        for (i in 0 until pages) {
            val page = doc.startPage(PdfDocument.PageInfo.Builder(595, 842, i + 1).create())
            page.canvas.drawColor(Color.WHITE)
            draw(page.canvas, i)
            doc.finishPage(page)
        }
        val f = File(dir, name)
        f.outputStream().use { doc.writeTo(it) }
        doc.close()
        return f
    }

    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.BLACK; textSize = 18f }

    fun pdfEnglish(): File = pdf("English report.pdf", 3) { c, i ->
        c.drawText("DropFlow test page ${i + 1}", 60f, 100f, textPaint)
        c.drawText("The quick brown fox jumps over the lazy dog.", 60f, 140f, textPaint)
    }

    /** The words are written in their logical order; Android shapes and lays them out right to left (as Chrome would). */
    val arabicLines = listOf("مرحبا بالعالم", "هذا ملف تجريبي باللغة العربية")

    /** Arabic with English words inside (the English keeps its own left-to-right order). */
    val arabicMixed = "هذا DropFlow app جميل"

    fun pdfArabic(): File {
        val arabic = Paint(textPaint).apply { typeface = Typeface.create("sans-serif", Typeface.NORMAL); textSize = 22f; textAlign = Paint.Align.RIGHT }
        return pdf("تقرير عربي.pdf", 2) { c, i ->
            c.drawText(arabicLines[0], 535f, 100f, arabic)
            c.drawText(arabicLines[1], 535f, 140f, arabic)
            c.drawText("صفحة ${i + 1}", 535f, 180f, arabic)
            c.drawText(arabicMixed, 535f, 220f, arabic)
        }
    }

    /**
     * Arabic the way many older generators write it: letters already shaped into presentation forms (U+FExx) and
     * stored in visual order (right to left reversed), in an embedded TrueType font with a ToUnicode map.
     * Null when the system has no Arabic TrueType font with presentation forms.
     */
    fun pdfArabicPresentationForms(): File? {
        PDFBoxResourceLoader.init(context)
        val fontFile = listOf("/system/fonts/NotoNaskhArabic-Regular.ttf", "/system/fonts/NotoSansArabic-Regular.ttf")
            .map(::File).firstOrNull { it.exists() } ?: return null
        val f = File(dir, "عربي قديم.pdf")
        PDDocument().use { doc ->
            val font = com.tom_roush.pdfbox.pdmodel.font.PDType0Font.load(doc, fontFile)
            val page = com.tom_roush.pdfbox.pdmodel.PDPage()
            doc.addPage(page)
            com.tom_roush.pdfbox.pdmodel.PDPageContentStream(doc, page).use { cs ->
                var y = 700f
                for (line in arabicLines) {
                    val visual = shapeArabic(line).reversed()
                    cs.beginText()
                    cs.setFont(font, 20f)
                    val width = font.getStringWidth(visual) / 1000f * 20f
                    cs.newLineAtOffset(540f - width, y)
                    cs.showText(visual)
                    cs.endText()
                    y -= 40f
                }
            }
            doc.save(f)
        }
        return f
    }

    /** Isolated, final, initial, medial presentation forms of the letters the samples use (right-joining letters have 2). */
    private val forms: Map<Char, IntArray> = mapOf(
        'ا' to intArrayOf(0xFE8D, 0xFE8E), 'ب' to intArrayOf(0xFE8F, 0xFE90, 0xFE91, 0xFE92), 'ة' to intArrayOf(0xFE93, 0xFE94),
        'ت' to intArrayOf(0xFE95, 0xFE96, 0xFE97, 0xFE98), 'ج' to intArrayOf(0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0),
        'ح' to intArrayOf(0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4), 'ذ' to intArrayOf(0xFEAB, 0xFEAC), 'ر' to intArrayOf(0xFEAD, 0xFEAE),
        'ع' to intArrayOf(0xFEC9, 0xFECA, 0xFECB, 0xFECC), 'غ' to intArrayOf(0xFECD, 0xFECE, 0xFECF, 0xFED0),
        'ف' to intArrayOf(0xFED1, 0xFED2, 0xFED3, 0xFED4), 'ل' to intArrayOf(0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0),
        'م' to intArrayOf(0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4), 'ه' to intArrayOf(0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC),
        'ي' to intArrayOf(0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4),
    )

    /** A tiny Arabic shaper for the sample words (no lam-alef in them): logical letters to presentation forms. */
    private fun shapeArabic(text: String): String = buildString {
        for (i in text.indices) {
            val f = forms[text[i]]
            if (f == null) { append(text[i]); continue }
            val prev = text.getOrNull(i - 1)?.let { forms[it] }
            val next = text.getOrNull(i + 1)?.let { forms[it] }
            val joinPrev = prev != null && prev.size == 4
            val joinNext = f.size == 4 && next != null
            val form = when {
                joinPrev && joinNext -> 3
                joinPrev -> 1
                joinNext -> 2
                else -> 0
            }
            append(f[form].toChar())
        }
    }

    /** Pages that are only pictures (what a scanner app makes): no text layer at all. */
    fun pdfScanned(): File {
        val scan = Bitmap.createBitmap(595, 842, Bitmap.Config.ARGB_8888)
        Canvas(scan).apply { drawColor(Color.rgb(250, 248, 240)); drawText("SCANNED TEXT", 60f, 100f, textPaint) }
        return pdf("scan.pdf", 2) { c, _ -> c.drawBitmap(scan, 0f, 0f, null) }
    }

    fun pdfManyPages(n: Int = 12): File = pdf("book.pdf", n) { c, i -> c.drawText("Page ${i + 1}", 60f, 100f, textPaint) }

    /** One page with a large photo-like image (stored losslessly by PdfDocument), so compression has work to do. */
    fun pdfWithBigImage(): File {
        val photo = picture(2000, 1500)
        return pdf("photos.pdf", 1) { c, _ -> c.drawBitmap(photo, null, android.graphics.RectF(20f, 20f, 575f, 436f), null) }
    }

    /** A PDF protected with an owner and a user password (PdfBox, 128-bit AES). */
    fun pdfProtected(): File {
        PDFBoxResourceLoader.init(context)
        val f = File(dir, "locked.pdf")
        PDDocument.load(pdfEnglish()).use { doc ->
            doc.protect(StandardProtectionPolicy("owner-secret", "user-secret", AccessPermission()).apply { encryptionKeyLength = 128 })
            doc.save(f)
        }
        return f
    }

    // ------------------------------------------------------------------------------------------------ audio / video

    private fun pcm(seconds: Double, rate: Int, channels: Int, freq: Double = 440.0): ByteArray {
        val frames = (seconds * rate).toInt()
        val b = ByteBuffer.allocate(frames * channels * 2).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until frames) {
            val s = (sin(2 * PI * freq * i / rate) * 12000).toInt().toShort()
            repeat(channels) { b.putShort(s) }
        }
        return b.array()
    }

    fun wav(seconds: Double = 3.0, rate: Int = 44100, channels: Int = 2): File {
        val data = pcm(seconds, rate, channels)
        return write("voice note.wav", WavHeader.build(data.size.toLong(), rate, channels) + data)
    }

    fun hasEncoder(mime: String): Boolean =
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { it.isEncoder && it.supportedTypes.any { t -> t.equals(mime, true) } }

    private class Sample(val data: ByteArray, val ptsUs: Long, val flags: Int)
    private class Track(val format: MediaFormat, val samples: List<Sample>)

    /** Runs one encoder to the end and keeps every output sample (small clips only). */
    private fun drain(codec: MediaCodec, feed: (index: Int) -> Boolean): Track {
        val info = MediaCodec.BufferInfo()
        val out = mutableListOf<Sample>()
        var format: MediaFormat? = null
        var inputDone = false
        while (true) {
            if (!inputDone) {
                val i = codec.dequeueInputBuffer(10_000)
                if (i >= 0) inputDone = !feed(i)
            }
            val o = codec.dequeueOutputBuffer(info, 10_000)
            if (o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) format = codec.outputFormat
            if (o >= 0) {
                val buf = codec.getOutputBuffer(o)!!
                if (info.size > 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) {
                    val bytes = ByteArray(info.size)
                    buf.position(info.offset); buf.get(bytes)
                    out += Sample(bytes, info.presentationTimeUs, info.flags)
                }
                codec.releaseOutputBuffer(o, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
            }
        }
        codec.stop(); codec.release()
        return Track(format!!, out)
    }

    private fun encodeVideo(mime: String, w: Int, h: Int, seconds: Double, fps: Int = 30, bitrate: Int = 1_200_000, noise: Boolean = false): Track {
        val rnd = java.util.Random(3)
        val fmt = MediaFormat.createVideoFormat(mime, w, h).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }
        val codec = MediaCodec.createEncoderByType(mime)
        codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
        val frames = (seconds * fps).toInt()
        var n = 0
        return drain(codec) { index ->
            if (n >= frames) {
                codec.queueInputBuffer(index, 0, 0, n * 1_000_000L / fps, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                false
            } else {
                val image = codec.getInputImage(index)!!
                // Y: a bar that moves with time; U/V: constant colour. Respect the strides the codec asks for.
                val planes = image.planes
                for (p in planes.indices) {
                    val plane = planes[p]
                    val pw = if (p == 0) w else w / 2
                    val ph = if (p == 0) h else h / 2
                    val buf = plane.buffer
                    for (y in 0 until ph) for (x in 0 until pw) {
                        val v = if (p == 0) {
                            if (noise) rnd.nextInt(256) else if ((x + n * 4) % pw < pw / 4) 230 else 60
                        } else if (p == 1) 90 else 200
                        buf.put(y * plane.rowStride + x * plane.pixelStride, v.toByte())
                    }
                }
                codec.queueInputBuffer(index, 0, w * h * 3 / 2, n * 1_000_000L / fps, 0)
                n++
                true
            }
        }
    }

    private fun encodeAudio(mime: String, rate: Int, channels: Int, seconds: Double, bitrate: Int): Track {
        val fmt = MediaFormat.createAudioFormat(mime, rate, channels).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            if (mime == MediaFormat.MIMETYPE_AUDIO_AAC) setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16 * 1024)
        }
        val codec = MediaCodec.createEncoderByType(mime)
        codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
        val pcm = pcm(seconds, rate, channels)
        var offset = 0
        val bytesPerSecond = rate * channels * 2
        return drain(codec) { index ->
            val buf = codec.getInputBuffer(index)!!
            val pts = offset * 1_000_000L / bytesPerSecond
            if (offset >= pcm.size) {
                codec.queueInputBuffer(index, 0, 0, pts, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                false
            } else {
                val n = minOf(buf.remaining(), pcm.size - offset, 4096 * channels)
                buf.put(pcm, offset, n)
                codec.queueInputBuffer(index, 0, n, pts, 0)
                offset += n
                true
            }
        }
    }

    private fun mux(name: String, format: Int, tracks: List<Track>, rotation: Int = 0): File {
        val f = File(dir, name)
        val m = MediaMuxer(f.absolutePath, format)
        if (rotation != 0) m.setOrientationHint(rotation)
        val ids = tracks.map { m.addTrack(it.format) }
        m.start()
        val info = MediaCodec.BufferInfo()
        // Interleave by time like a real muxer input.
        val all = tracks.flatMapIndexed { t, track -> track.samples.map { t to it } }.sortedBy { it.second.ptsUs }
        for ((t, s) in all) {
            info.set(0, s.data.size, s.ptsUs, if (s.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            m.writeSampleData(ids[t], ByteBuffer.wrap(s.data), info)
        }
        m.stop(); m.release()
        return f
    }

    /** H.264 or HEVC video (+ AAC sound) in MP4; null if the device cannot encode [videoMime]. */
    fun mp4(name: String, videoMime: String = MediaFormat.MIMETYPE_VIDEO_AVC, w: Int = 320, h: Int = 240, seconds: Double = 3.0,
            withAudio: Boolean = true, rotation: Int = 0, bitrate: Int = 1_200_000, noise: Boolean = false): File? {
        if (!hasEncoder(videoMime)) return null
        val tracks = mutableListOf(encodeVideo(videoMime, w, h, seconds, bitrate = bitrate, noise = noise))
        if (withAudio) tracks += encodeAudio(MediaFormat.MIMETYPE_AUDIO_AAC, 44100, 2, seconds, 128_000)
        return mux(name, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4, tracks, rotation)
    }

    /** VP8 video + Opus sound in WebM (forces DropFlow's re-encode path); null when an encoder is missing. */
    fun webm(): File? {
        if (!hasEncoder(MediaFormat.MIMETYPE_VIDEO_VP8) || !hasEncoder(MediaFormat.MIMETYPE_AUDIO_OPUS)) return null
        return mux("screen recording.webm", MediaMuxer.OutputFormat.MUXER_OUTPUT_WEBM,
            listOf(encodeVideo(MediaFormat.MIMETYPE_VIDEO_VP8, 320, 240, 3.0), encodeAudio(MediaFormat.MIMETYPE_AUDIO_OPUS, 48000, 2, 3.0, 64_000)))
    }

    fun m4a(): File = mux("podcast.m4a", MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4, listOf(encodeAudio(MediaFormat.MIMETYPE_AUDIO_AAC, 44100, 2, 4.0, 128_000)))

    fun ogg(): File? = if (!hasEncoder(MediaFormat.MIMETYPE_AUDIO_OPUS)) null
    else mux("voice.ogg", MediaMuxer.OutputFormat.MUXER_OUTPUT_OGG, listOf(encodeAudio(MediaFormat.MIMETYPE_AUDIO_OPUS, 48000, 1, 3.0, 32_000)))

    fun amr(): File? = if (!hasEncoder(MediaFormat.MIMETYPE_AUDIO_AMR_NB)) null
    else mux("call.3gp", MediaMuxer.OutputFormat.MUXER_OUTPUT_3GPP, listOf(encodeAudio(MediaFormat.MIMETYPE_AUDIO_AMR_NB, 8000, 1, 3.0, 12_200)))

    // ------------------------------------------------------------------------------------------------ archives

    private fun zip(name: String, charset: Charset, entries: List<Pair<String, ByteArray?>>): File {
        val f = File(dir, name)
        ZipOutputStream(f.outputStream(), charset).use { z ->
            for ((n, data) in entries) {
                z.putNextEntry(ZipEntry(n))
                data?.let { z.write(it) }
                z.closeEntry()
            }
        }
        return f
    }

    /** Folders, an Arabic file name, a picture and an empty folder. */
    fun zipNested(): File = zip(
        "project files.zip", Charsets.UTF_8,
        listOf(
            "docs/" to null,
            "docs/readme.txt" to "hello".toByteArray(),
            "docs/ملاحظات.txt" to "مرحبا".toByteArray(),
            "images/red.png" to compress(picture(32, 32), Bitmap.CompressFormat.PNG, 100),
            "empty/" to null,
            "top.txt" to "top".toByteArray(),
        ),
    )

    /** Names in the old DOS code page (no UTF-8 flag), as Windows' built-in zip used to write. */
    fun zipCp437(): File = zip("old windows.zip", Charset.forName("IBM437"), listOf("Résumé.txt" to "cv".toByteArray(), "café/menu.txt" to "x".toByteArray()))

    fun zipSlip(): File = zip("evil.zip", Charsets.UTF_8, listOf("ok.txt" to "ok".toByteArray(), "../../evil.txt" to "pwned".toByteArray()))

    /** A valid archive with no entries. */
    fun zipEmpty(): File = zip("empty.zip", Charsets.UTF_8, emptyList())

    fun text(name: String, body: String): File = File(dir, name).apply { writeText(body, Charsets.UTF_8) }
}
