// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import android.content.Context
import android.graphics.BitmapFactory
import android.graphics.ImageDecoder
import android.graphics.pdf.PdfRenderer
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import android.os.ParcelFileDescriptor
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import dev.scouthub.dropflow.catalog.AndroidTools
import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.ops.OpContext
import dev.scouthub.dropflow.ops.OpFailure
import dev.scouthub.dropflow.ops.OpResult
import dev.scouthub.dropflow.work.InputFile
import dev.scouthub.dropflow.work.JobRequest
import dev.scouthub.dropflow.work.Runner
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import java.io.File

/**
 * Runs real tools on real samples through the same [Runner] the app uses and records one matrix row per
 * (sample, tool): PASS / FAIL / SKIP and what was checked. Rows go to logcat with the tag DFMATRIX so the full
 * table can be read after a run; a test fails at the end if any of its rows failed.
 */
class Matrix {
    val context: Context = InstrumentationRegistry.getInstrumentation().targetContext
    private val failures = mutableListOf<String>()

    fun input(file: File, mime: String? = null): InputFile {
        val m = mime ?: FileKind.mimeFor(FileKind.extensionOf(file.name))
        return InputFile(file, file.name, m, FileKind.of(file.name, m))
    }

    /** Runs the wheel entry [key] on [inputs] with [options]; returns the result or the failure. */
    fun run(key: String, inputs: List<InputFile>, options: Map<String, String> = emptyMap()): Result<OpResult> {
        val tool = AndroidTools.byKey(key) ?: error("no tool $key")
        val work = File(context.cacheDir, "matrix-jobs/${System.nanoTime()}").apply { mkdirs() }
        return runCatching {
            runBlocking { Runner.run(context, JobRequest(0, tool, inputs, options, key), work) { } }
        }
    }

    /** One matrix row. [check] gets the result and returns a short description of what it verified. */
    fun row(sample: String, tool: String, check: () -> String) {
        val line = try {
            "PASS|${check()}"
        } catch (e: Throwable) {
            val msg = "${e.javaClass.simpleName}: ${(e as? OpFailure)?.let { "${it.messageKey} ${it.detail}" } ?: e.message}"
            failures += "$sample × $tool: $msg"
            Log.e(TAG, "row failed", e)
            "FAIL|$msg"
        }
        Log.i(TAG, "$sample|$tool|$line")
    }

    fun skip(sample: String, tool: String, why: String) = Log.i(TAG, "$sample|$tool|SKIP|$why")

    fun finish() {
        if (failures.isNotEmpty()) fail("${failures.size} matrix rows failed:\n" + failures.joinToString("\n"))
    }

    // ------------------------------------------------------------------------------------------------ checks

    fun ok(r: Result<OpResult>): OpResult = r.getOrElse { throw it }

    /** The tool must refuse with exactly this message key. */
    fun refused(r: Result<OpResult>, key: String): String {
        val e = r.exceptionOrNull() ?: throw AssertionError("expected $key but the tool succeeded with ${r.getOrNull()?.files?.map { it.name }}")
        if (e !is OpFailure) throw AssertionError("expected OpFailure($key), got $e", e)
        assertEquals("message key", key, e.messageKey)
        return "refused with $key"
    }

    data class Img(val width: Int, val height: Int, val mime: String?)

    /** Decoded size as shown (EXIF orientation applied) and the format the bytes really are. */
    fun image(f: File): Img {
        val opts = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(f.absolutePath, opts)
        val b = ImageDecoder.decodeBitmap(ImageDecoder.createSource(f)) { d, _, _ -> d.allocator = ImageDecoder.ALLOCATOR_SOFTWARE }
        return Img(b.width, b.height, opts.outMimeType).also { b.recycle() }
    }

    fun pdfPages(f: File): Int = ParcelFileDescriptor.open(f, ParcelFileDescriptor.MODE_READ_ONLY).use { PdfRenderer(it).use { r -> r.pageCount } }

    data class Media(val video: List<String>, val audio: List<String>, val durationMs: Long, val width: Int?, val height: Int?, val rotation: Int)

    fun media(f: File): Media {
        val ex = MediaExtractor()
        val video = mutableListOf<String>()
        val audio = mutableListOf<String>()
        try {
            ex.setDataSource(f.absolutePath)
            for (i in 0 until ex.trackCount) {
                val m = ex.getTrackFormat(i).getString(MediaFormat.KEY_MIME) ?: continue
                if (m.startsWith("video/")) video += m
                if (m.startsWith("audio/")) audio += m
            }
        } finally {
            ex.release()
        }
        val r = MediaMetadataRetriever()
        try {
            r.setDataSource(f.absolutePath)
            return Media(
                video, audio,
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: -1,
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull(),
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull(),
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0,
            )
        } finally {
            r.release()
        }
    }

    fun assertNear(what: String, expected: Long, actual: Long, tolerance: Long) =
        assertTrue("$what: expected $expected ± $tolerance, got $actual", kotlin.math.abs(expected - actual) <= tolerance)

    companion object {
        const val TAG = "DFMATRIX"
    }
}
