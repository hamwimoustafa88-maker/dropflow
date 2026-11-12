// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * XMP is a second metadata block next to EXIF. Editors such as Lightroom copy the GPS position into it
 * (`exif:GPSLatitude`), and IPTC adds place names, so "remove location" must look there too.
 */
object Xmp {
    private val location = Regex(
        "(exif|exifEX):GPS[A-Za-z]*|Iptc4xmpExt:Location(Created|Shown)|photoshop:(City|State|Country)|Iptc4xmpCore:Location",
    )

    fun hasLocation(xmp: String): Boolean = location.containsMatchIn(xmp)
}
