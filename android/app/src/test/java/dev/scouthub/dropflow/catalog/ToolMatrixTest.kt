// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.catalog

import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.work.Publisher
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Which inputs each tool accepts, where its results go, and whether the manifest lets those inputs in at all.
 * The expected lists are written out by hand on purpose: a change to the tool table must show up here.
 */
class ToolMatrixTest {
    private val caps = AndroidTools.Capabilities(heicEncoder = true)

    private fun keys(kinds: List<FileKind>): Map<Ring, List<String>> = AndroidTools.wheelFor(kinds, caps).mapValues { e -> e.value.map { it.key } }

    @Test
    fun `one file of each kind gets exactly these entries`() {
        val anyFile = listOf("arc.zip")
        val actions = listOf("act.share", "act.open-with", "act.copy")
        val expected = mapOf(
            FileKind.IMAGE to Triple(
                listOf("convert.image.jpg", "convert.image.png", "convert.image.webp", "convert.image.heic", "convert.image.pdf"),
                listOf("img.compress", "img.resize", "img.rotate", "img.strip-gps", "img.to-pdf", "img.qr") + anyFile,
                actions,
            ),
            FileKind.PDF to Triple(
                listOf("convert.pdf.png", "convert.pdf.jpg", "convert.pdf.txt"),
                listOf("pdf.compress", "pdf.split", "pdf.rotate", "pdf.metadata", "pdf.to-images", "pdf.extract-text") + anyFile,
                actions,
            ),
            FileKind.VIDEO to Triple(
                listOf("convert.video.mp4", "convert.video.mp4-hevc", "convert.video.m4a", "convert.video.wav"),
                listOf("vid.compress", "vid.trim", "vid.mute", "vid.snapshot") + anyFile,
                actions,
            ),
            FileKind.AUDIO to Triple(listOf("convert.audio.m4a", "convert.audio.wav"), listOf("aud.trim") + anyFile, actions),
            FileKind.ZIP to Triple(emptyList(), listOf("arc.extract") + anyFile, actions),
            FileKind.TEXT to Triple(emptyList(), listOf("qr.create") + anyFile, listOf("act.share", "act.copy")),
            FileKind.OTHER to Triple(emptyList(), anyFile, actions),
        )
        for (kind in FileKind.entries) {
            val (convert, tools, acts) = expected.getValue(kind)
            val w = keys(listOf(kind))
            assertEquals("$kind convert", convert, w[Ring.CONVERT])
            assertEquals("$kind tools", tools, w[Ring.TOOLS])
            assertEquals("$kind actions", acts, w[Ring.ACTIONS])
        }
    }

    @Test
    fun `several files of one kind lose the single-file editors and gain the combining tools`() {
        val twoImages = keys(listOf(FileKind.IMAGE, FileKind.IMAGE))
        assertTrue("img.to-pdf" in twoImages[Ring.TOOLS]!!)
        assertFalse("act.open-with" in twoImages[Ring.ACTIONS]!!)
        val twoPdfs = keys(listOf(FileKind.PDF, FileKind.PDF))
        assertTrue("pdf.merge" in twoPdfs[Ring.TOOLS]!!)
        val twoAudio = keys(listOf(FileKind.AUDIO, FileKind.AUDIO))
        assertFalse("aud.trim" in twoAudio[Ring.TOOLS]!!)
        assertEquals(listOf("convert.audio.m4a", "convert.audio.wav"), twoAudio[Ring.CONVERT])
        // A video and an audio file together: no per-kind tool fits both.
        val mixed = keys(listOf(FileKind.VIDEO, FileKind.AUDIO))
        assertEquals(emptyList<String>(), mixed[Ring.CONVERT])
        assertEquals(listOf("arc.zip"), mixed[Ring.TOOLS])
    }

