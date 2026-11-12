// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.tom_roush.pdfbox.android.PDFBoxResourceLoader
import com.tom_roush.pdfbox.pdmodel.PDDocument
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class PdfToolsTest {
    private val m = Matrix()
    private val s = Samples(m.context)

    private data class Sample(val label: String, val file: File, val pages: Int, val text: List<String>?)

    private fun samples() = listOfNotNull(
        Sample("English PDF, 3 pages", s.pdfEnglish(), 3, listOf("The quick brown fox jumps over the lazy dog.", "DropFlow test page 3")),
        Sample("Arabic PDF, 2 pages (Android/Skia, like Chrome print)", s.pdfArabic(), 2, s.arabicLines + "صفحة 2" + s.arabicMixed),
        s.pdfArabicPresentationForms()?.let { Sample("Arabic PDF (presentation forms, visual order)", it, 1, s.arabicLines) },
        Sample("Scanned PDF (images only), 2 pages", s.pdfScanned(), 2, null),
        Sample("PDF, 12 pages", s.pdfManyPages(), 12, listOf("Page 12")),
        Sample("PDF with a 2000x1500 photo", s.pdfWithBigImage(), 1, null),
    )

    private fun pdfbox(f: File): PDDocument {
        PDFBoxResourceLoader.init(m.context)
        return PDDocument.load(f)
    }

    @Test
    fun everyPdfTool() {
        val all = samples()
        for (smp in all) {
            val input = m.input(smp.file)
            for ((key, ext, dpi) in listOf(Triple("convert.pdf.png", "png", "150"), Triple("convert.pdf.jpg", "jpg", "72"), Triple("pdf.to-images", "png", "300"))) {
                m.row(smp.label, "$key dpi $dpi") {
                    val files = m.ok(m.run(key, listOf(input), mapOf("dpi" to dpi))).files
                    assertEquals("one image per page", smp.pages, files.size)
                    val img = m.image(files.first().file)
                    assertEquals(if (ext == "png") "image/png" else "image/jpeg", img.mime)
                    val pageWidth = android.os.ParcelFileDescriptor.open(smp.file, android.os.ParcelFileDescriptor.MODE_READ_ONLY).use { fd ->
                        android.graphics.pdf.PdfRenderer(fd).use { r -> r.openPage(0).use { it.width } }
                    }
                    val expectedW = pageWidth * dpi.toInt() / 72
                    assertTrue("width ~$expectedW, got ${img.width}", kotlin.math.abs(img.width - expectedW) <= 2)
                    if (smp.pages > 1) assertEquals("pages go in one folder", "${smp.file.name.removeSuffix(".pdf")} (pages)", files.first().folder)
                    "${files.size} × ${img.width}x${img.height} ${img.mime}"
                }
            }
            for (key in listOf("convert.pdf.txt", "pdf.extract-text")) {
                m.row(smp.label, key) {
                    val r = m.run(key, listOf(input))
                    if (smp.text == null) return@row m.refused(r, "error_pdf_no_text")
                    val out = m.ok(r).files.single()
                    assertEquals("text/plain", out.mime)
                    val text = out.file.readText(Charsets.UTF_8)
                    for (line in smp.text) assertTrue("missing '$line' in:\n$text", text.contains(line))
                    "found ${smp.text.size} lines in logical order: ${smp.text.first()}"
                }
            }
            m.row(smp.label, "pdf.compress balanced") {
                val r = m.run("pdf.compress", listOf(input), mapOf("pdf_level" to "balanced"))
                // Only the photo PDF has something to shrink; the others may be refused as "already small".
                if (r.isFailure && !smp.label.contains("photo")) return@row m.refused(r, "error_already_small")
                val out = m.ok(r).files.single()
                assertTrue("never bigger", out.file.length() < smp.file.length())
                assertEquals(smp.pages, m.pdfPages(out.file))
                if (smp.label.contains("photo")) assertTrue("photo PDF shrinks: ${smp.file.length()} -> ${out.file.length()}", out.file.length() < smp.file.length() * 0.8)
                "${smp.file.length() / 1024} KB -> ${out.file.length() / 1024} KB, ${smp.pages} pages"
            }
            m.row(smp.label, "pdf.split each") {
                val files = m.ok(m.run("pdf.split", listOf(input), mapOf("split_mode" to "each"))).files
                assertEquals(smp.pages, files.size)
                files.forEach { assertEquals(1, m.pdfPages(it.file)) }
                "${files.size} one-page PDFs: ${files.first().name}"
            }
            m.row(smp.label, "pdf.rotate 90 page 1") {
                val out = m.ok(m.run("pdf.rotate", listOf(input), mapOf("angle" to "90", "pages" to "1"))).files.single()
                pdfbox(out.file).use { d ->
                    assertEquals(90, d.getPage(0).rotation)
                    if (d.numberOfPages > 1) assertEquals("other pages untouched", 0, d.getPage(1).rotation)
                }
                "page 1 at 90°, others 0°"
            }
            m.row(smp.label, "pdf.metadata (remove)") {
                val out = m.ok(m.run("pdf.metadata", listOf(input))).files.single()
                pdfbox(out.file).use { d ->
                    assertNull("producer", d.documentInformation.producer)
                    assertNull("creator", d.documentInformation.creator)
                    assertNull("XMP", d.documentCatalog.metadata)
                    assertEquals(smp.pages, d.numberOfPages)
                }
                "info dictionary and XMP empty"
            }
        }
        m.row("12-page PDF", "pdf.split ranges '1-3, 5, 8-'") {
            val files = m.ok(m.run("pdf.split", listOf(m.input(s.pdfManyPages())), mapOf("split_mode" to "ranges", "ranges" to "1-3, 5, 8-"))).files
            assertEquals(listOf(3, 1, 5), files.map { m.pdfPages(it.file) })
            "3 parts: ${files.joinToString { it.name }}"
        }
        m.row("12-page PDF", "pdf.split ranges Arabic digits '٢-٤'") {
            val files = m.ok(m.run("pdf.split", listOf(m.input(s.pdfManyPages())), mapOf("split_mode" to "ranges", "ranges" to "٢-٤"))).files
            assertEquals(listOf(3), files.map { m.pdfPages(it.file) })
            "1 part of 3 pages"
        }
        m.row("12-page PDF", "pdf.split ranges '20'") {
            m.refused(m.run("pdf.split", listOf(m.input(s.pdfManyPages())), mapOf("split_mode" to "ranges", "ranges" to "20")), "error_page_range")
        }
        m.row("English + Arabic + 12-page", "pdf.merge") {
            val out = m.ok(m.run("pdf.merge", listOf(m.input(s.pdfEnglish()), m.input(s.pdfArabic()), m.input(s.pdfManyPages())))).files.single()
            assertEquals(3 + 2 + 12, m.pdfPages(out.file))
            "17 pages, ${out.name}"
        }
        val locked = m.input(s.pdfProtected())
        for ((key, opts) in listOf(
            "convert.pdf.png" to emptyMap(), "convert.pdf.txt" to emptyMap(), "pdf.compress" to emptyMap(), "pdf.split" to emptyMap(),
            "pdf.rotate" to mapOf("angle" to "90"), "pdf.metadata" to emptyMap(), "pdf.to-images" to emptyMap(),
        )) {
            m.row("Protected PDF (AES-128, user password)", key) { m.refused(m.run(key, listOf(locked), opts), "error_pdf_protected") }
        }
        m.row("Protected PDF + English", "pdf.merge") { m.refused(m.run("pdf.merge", listOf(m.input(s.pdfEnglish()), locked)), "error_pdf_protected") }
        m.row("Not a PDF (text named .pdf)", "convert.pdf.txt") {
            m.refused(m.run("convert.pdf.txt", listOf(m.input(s.text("fake.pdf", "hello")))), "error_pdf_damaged")
        }
        m.finish()
    }
}
