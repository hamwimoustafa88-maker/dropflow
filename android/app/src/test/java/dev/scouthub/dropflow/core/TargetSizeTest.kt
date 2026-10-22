// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TargetSizeTest {
    /** A fake encoder whose size grows with quality, like JPEG. */
    private fun fake(q: Int): Long = 10_000L + q * q * 50L

    @Test
    fun `finds the highest quality that fits`() {
        val target = 300_000L
        val r = TargetSize.search(target, encode = { q -> q }, sizeOf = { fake(it) })
        assertTrue(r.fits)
        assertTrue(r.size <= target)
        // The next quality up must not fit, otherwise the search stopped too early.
        assertTrue(fake(r.quality + 1) > target)
        assertEquals(r.quality, r.value)
    }

    @Test
    fun `takes the best quality at once when it already fits`() {
        val r = TargetSize.search(10_000_000L, encode = { it }, sizeOf = { fake(it) })
        assertEquals(95, r.quality)
        assertEquals(1, r.attempts)
    }

    @Test
    fun `reports when even the lowest quality is too big`() {
        val r = TargetSize.search(5_000L, encode = { it }, sizeOf = { fake(it) })
        assertFalse(r.fits)
        assertEquals(5, r.quality)
    }

    @Test
    fun `binary search needs few encodes`() {
        val r = TargetSize.search(200_000L, encode = { it }, sizeOf = { fake(it) })
        assertTrue("attempts=${r.attempts}", r.attempts <= 9)
    }

    @Test
    fun `every target from tiny to huge gives a fitting or flagged result`() {
        for (target in listOf(10_300L, 11_000L, 50_000L, 123_456L, 451_000L, 461_250L)) {
            val r = TargetSize.search(target, encode = { it }, sizeOf = { fake(it) })
            if (r.fits) {
                assertTrue(r.size <= target)
                if (r.quality < 95) assertTrue(fake(r.quality + 1) > target)
            }
        }
    }

    @Test
    fun `kilobytes typed with Arabic digits`() {
        assertEquals(500L * 1024, TargetSize.parseKilobytes("500"))
        assertEquals(500L * 1024, TargetSize.parseKilobytes("٥٠٠"))
        assertEquals(512L, TargetSize.parseKilobytes("0,5"))
        assertNull(TargetSize.parseKilobytes("abc"))
        assertNull(TargetSize.parseKilobytes("-3"))
    }
}
