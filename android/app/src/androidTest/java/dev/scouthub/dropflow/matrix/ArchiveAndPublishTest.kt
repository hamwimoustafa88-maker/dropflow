// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.scouthub.dropflow.ops.Produced
import dev.scouthub.dropflow.work.Publisher
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.zip.ZipFile

@RunWith(AndroidJUnit4::class)
class ArchiveAndPublishTest {
    private val m = Matrix()
    private val s = Samples(m.context)

    @Test
    fun archives() {
        m.row("JPEG + PDF + Arabic-named text", "arc.zip") {
            val inputs = listOf(m.input(s.jpegWithExif()), m.input(s.pdfEnglish()), m.input(s.text("ملاحظة.txt", "مرحبا")))
            val out = m.ok(m.run("arc.zip", inputs)).files.single()
            ZipFile(out.file).use { z ->
                val names = z.entries().toList().map { it.name }
                assertEquals(listOf("camera photo.jpg", "English report.pdf", "ملاحظة.txt"), names)
                assertEquals("مرحبا", z.getInputStream(z.getEntry("ملاحظة.txt")).readBytes().toString(Charsets.UTF_8))
            }
            assertEquals("application/zip", out.mime)
            "${out.name}: 3 entries, UTF-8 names"
        }
        m.row("ZIP with folders + Arabic name", "arc.extract") {
            val files = m.ok(m.run("arc.extract", listOf(m.input(s.zipNested())))).files
            val got = files.map { "${it.folder}/${it.name}" }.toSet()
            assertEquals(
                setOf("project files (extracted)/docs/readme.txt", "project files (extracted)/docs/ملاحظات.txt",
                    "project files (extracted)/images/red.png", "project files (extracted)/top.txt"),
                got,
            )
            assertTrue("archive contents stay together in Download", files.all { it.inDownloads })
            assertEquals("مرحبا", files.single { it.name == "ملاحظات.txt" }.file.readText())
            assertEquals("image/png", files.single { it.name == "red.png" }.mime)
            "${files.size} files in their folders"
        }
        m.row("ZIP with DOS (CP437) names", "arc.extract") {
            val files = m.ok(m.run("arc.extract", listOf(m.input(s.zipCp437())))).files
            assertEquals(setOf("Résumé.txt", "menu.txt"), files.map { it.name }.toSet())
            assertEquals("old windows (extracted)/café", files.single { it.name == "menu.txt" }.folder)
            "names decoded: ${files.joinToString { it.name }}"
        }
        m.row("ZIP with ../../evil.txt", "arc.extract") {
            val msg = m.refused(m.run("arc.extract", listOf(m.input(s.zipSlip()))), "error_zip_unsafe")
            assertTrue("nothing written outside", !File(m.context.cacheDir, "evil.txt").exists() && !File(m.context.cacheDir.parentFile, "evil.txt").exists())
            msg
        }
        m.row("Empty ZIP", "arc.extract") { m.refused(m.run("arc.extract", listOf(m.input(s.zipEmpty()))), "error_zip_empty") }
        m.row("Text named .zip", "arc.extract") { m.refused(m.run("arc.extract", listOf(m.input(s.text("broken.zip", "nope")))), "error_zip_damaged") }
        m.finish()
    }

    /** Results really land in MediaStore in the right folder, with the bytes intact. Cleans up after itself. */
    @Test
    fun publishing() {
        val resolver = m.context.contentResolver
        val cases = listOf(
            Produced(s.pngAlpha(), "matrix test (converted).png", "image/png") to "Pictures/DropFlow/",
            Produced(s.pdfEnglish(), "matrix test (converted).pdf", "application/pdf") to "Download/DropFlow/",
            Produced(s.wav(), "matrix test (converted).wav", "audio/x-wav") to "Music/DropFlow/",
            Produced(s.text("t.txt", "x"), "matrix test.txt", "text/plain", folder = "matrix (extracted)/sub", inDownloads = true) to
                "Download/DropFlow/matrix (extracted)/sub/",
        ) + listOfNotNull(s.mp4("pub.mp4")?.let { Produced(it, "matrix test (converted).mp4", "video/mp4") to "Movies/DropFlow/" })
        for ((item, folder) in cases) {
            m.row("publish ${item.name}", "Publisher → $folder") {
                val a = Publisher.publish(m.context, item)
                val b = Publisher.publish(m.context, item)
                try {
                    assertEquals(folder.trimEnd('/'), a.location)
                    assertEquals("second copy counts up", item.name.substringBeforeLast('.').let { stem ->
                        if (stem.endsWith(")")) stem.dropLast(1) + " 2)" else "$stem (2)"
                    } + "." + item.name.substringAfterLast('.'), b.name)
                    resolver.query(a.uri, arrayOf(MediaStore.MediaColumns.RELATIVE_PATH, MediaStore.MediaColumns.IS_PENDING, MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)!!.use { c ->
                        assertTrue(c.moveToFirst())
                        assertEquals(folder, c.getString(0))
                        assertEquals("not pending", 0, c.getInt(1))
                        assertEquals(item.name, c.getString(2))
                    }
                    assertArrayEquals("bytes intact", item.file.readBytes(), resolver.openInputStream(a.uri)!!.use { it.readBytes() })
                    "${a.location}/${a.name} (${a.mime}), then ${b.name}"
                } finally {
                    resolver.delete(a.uri, null, null)
                    resolver.delete(b.uri, null, null)
                }
            }
        }
        m.finish()
    }
}
