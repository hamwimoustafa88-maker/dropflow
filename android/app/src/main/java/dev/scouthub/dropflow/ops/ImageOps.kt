// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ImageDecoder
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.pdf.PdfDocument
import android.media.MediaCodecList
import android.os.Build
import androidx.exifinterface.media.ExifInterface
import androidx.heifwriter.HeifWriter
import dev.scouthub.dropflow.core.ExifOrientation
import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.core.TargetSize
import dev.scouthub.dropflow.core.Xmp
import dev.scouthub.dropflow.work.InputFile
import java.io.ByteArrayOutputStream
import java.io.File
import java.lang.reflect.Modifier
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sqrt

/** Image work with Android's own codecs (ImageDecoder, Bitmap.compress, HeifWriter) and ExifInterface. */
object ImageOps {
    /** Decoding cap: about 36 megapixels (144 MB as ARGB). Larger images are scaled down while decoding. */
    private const val MAX_PIXELS = 36_000_000L

    private val lossyKeep = setOf("jpg", "jpeg", "png", "webp")

    val heicEncoderAvailable: Boolean by lazy {
        runCatching {
            MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { info ->
                info.isEncoder && info.supportedTypes.any { it.equals("video/hevc", true) || it.equals("image/vnd.android.heic", true) }
            }
        }.getOrDefault(false)
    }

    // ------------------------------------------------------------------ decode / encode

    fun decode(file: File, maxLongEdge: Int? = null): Bitmap {
        val source = ImageDecoder.createSource(file)
        return try {
            ImageDecoder.decodeBitmap(source) { decoder, info, _ ->
                decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
                val w = info.size.width
                val h = info.size.height
                var scale = 1.0
                val pixels = w.toLong() * h
                if (pixels > MAX_PIXELS) scale = sqrt(MAX_PIXELS.toDouble() / pixels)
                if (maxLongEdge != null && max(w, h) * scale > maxLongEdge) scale = maxLongEdge.toDouble() / max(w, h)
                if (scale < 1.0) decoder.setTargetSize(max(1, (w * scale).roundToInt()), max(1, (h * scale).roundToInt()))
            }
        } catch (e: Exception) {
            throw OpFailure("error_decode_image", e.message)
        }
    }

    private fun flattened(b: Bitmap): Bitmap {
        if (!b.hasAlpha()) return b
        val out = Bitmap.createBitmap(b.width, b.height, Bitmap.Config.ARGB_8888)
        Canvas(out).apply {
            drawColor(Color.WHITE)
            drawBitmap(b, 0f, 0f, null)
        }
        return out
    }