    @Test
    fun `every op that runs as a job is reachable from some wheel`() {
        val reachable = FileKind.entries.flatMap { k -> AndroidTools.wheelFor(listOf(k, k), caps).values.flatten() + AndroidTools.wheelFor(listOf(k), caps).values.flatten() }
            .map { it.op }.toSet()
        assertEquals(Op.entries.toSet(), reachable)
    }

    @Test
    fun `results land in the folder that matches their type`() {
        assertEquals("Pictures", Publisher.directoryFor(FileKind.mimeFor("jpg")))
        assertEquals("Pictures", Publisher.directoryFor(FileKind.mimeFor("heic")))
        assertEquals("Movies", Publisher.directoryFor(FileKind.mimeFor("mp4")))
        assertEquals("Music", Publisher.directoryFor(FileKind.mimeFor("m4a")))
        assertEquals("Music", Publisher.directoryFor("audio/x-wav"))
        assertEquals("Download", Publisher.directoryFor(FileKind.mimeFor("pdf")))
        assertEquals("Download", Publisher.directoryFor(FileKind.mimeFor("txt")))
        assertEquals("Download", Publisher.directoryFor(FileKind.mimeFor("zip")))
        assertEquals("Download", Publisher.directoryFor("image/png", inDownloads = true))
        assertEquals("Pictures/DropFlow/report (pages)", Publisher.locationFor("image/png", "report (pages)"))
        assertEquals("Download/DropFlow/a (extracted)/sub", Publisher.locationFor("text/plain", "a (extracted)/sub", inDownloads = true))
        // Every convert target is written with a type of its own family.
        for (def in AndroidTools.all.filter { it.ring == Ring.CONVERT }) {
            val mime = FileKind.mimeFor(def.target!!)
            assertFalse("${def.key} -> $mime", mime == "application/octet-stream")
            assertEquals("${def.key}: output kind", FileKind.of("x.${def.target}", null), FileKind.of("x", mime))
        }
    }

    private fun manifest(): Element {
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .flatMap { sequenceOf(File(it, "src/main/AndroidManifest.xml"), File(it, "app/src/main/AndroidManifest.xml")) }
            .first { it.exists() }
        return DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true }.newDocumentBuilder().parse(file).documentElement
    }

    private fun Element.children(tag: String): List<Element> {
        val nodes = getElementsByTagName(tag)
        return (0 until nodes.length).map { nodes.item(it) as Element }
    }

    private val androidNs = "http://schemas.android.com/apk/res/android"

    @Test
    fun `the share target takes every file and Open with takes every kind with tools`() {
        val wheel = manifest().children("activity").single { it.getAttributeNS(androidNs, "name") == ".WheelActivity" }
        val filters = wheel.children("intent-filter").map { f ->
            f.children("action").map { it.getAttributeNS(androidNs, "name") } to f.children("data").mapNotNull { d ->
                d.getAttributeNS(androidNs, "mimeType").takeIf { it.isNotEmpty() }
            }
        }
        for (action in listOf("android.intent.action.SEND", "android.intent.action.SEND_MULTIPLE")) {
            assertTrue("$action takes */*", filters.any { (a, m) -> action in a && "*/*" in m })
        }
        val view = filters.single { (a, _) -> "android.intent.action.VIEW" in a }.second
        // Every MIME type FileKind maps to a kind with its own tools must reach DropFlow through "Open with".
        val mustOpen = listOf("image/jpeg", "image/heic", "video/mp4", "audio/mpeg", "application/pdf", "application/zip", "application/x-zip-compressed")
        for (mime in mustOpen) {
            val ok = view.any { it == mime || (it.endsWith("/*") && mime.startsWith(it.removeSuffix("*"))) }
            assertTrue("VIEW filter misses $mime", ok)
        }
    }

    @Test
    fun `the manifest asks for no network access`() {
        val perms = manifest().children("uses-permission")
        val internet = perms.filter { it.getAttributeNS(androidNs, "name") == "android.permission.INTERNET" }
        assertEquals(1, internet.size)
        assertEquals("remove", internet.single().getAttributeNS("http://schemas.android.com/tools", "node"))
    }
}
