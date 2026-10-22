// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.cos.COSBase
import com.tom_roush.pdfbox.cos.COSName
import com.tom_roush.pdfbox.io.MemoryUsageSetting
import com.tom_roush.pdfbox.multipdf.PDFMergerUtility
import com.tom_roush.pdfbox.pdmodel.PDDocument
import com.tom_roush.pdfbox.pdmodel.PDDocumentInformation
import com.tom_roush.pdfbox.pdmodel.PDResources
import com.tom_roush.pdfbox.pdmodel.encryption.InvalidPasswordException
import com.tom_roush.pdfbox.pdmodel.graphics.form.PDFormXObject
import com.tom_roush.pdfbox.pdmodel.graphics.image.JPEGFactory
import com.tom_roush.pdfbox.pdmodel.graphics.image.PDImageXObject
import com.tom_roush.pdfbox.text.PDFTextStripper
import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.core.PageRanges
import dev.scouthub.dropflow.work.InputFile
import java.io.File
import java.io.IOException
import kotlin.math.max
import kotlin.math.min

/** PDF work: Android's PdfRenderer for pages to images, PdfBox-Android (Apache-2.0) for everything that edits a PDF. */
object PdfOps {
    @Volatile
    private var pdfBoxReady = false

    private fun ensurePdfBox(context: Context) {
        if (!pdfBoxReady) synchronized(this) {
            if (!pdfBoxReady) {
                PDFBoxResourceLoader.init(context.applicationContext)
                pdfBoxReady = true
            }
        }
    }

    private fun memory(ctx: OpContext): MemoryUsageSetting =
        MemoryUsageSetting.setupTempFileOnly().setTempDir(File(ctx.workDir, "pdfbox-tmp").apply { mkdirs() })

    /** Opens a PDF for editing. Protected PDFs are refused: DropFlow never removes protection it was not given the password for. */
    private fun open(ctx: OpContext, file: File): PDDocument {
        ensurePdfBox(ctx.app)
        val doc = try {
            PDDocument.load(file, memory(ctx))
        } catch (e: InvalidPasswordException) {
            throw OpFailure("error_pdf_protected", e.message)
        } catch (e: IOException) {
            throw OpFailure("error_pdf_damaged", e.message)
        }
        if (doc.isEncrypted) {
            doc.close()
            throw OpFailure("error_pdf_protected")
        }
        return doc
    }

    private fun pdf(file: File) = Produced(file, file.name, "application/pdf")

    // ------------------------------------------------------------------ pages to images (PdfRenderer)

    suspend fun toImages(ctx: OpContext, inputs: List<InputFile>, format: String): OpResult {
        val dpi = ctx.intOption("dpi", 150).coerceIn(36, 600)
        val ext = if (format == "jpg") "jpg" else "png"
        val files = ctx.eachInput(inputs) { input, progress ->
            val fd = try {
                ParcelFileDescriptor.open(input.file, ParcelFileDescriptor.MODE_READ_ONLY)
            } catch (e: IOException) {
                throw OpFailure("error_pdf_damaged", e.message)
            }
            val renderer = try {
                PdfRenderer(fd)
            } catch (e: SecurityException) {
                fd.close()
                throw OpFailure("error_pdf_protected", e.message)
            } catch (e: IOException) {
                fd.close()
                throw OpFailure("error_pdf_damaged", e.message)
            }
            renderer.use { r ->
                val count = r.pageCount
                val folder = if (count > 1) OutputNaming.forFolder(input.name, "pages") else null
                val stem = OutputNaming.stem(input.name)
                (0 until count).map { i ->
                    ctx.checkCancelled()
                    r.openPage(i).use { page ->
                        var scale = dpi / 72f
                        val longest = max(page.width, page.height) * scale
                        if (longest > 6000f) scale *= 6000f / longest // keep each page bitmap within memory
                        val bitmap = Bitmap.createBitmap(
                            max(1, (page.width * scale).toInt()), max(1, (page.height * scale).toInt()), Bitmap.Config.ARGB_8888,
                        )
                        bitmap.eraseColor(Color.WHITE)
                        page.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                        val name = if (count > 1) "$stem - page ${(i + 1).toString().padStart(3, '0')}.$ext"
                        else OutputNaming.forFile(input.name, ext)
                        val out = ctx.newFile(name)
                        ImageOps.encode(bitmap, ext, 92, out)
                        bitmap.recycle()
                        progress((i + 1f) / count)
                        Produced(out, name, FileKind.mimeFor(ext), folder)
                    }
                }
            }
        }.flatten()
        return OpResult(files)
    }

    // ------------------------------------------------------------------ PdfBox-Android

    suspend fun merge(ctx: OpContext, inputs: List<InputFile>): OpResult {
        ensurePdfBox(ctx.app)
        // Check every input first so a protected file gives a clear message instead of a half-merged result.
        for (input in inputs) open(ctx, input.file).close()
        val out = ctx.newFile(OutputNaming.forFile(inputs.first().name, "pdf", "merged"))
        ctx.progress(null)
        try {
            PDFMergerUtility().apply {
                destinationFileName = out.absolutePath
                inputs.forEach { addSource(it.file) }
            }.mergeDocuments(memory(ctx))
        } catch (e: IOException) {
            throw OpFailure("error_pdf_damaged", e.message)
        }
        ctx.progress(1f)
        return OpResult(listOf(pdf(out)))
    }