    @Suppress("DEPRECATION")
    private fun webpFormat(): Bitmap.CompressFormat =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) Bitmap.CompressFormat.WEBP_LOSSY else Bitmap.CompressFormat.WEBP

    fun encodeBytes(bitmap: Bitmap, format: String, quality: Int): ByteArray {
        val bytes = ByteArrayOutputStream()
        when (format) {
            "jpg", "jpeg" -> flattened(bitmap).compress(Bitmap.CompressFormat.JPEG, quality, bytes)
            "png" -> bitmap.compress(Bitmap.CompressFormat.PNG, 100, bytes)
            "webp" -> bitmap.compress(webpFormat(), quality, bytes)
            else -> throw OpFailure("error_format", format)
        }
        return bytes.toByteArray()
    }

    fun encode(bitmap: Bitmap, format: String, quality: Int, out: File) {
        if (format == "heic" || format == "heif") {
            writeHeic(bitmap, quality, out)
            return
        }
        out.writeBytes(encodeBytes(bitmap, format, quality))
    }

    private fun writeHeic(bitmap: Bitmap, quality: Int, out: File) {
        if (!heicEncoderAvailable) throw OpFailure("error_no_heic_encoder")
        val argb = if (bitmap.config == Bitmap.Config.ARGB_8888) bitmap else bitmap.copy(Bitmap.Config.ARGB_8888, false)
        try {
            val writer = HeifWriter.Builder(out.absolutePath, argb.width, argb.height, HeifWriter.INPUT_MODE_BITMAP)
                .setQuality(quality)
                .setMaxImages(1)
                .build()
            try {
                writer.start()
                writer.addBitmap(argb)
                writer.stop(60_000)
            } finally {
                val closeable: AutoCloseable = writer // HeifWriter's own close() override is marked library-internal
                closeable.close()
            }
        } catch (e: OpFailure) {
            throw e
        } catch (e: Exception) {
            out.delete()
            throw OpFailure("error_encode", e.message)
        }
    }

    /** Keeps date and camera model on converted images (never the location). */
    private fun copyBasicExif(from: File, to: File, format: String) {
        if (format !in lossyKeep) return
        runCatching {
            val src = ExifInterface(from)
            val dst = ExifInterface(to)
            for (tag in listOf(
                ExifInterface.TAG_DATETIME, ExifInterface.TAG_DATETIME_ORIGINAL, ExifInterface.TAG_DATETIME_DIGITIZED,
                ExifInterface.TAG_OFFSET_TIME_ORIGINAL, ExifInterface.TAG_MAKE, ExifInterface.TAG_MODEL,
            )) {
                src.getAttribute(tag)?.let { dst.setAttribute(tag, it) }
            }
            dst.saveAttributes()
        }
    }

    /** Output format that keeps the input's format when Android can write it. */
    private fun sameFormat(input: InputFile, bitmap: Bitmap): String = when (val ext = FileKind.extensionOf(input.name)) {
        "jpg", "jpeg" -> "jpg"
        "png", "webp" -> ext
        "heic", "heif" -> if (heicEncoderAvailable) "heic" else "jpg"
        else -> if (bitmap.hasAlpha()) "png" else "jpg"
    }

    private fun produced(file: File, ext: String) = Produced(file, file.name, FileKind.mimeFor(ext))

    // ------------------------------------------------------------------ operations

    suspend fun convert(ctx: OpContext, inputs: List<InputFile>, target: String): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            progress(0.1f)
            val bitmap = decode(input.file)
            val out = ctx.newFile(OutputNaming.forFile(input.name, target))
            encode(bitmap, target, if (target == "heic") 85 else 92, out)
            copyBasicExif(input.file, out, target)
            progress(1f)
            produced(out, target)
        }
        return OpResult(files)
    }

    suspend fun compress(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val bySize = ctx.option("compress_by", "quality") == "size"
        val formatOption = ctx.option("format", "auto")
        val target = if (bySize) TargetSize.parseKilobytes(ctx.option("target_kb", "500")) ?: throw OpFailure("error_target_size") else 0L
        val files = ctx.eachInput(inputs) { input, progress ->
            val full = decode(input.file)
            val format = when (formatOption) {
                "jpg", "webp" -> formatOption
                else -> if (full.hasAlpha()) "webp" else "jpg"
            }
            val bytes = if (bySize) {
                var bitmap = full
                var bitmapStep = 0
                val r = TargetSize.searchShrinking(
                    target,
                    maxSteps = 8,
                    encodeAt = { step, q ->
                        // Even the lowest quality was too big: shrink the picture (0.75x per side per step) and try again.
                        while (bitmapStep < step) {
                            bitmap = Bitmap.createScaledBitmap(bitmap, max(1, (bitmap.width * 0.75).toInt()), max(1, (bitmap.height * 0.75).toInt()), true)
                            bitmapStep++
                        }
                        encodeBytes(bitmap, format, q)
                    },
                    sizeOf = { it.size.toLong() },
                    onStep = { step -> progress(step / 8f) },
                )
                ctx.checkCancelled()
                // Never save a file above the size the person asked for.
                if (!r.fits) throw OpFailure("error_target_unreachable")
                r.value
            } else {
                encodeBytes(full, format, ctx.intOption("quality", 75).coerceIn(5, 100))
            }
            progress(1f)
            // A "compressed" copy that is not smaller than the original is useless: keep nothing for this file.
            if (!TargetSize.isSmaller(bytes.size.toLong(), input.file.length())) return@eachInput null
            val out = ctx.newFile(OutputNaming.forFile(input.name, format, "compressed"))
            out.writeBytes(bytes)
            copyBasicExif(input.file, out, format)
            // The date/camera tags add a few hundred bytes: drop them again if they push the file over the target.
            if (bySize && out.length() > target) out.writeBytes(bytes)
            produced(out, format)
        }.filterNotNull()
        if (files.isEmpty()) throw OpFailure("error_already_small")
        return OpResult(files)
    }

    suspend fun resize(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val byPercent = ctx.option("resize_by", "long_edge") == "percent"
        val files = ctx.eachInput(inputs) { input, progress ->
            val bitmap = if (byPercent) {
                val full = decode(input.file)
                val p = ctx.intOption("percent", 50).coerceIn(1, 100) / 100.0
                Bitmap.createScaledBitmap(full, max(1, (full.width * p).roundToInt()), max(1, (full.height * p).roundToInt()), true)
            } else {
                decode(input.file, maxLongEdge = ctx.intOption("long_edge", 1600).coerceAtLeast(16))
            }
            val format = sameFormat(input, bitmap)
            val out = ctx.newFile(OutputNaming.forFile(input.name, format, "resized"))
            encode(bitmap, format, 90, out)
            copyBasicExif(input.file, out, format)
            progress(1f)
            produced(out, format)
        }
        return OpResult(files)
    }

    suspend fun rotate(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val degrees = ctx.intOption("angle", 90).let { ((it % 360) + 360) % 360 }
        val files = ctx.eachInput(inputs) { input, progress ->
            val ext = FileKind.extensionOf(input.name)
            if (ext == "jpg" || ext == "jpeg") {
                // Lossless: JPEG pixels stay as they are, only the orientation tag turns.
                val out = ctx.newFile(OutputNaming.forFile(input.name, ext, "rotated"))
                input.file.copyTo(out)
                // Not ExifInterface.rotate(): it leaves a file without the tag ("undefined") unchanged.
                ExifInterface(out).apply {
                    val current = getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_UNDEFINED)
                    setAttribute(ExifInterface.TAG_ORIENTATION, ExifOrientation.rotated(current, degrees).toString())
                    saveAttributes()
                }
                progress(1f)
                return@eachInput produced(out, ext)
            }
            val bitmap = decode(input.file)
            val rotated = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, Matrix().apply { postRotate(degrees.toFloat()) }, true)
            val format = sameFormat(input, rotated)
            val out = ctx.newFile(OutputNaming.forFile(input.name, format, "rotated"))
            encode(rotated, format, 92, out)
            progress(1f)
            produced(out, format)
        }
        return OpResult(files)
    }

    /** Every EXIF tag name ExifInterface knows (its public TAG_ constants; kept by proguard-rules.pro). */
    private val allExifTags: List<String> by lazy {
        ExifInterface::class.java.fields
            .filter { Modifier.isStatic(it.modifiers) && it.type == String::class.java && it.name.startsWith("TAG_") }
            .mapNotNull { runCatching { it.get(null) as String }.getOrNull() }
            .distinct()
    }

    private val gpsTags: List<String> by lazy {
        ExifInterface::class.java.fields
            .filter { Modifier.isStatic(it.modifiers) && it.type == String::class.java && it.name.startsWith("TAG_GPS_") }
            .mapNotNull { runCatching { it.get(null) as String }.getOrNull() }
    }

    private fun xmpOf(exif: ExifInterface): String? =
        runCatching { exif.getAttributeBytes(ExifInterface.TAG_XMP)?.toString(Charsets.UTF_8) }.getOrNull()

    suspend fun stripMetadata(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val everything = ctx.option("strip", "location") == "all"
        val files = ctx.eachInput(inputs) { input, progress ->
            val ext = FileKind.extensionOf(input.name)
            if (ext in lossyKeep) {
                // Lossless: the pixels are copied as they are and only the metadata is removed.
                val out = ctx.newFile(OutputNaming.forFile(input.name, ext, "cleaned"))
                input.file.copyTo(out)
                val exif = ExifInterface(out)
                val tags = if (everything) allExifTags.filter { it != ExifInterface.TAG_ORIENTATION } else gpsTags
                for (tag in tags) runCatching { exif.setAttribute(tag, null) }
                // The XMP block can carry the position too (Lightroom, IPTC place names): drop it when it does.
                if (xmpOf(exif)?.let(Xmp::hasLocation) == true) runCatching { exif.setAttribute(ExifInterface.TAG_XMP, null) }
                exif.saveAttributes()
                val check = ExifInterface(out)
                if (check.latLong != null || check.getAttribute(ExifInterface.TAG_GPS_LATITUDE) != null ||
                    xmpOf(check)?.let(Xmp::hasLocation) == true
                ) {
                    out.delete()
                    throw OpFailure("error_strip_failed")
                }
                progress(1f)
                produced(out, if (ext == "jpeg") "jpg" else ext)
            } else {
                // Formats Android cannot rewrite in place (HEIC, AVIF...): re-encode the pixels, which carries no metadata.
                val bitmap = decode(input.file)
                val format = if (bitmap.hasAlpha()) "png" else "jpg"
                val out = ctx.newFile(OutputNaming.forFile(input.name, format, "cleaned"))
                encode(bitmap, format, 95, out)
                progress(1f)
                produced(out, format)
            }
        }
        return OpResult(files)
    }

    /** Images to PDF with Android's PdfDocument. [combine] = one PDF for all images. */
    suspend fun toPdf(ctx: OpContext, inputs: List<InputFile>, combine: Boolean): OpResult {
        val a4 = ctx.option("page_size", "a4") == "a4"
        val groups = if (combine) listOf(inputs) else inputs.map { listOf(it) }
        val files = groups.mapIndexed { g, group ->
            val suffix = if (group.size > 1) "merged" else "converted"
            val out = ctx.newFile(OutputNaming.forFile(group.first().name, "pdf", suffix))
            val doc = PdfDocument()
            try {
                group.forEachIndexed { i, input ->
                    ctx.checkCancelled()
                    val bitmap = decode(input.file, maxLongEdge = 2480) // A4 at 300 dpi
                    val (pw, ph) = if (a4) {
                        if (bitmap.width > bitmap.height) 842 to 595 else 595 to 842
                    } else {
                        (bitmap.width * 72 / 150).coerceIn(72, 14_400) to (bitmap.height * 72 / 150).coerceIn(72, 14_400)
                    }
                    val page = doc.startPage(PdfDocument.PageInfo.Builder(pw, ph, i + 1).create())
                    val margin = if (a4) 24f else 0f
                    val boxW = pw - 2 * margin
                    val boxH = ph - 2 * margin
                    val scale = min(boxW / bitmap.width, boxH / bitmap.height)
                    val w = bitmap.width * scale
                    val h = bitmap.height * scale
                    val left = (pw - w) / 2f
                    val top = (ph - h) / 2f
                    page.canvas.drawColor(Color.WHITE)
                    page.canvas.drawBitmap(bitmap, null, RectF(left, top, left + w, top + h), Paint(Paint.FILTER_BITMAP_FLAG))
                    doc.finishPage(page)
                    bitmap.recycle()
                    ctx.progress((g + (i + 1f) / group.size) / groups.size)
                }
                out.outputStream().use { doc.writeTo(it) }
            } finally {
                doc.close()
            }
            Produced(out, out.name, "application/pdf")
        }
        return OpResult(files)
    }

    // ------------------------------------------------------------------ QR codes (ZXing)

    suspend fun readQr(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val texts = ctx.eachInput(inputs) { input, progress ->
            val text = Qr.read(decode(input.file, maxLongEdge = 2048))
            progress(1f)
            text?.let { if (inputs.size > 1) "${input.name}:\n$it" else it }
        }.filterNotNull()
        if (texts.isEmpty()) throw OpFailure("error_no_qr")
        return OpResult(text = texts.joinToString("\n\n"))
    }

    suspend fun createQr(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val text = ctx.options["qr_text"]?.takeIf { it.isNotBlank() }
            ?: inputs.firstOrNull()?.file?.readText(Charsets.UTF_8)?.takeIf { it.isNotBlank() }
            ?: throw OpFailure("error_qr_empty")
        val bitmap = Qr.create(text)
        val out = ctx.newFile("QR code.png")
        encode(bitmap, "png", 100, out)
        ctx.progress(1f)
        return OpResult(listOf(produced(out, "png")), text = null)
    }
}
