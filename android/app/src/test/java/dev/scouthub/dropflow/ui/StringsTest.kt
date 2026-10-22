// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

/** Arabic and English must stay in step, and every key the code looks up must exist in both. */
class StringsTest {
    private fun resDir(): File = generateSequence(File("").absoluteFile) { it.parentFile }
        .flatMap { sequenceOf(File(it, "src/main/res"), File(it, "app/src/main/res"), File(it, "android/app/src/main/res")) }
        .first { File(it, "values/strings.xml").exists() }

    private data class Entry(val name: String, val translatable: Boolean)

    private fun read(file: File): List<Entry> {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file)
        val out = mutableListOf<Entry>()
        for (tag in listOf("string", "plurals")) {
            val nodes = doc.getElementsByTagName(tag)
            for (i in 0 until nodes.length) {
                val e = nodes.item(i) as Element
                out += Entry(e.getAttribute("name"), e.getAttribute("translatable") != "false")
            }
        }
        return out
    }

    @Test
    fun `arabic has exactly the translatable english keys`() {
        val en = read(File(resDir(), "values/strings.xml"))
        val ar = read(File(resDir(), "values-ar/strings.xml"))
        val translatable = en.filter { it.translatable }.map { it.name }.toSortedSet()
        val arabic = ar.map { it.name }.toSortedSet()
        assertEquals("missing in Arabic: ${translatable - arabic}; extra in Arabic: ${arabic - translatable}", translatable, arabic)
    }

    @Test
    fun `every key in the label table exists in english`() {
        val en = read(File(resDir(), "values/strings.xml")).map { it.name }.toSet()
        for (key in Labels.strings.keys) assertTrue("$key missing from values/strings.xml", key in en)
    }

    @Test
    fun `every error key raised by the operations has a message`() {
        val sources = File(resDir().parentFile, "java")
        val raised = sources.walkTopDown().filter { it.extension == "kt" }
            .flatMap { Regex("OpFailure\\(\"(error_[a-z_]+)\"").findAll(it.readText()).map { m -> m.groupValues[1] } }
            .toSet()
        assertTrue(raised.isNotEmpty())
        for (key in raised) assertTrue("$key has no message in Labels", key in Labels.errorKeys)
    }
}
