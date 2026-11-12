// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import com.tom_roush.pdfbox.text.PDFTextStripper
import com.tom_roush.pdfbox.text.TextPosition
import dev.scouthub.dropflow.core.BidiWords

/**
 * PDFTextStripper that writes right-to-left lines in reading order. PdfBox-Android applies the bidirectional
 * algorithm per word only, so a line such as "مرحبا بالعالم" drawn word by word (Chrome, Android, Word) came out as
 * "بالعالم مرحبا". The words of each line are collected and put back in order with [BidiWords] before writing.
 */
class LogicalTextStripper : PDFTextStripper() {
    private val words = mutableListOf<String>()

    override fun writeString(text: String, textPositions: List<TextPosition>) {
        words += text
    }

    override fun writeWordSeparator() {
        // Written by flush(), between the reordered words.
    }

    private fun flush() {
        if (words.isEmpty()) return
        val ordered = BidiWords.logicalOrder(words.toList())
        words.clear()
        for ((i, w) in ordered.withIndex()) {
            if (i > 0) super.writeWordSeparator()
            super.writeString(w)
        }
    }

    override fun writeLineSeparator() { flush(); super.writeLineSeparator() }
    override fun writeParagraphStart() { flush(); super.writeParagraphStart() }
    override fun writeParagraphEnd() { flush(); super.writeParagraphEnd() }
    override fun writeParagraphSeparator() { flush(); super.writeParagraphSeparator() }
    override fun writePageStart() { flush(); super.writePageStart() }
    override fun writePageEnd() { flush(); super.writePageEnd() }
    override fun startArticle(isLTR: Boolean) { flush(); super.startArticle(isLTR) }
    override fun endArticle() { flush(); super.endArticle() }
}
