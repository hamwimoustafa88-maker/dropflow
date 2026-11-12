// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/** Whether text pulled out of a PDF is real text or only the page breaks and spacing of a scanned (image-only) PDF. */
object TextLayer {
    fun hasText(text: String): Boolean = text.any { it.isLetterOrDigit() }
}
