// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.catalog

import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.ui.Labels
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/** Keeps the Android tool table tied to catalog/tools.json, the repository's single source of truth. */
class CatalogMappingTest {
    private val caps = AndroidTools.Capabilities(heicEncoder = true)

    @Test
    fun `the generated catalog matches catalog tools json`() {
        val json = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "catalog/tools.json") }
            .first { it.exists() }
            .readText()
        for (t in Catalog.tools + Catalog.actions) assertTrue("${t.id} missing from tools.json", json.contains("\"id\": \"${t.id}\""))
        val idCount = Regex("\"id\":\\s*\"(img|vid|aud|pdf|arc|gen|act)\\.").findAll(json).count()
        assertEquals("every catalog tool and action is generated", idCount, Catalog.tools.size + Catalog.actions.size)
        assertTrue(Catalog.categories.isNotEmpty())
    }

    @Test
    fun `every catalog id used on Android exists in the catalog`() {
        for (def in AndroidTools.all) {
            val id = def.catalogId ?: continue
            assertNotNull("${def.key} -> $id is not in catalog/tools.json", Catalog.tool(id))
        }
    }

    @Test
    fun `entries without a catalog label have their own string`() {
        for (def in AndroidTools.all) {
            if (def.catalogId == null) assertNotNull("${def.key} needs a labelKey", def.labelKey)
            def.labelKey?.let { assertTrue("$it missing from Labels", it in Labels.strings) }
        }
    }

    @Test
    fun `convert targets are outputs of the catalog category`() {
        for (def in AndroidTools.all.filter { it.ring == Ring.CONVERT }) {
            val target = assertNotNullAnd(def.target, def.key)
            assertEquals(1, def.kinds.size)
            val category = Catalog.category(def.kinds.single().catalogCategory!!)!!
            assertTrue("${def.key}: '$target' is not an output of ${category.id}", target in category.outputs)
        }
    }

    @Test
    fun `catalog multi-file tools combine on Android too`() {
        for (def in AndroidTools.all) {
            val entry = def.catalogId?.let(Catalog::tool) ?: continue
            if (entry.multi && def.op != Op.QR_CREATE) assertTrue("${def.key} should combine inputs", def.combine)
            entry.maxFiles?.let { max ->
                val own = def.maxFiles
                assertTrue("${def.key} allows more files than the catalog", own != null && own <= max)
            }
        }
    }

    @Test
    fun `keys are unique and every option has labels`() {
        assertEquals(AndroidTools.all.size, AndroidTools.all.map { it.key }.toSet().size)
        for (key in AndroidTools.optionLabelKeys()) assertTrue("$key missing from Labels", key in Labels.strings)
    }

    @Test
    fun `wheel for one image`() {
        val w = AndroidTools.wheelFor(listOf(FileKind.IMAGE), caps)
        assertEquals(listOf("jpg", "png", "webp", "heic", "pdf"), w[Ring.CONVERT]!!.map { it.target })
        val tools = w[Ring.TOOLS]!!.map { it.key }
        assertTrue(tools.containsAll(listOf("img.compress", "img.resize", "img.rotate", "img.strip-gps", "img.to-pdf", "img.qr", "arc.zip")))
        assertFalse("pdf.merge" in tools)
        assertEquals(listOf("act.share", "act.open-with", "act.copy"), w[Ring.ACTIONS]!!.map { it.key })
    }

    @Test
    fun `no HEIC target without an encoder`() {
        val w = AndroidTools.wheelFor(listOf(FileKind.IMAGE), AndroidTools.Capabilities(heicEncoder = false))
        assertFalse(w[Ring.CONVERT]!!.any { it.target == "heic" })
    }

    @Test
    fun `merge needs two PDFs and editors need one file`() {
        assertFalse(AndroidTools.wheelFor(listOf(FileKind.PDF), caps)[Ring.TOOLS]!!.any { it.key == "pdf.merge" })
        assertTrue(AndroidTools.wheelFor(listOf(FileKind.PDF, FileKind.PDF), caps)[Ring.TOOLS]!!.any { it.key == "pdf.merge" })
        val twoVideos = AndroidTools.wheelFor(listOf(FileKind.VIDEO, FileKind.VIDEO), caps)[Ring.TOOLS]!!.map { it.key }
        assertFalse("vid.trim" in twoVideos)
        assertTrue("vid.compress" in twoVideos)
        assertFalse(AndroidTools.wheelFor(listOf(FileKind.VIDEO, FileKind.VIDEO), caps)[Ring.ACTIONS]!!.any { it.key == "act.open-with" })
    }

    @Test
    fun `mixed selections only get tools for any file`() {
        val w = AndroidTools.wheelFor(listOf(FileKind.IMAGE, FileKind.PDF), caps)
        assertTrue(w[Ring.CONVERT]!!.isEmpty())
        assertEquals(listOf("arc.zip"), w[Ring.TOOLS]!!.map { it.key })
        assertTrue(w[Ring.ACTIONS]!!.isNotEmpty())
    }

    @Test
    fun `shared text offers a QR code`() {
        val w = AndroidTools.wheelFor(listOf(FileKind.TEXT), caps)
        assertTrue(w[Ring.TOOLS]!!.any { it.op == Op.QR_CREATE })
        assertFalse(w[Ring.ACTIONS]!!.any { it.op == Op.OPEN_WITH })
    }

    @Test
    fun `every file kind DropFlow detects gets at least one entry`() {
        for (kind in FileKind.entries) {
            val w = AndroidTools.wheelFor(listOf(kind), caps)
            assertTrue("$kind has an empty wheel", w.values.any { it.isNotEmpty() })
        }
        assertTrue(AndroidTools.wheelFor(emptyList(), caps).values.all { it.isEmpty() })
    }

    private fun <T : Any> assertNotNullAnd(v: T?, what: String): T {
        assertNotNull(what, v)
        return v!!
    }
}
