// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * EXIF orientation values (1..8) turned clockwise by a multiple of 90 degrees, with the same tables as
 * androidx ExifInterface.rotate(). Unlike ExifInterface, a missing or "undefined" (0) tag counts as upright (1):
 * ExifInterface leaves such files unchanged, so turning a JPEG without the tag (most screenshots, chat photos
 * and web images) did nothing.
 */
object ExifOrientation {
    private val upright = intArrayOf(1, 6, 3, 8)
    private val mirrored = intArrayOf(2, 7, 4, 5)

    fun rotated(current: Int, degrees: Int): Int {
        require(degrees % 90 == 0) { "only quarter turns" }
        val steps = ((degrees / 90) % 4 + 4) % 4
        val from = if (current in 1..8) current else 1
        for (table in listOf(upright, mirrored)) {
            val i = table.indexOf(from)
            if (i >= 0) return table[(i + steps) % 4]
        }
        return from
    }
}
