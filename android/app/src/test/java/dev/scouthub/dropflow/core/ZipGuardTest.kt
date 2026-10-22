// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class ZipGuardTest {
    @get:Rule
    val tmp = TemporaryFolder()

    @Test
    fun `normal entries stay inside the destination`() {
        val dest = tmp.newFolder("out")
        assertEquals(File(dest, "a.txt").canonicalFile, ZipGuard.resolve(dest, "a.txt"))
        assertEquals(File(dest, "dir${File.separator}b.txt").canonicalFile, ZipGuard.resolve(dest, "dir/b.txt"))
        assertEquals(File(dest, "c.txt").canonicalFile, ZipGuard.resolve(dest, "./c.txt"))
        assertEquals(File(dest, "d${File.separator}e").canonicalFile, ZipGuard.resolve(dest, "d//e"))
    }

    @Test
    fun `zip slip entries are refused`() {
        val dest = tmp.newFolder("out")
        val evil = listOf(
            "../evil.txt", "a/../../evil.txt", "..", "a/..", "/etc/passwd", "\\windows\\x", "C:\\x.txt", "c:/x.txt",
            "a\\..\\..\\x", "~/x", "", "a\u0000b", ".", "./",
        )
        for (name in evil) {
            assertThrows("should refuse '$name'", ZipGuard.UnsafeEntry::class.java) { ZipGuard.resolve(dest, name) }
        }
    }

    @Test
    fun `a sibling folder with the same prefix is not inside`() {
        val dest = tmp.newFolder("out")
        tmp.newFolder("out-evil")
        assertThrows(ZipGuard.UnsafeEntry::class.java) { ZipGuard.resolve(dest, "../out-evil/x") }
    }

    @Test
    fun `a real archive with a traversal entry is caught before anything is written`() {
        val zip = File(tmp.root, "evil.zip")
        ZipOutputStream(zip.outputStream()).use { z ->
            z.putNextEntry(ZipEntry("good.txt")); z.write("ok".toByteArray()); z.closeEntry()
            z.putNextEntry(ZipEntry("../../escaped.txt")); z.write("bad".toByteArray()); z.closeEntry()
        }
        val dest = tmp.newFolder("x", "y")
        val refused = mutableListOf<String>()
        java.util.zip.ZipInputStream(zip.inputStream()).use { zin ->
            while (true) {
                val e = zin.nextEntry ?: break
                try {
                    val target = ZipGuard.resolve(dest, e.name)
                    target.parentFile?.mkdirs()
                    target.outputStream().use { zin.copyTo(it) }
                } catch (_: ZipGuard.UnsafeEntry) {
                    refused += e.name
                }
            }
        }
        assertEquals(listOf("../../escaped.txt"), refused)
        assertTrue(File(dest, "good.txt").exists())
        assertFalse(File(tmp.root, "escaped.txt").exists())
        assertFalse(File(dest.parentFile!!.parentFile, "escaped.txt").exists())
    }

    @Test
    fun `budget stops archives that expand too far`() {
        val b = ZipGuard.Budget(maxEntries = 2, maxBytes = 100)
        b.addEntry(); b.addEntry()
        assertThrows(ZipGuard.TooLarge::class.java) { b.addEntry() }
        val c = ZipGuard.Budget(maxEntries = 10, maxBytes = 100)
        c.addBytes(60)
        assertThrows(ZipGuard.TooLarge::class.java) { c.addBytes(41) }
    }
}
