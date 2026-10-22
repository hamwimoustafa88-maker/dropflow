// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OutputNamingTest {
    @Test
    fun `converted name sits beside the original name`() {
        assertEquals("photo (converted).jpg", OutputNaming.forFile("photo.png", "jpg"))
        assertEquals("photo (compressed).png", OutputNaming.forFile("photo.png", null, "compressed"))
        assertEquals("my.holiday (converted).webp", OutputNaming.forFile("my.holiday.heic", ".webp"))
        assertEquals("صورة (converted).png", OutputNaming.forFile("صورة.jpg", "png"))
        assertEquals("README (converted)", OutputNaming.forFile("README", null))
    }

    @Test
    fun `taken names count up inside the parentheses like the Windows app`() {
        val taken = setOf("a (converted).jpg", "a (converted 2).jpg")
        assertEquals("a (converted 3).jpg", OutputNaming.unique("a (converted).jpg") { it in taken })
        assertEquals("a (converted).jpg", OutputNaming.unique("a (converted).jpg") { false })
        assertEquals("qr-code (2).png", OutputNaming.unique("qr-code.png") { it == "qr-code.png" })
        assertEquals("report (pages 2)", OutputNaming.unique("report (pages)") { it == "report (pages)" })
        assertEquals("v1.2 (pages 2)", OutputNaming.unique("v1.2 (pages)") { it == "v1.2 (pages)" })
    }

    @Test
    fun `folder names for many results`() {
        assertEquals("report (pages)", OutputNaming.forFolder("report.pdf", "pages"))
    }

    @Test
    fun `names from other apps cannot carry paths or forbidden characters`() {
        assertEquals("passwd", OutputNaming.sanitize("../../etc/passwd"))
        assertEquals("x.txt", OutputNaming.sanitize("C:\\Windows\\x.txt"))
        assertEquals("a_b_.jpg", OutputNaming.sanitize("a:b?.jpg"))
        assertEquals("file", OutputNaming.sanitize(""))
        assertEquals("file", OutputNaming.sanitize(null))
        assertEquals("file", OutputNaming.sanitize(".."))
        assertEquals("name", OutputNaming.sanitize("name..."))
        assertEquals("tab.png", OutputNaming.sanitize("ta\u0000b\n.png").replace("\t", ""))
        val long = "x".repeat(300) + ".jpeg"
        val cut = OutputNaming.sanitize(long)
        assertTrue(cut.length <= 120)
        assertTrue(cut.endsWith(".jpeg"))
        assertFalse(cut.contains('/'))
    }

    @Test
    fun `stem drops only the last extension`() {
        assertEquals("archive.tar", OutputNaming.stem("archive.tar.gz"))
        assertEquals(".hidden", OutputNaming.stem(".hidden"))
    }
}