    suspend fun split(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val byRanges = ctx.option("split_mode", "each") == "ranges"
        val files = ctx.eachInput(inputs) { input, progress ->
            open(ctx, input.file).use { doc ->
                val count = doc.numberOfPages
                val ranges = if (byRanges) parseRanges(ctx.option("ranges", "1"), count) else (1..count).map { it..it }
                val folder = OutputNaming.forFolder(input.name, "split")
                val stem = OutputNaming.stem(input.name)
                ranges.mapIndexed { i, range ->
                    ctx.checkCancelled()
                    val label = if (range.first == range.last) "page ${range.first}" else "pages ${range.first}-${range.last}"
                    val out = ctx.newFile("$stem ($label).pdf")
                    PDDocument(memory(ctx)).use { part ->
                        for (p in range) part.importPage(doc.getPage(p - 1))
                        part.save(out)
                    }
                    progress((i + 1f) / ranges.size)
                    Produced(out, out.name, "application/pdf", folder)
                }
            }
        }.flatten()
        return OpResult(files)
    }

    private fun parseRanges(text: String, count: Int): List<IntRange> = try {
        PageRanges.parse(text, count)
    } catch (e: PageRanges.InvalidRange) {
        throw OpFailure("error_page_range", e.message)
    }

    suspend fun rotate(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val degrees = ctx.intOption("angle", 90).let { ((it % 360) + 360) % 360 }
        val files = ctx.eachInput(inputs) { input, progress ->
            open(ctx, input.file).use { doc ->
                val pages = PageRanges.pageIndexes(parseRanges(ctx.option("pages", ""), doc.numberOfPages))
                for (i in pages) {
                    val page = doc.getPage(i)
                    page.rotation = ((page.rotation + degrees) % 360 + 360) % 360
                }
                val out = ctx.newFile(OutputNaming.forFile(input.name, "pdf", "rotated"))
                doc.save(out)
                progress(1f)
                pdf(out)
            }
        }
        return OpResult(files)
    }

    suspend fun stripMetadata(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            open(ctx, input.file).use { doc ->
                doc.documentInformation = PDDocumentInformation()
                doc.documentCatalog.metadata = null
                doc.documentCatalog.cosObject.removeItem(COSName.PIECE_INFO)
                for (page in doc.pages) {
                    page.metadata = null
                    page.cosObject.removeItem(COSName.PIECE_INFO)
                }
                val out = ctx.newFile(OutputNaming.forFile(input.name, "pdf", "cleaned"))
                doc.save(out)
                progress(1f)
                pdf(out)
            }
        }
        return OpResult(files)
    }

    suspend fun extractText(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            open(ctx, input.file).use { doc ->
                progress(null)
                val text = PDFTextStripper().apply { sortByPosition = true }.getText(doc)
                val out = ctx.newFile(OutputNaming.forFile(input.name, "txt"))
                out.writeText(text, Charsets.UTF_8)
                progress(1f)
                Produced(out, out.name, "text/plain")
            }
        }
        return OpResult(files)
    }

    /** Recompresses large embedded images as JPEG, downscaled to the level's longest side. */
    suspend fun compress(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val (maxSide, quality) = when (ctx.option("pdf_level", "balanced")) {
            "light" -> 2400 to 0.85f
            "strong" -> 1100 to 0.5f
            else -> 1600 to 0.7f
        }
        val files = ctx.eachInput(inputs) { input, progress ->
            open(ctx, input.file).use { doc ->
                val cache = HashMap<COSBase, PDImageXObject?>()
                val count = doc.numberOfPages
                doc.pages.forEachIndexed { i, page ->
                    ctx.checkCancelled()
                    shrinkImages(doc, page.resources, 0, cache, maxSide, quality)
                    progress((i + 1f) / count)
                }
                val out = ctx.newFile(OutputNaming.forFile(input.name, "pdf", "compressed"))
                doc.save(out)
                pdf(out)
            }
        }
        return OpResult(files)
    }

    private fun shrinkImages(
        doc: PDDocument, resources: PDResources?, depth: Int, cache: HashMap<COSBase, PDImageXObject?>, maxSide: Int, quality: Float,
    ) {
        if (resources == null || depth > 5) return
        for (name in resources.xObjectNames.toList()) {
            val xo = try {
                resources.getXObject(name)
            } catch (_: IOException) {
                continue
            }
            when (xo) {
                is PDImageXObject -> {
                    val key: COSBase = xo.cosObject
                    val replacement = if (cache.containsKey(key)) cache[key] else shrink(doc, xo, maxSide, quality).also { cache[key] = it }
                    if (replacement != null) resources.put(name, replacement)
                }
                is PDFormXObject -> shrinkImages(doc, xo.resources, depth + 1, cache, maxSide, quality)
            }
        }
    }

    private fun shrink(doc: PDDocument, image: PDImageXObject, maxSide: Int, quality: Float): PDImageXObject? = try {
        val stream = image.cosObject
        val skip = image.isStencil ||
            stream.containsKey(COSName.SMASK) || stream.containsKey(COSName.MASK) ||
            image.width.toLong() * image.height < 128L * 128 ||
            image.width.toLong() * image.height > 40_000_000L
        if (skip) {
            null
        } else {
            val bitmap: Bitmap? = image.image
            if (bitmap == null) {
                null
            } else {
                val s = min(1f, maxSide.toFloat() / max(bitmap.width, bitmap.height))
                val scaled = if (s < 1f) Bitmap.createScaledBitmap(bitmap, max(1, (bitmap.width * s).toInt()), max(1, (bitmap.height * s).toInt()), true) else bitmap
                val jpeg = JPEGFactory.createFromImage(doc, scaled, quality)
                if (jpeg.cosObject.length < stream.length * 0.9) jpeg else null
            }
        }
    } catch (_: Exception) {
        null // an image PdfBox cannot decode stays as it was
    } catch (_: OutOfMemoryError) {
        null
    }
}
