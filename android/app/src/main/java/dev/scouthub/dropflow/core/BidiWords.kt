// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * PdfBox-Android (2.0.x) applies the bidirectional algorithm to each word on its own, so every Arabic word comes
 * out correctly spelled but the words of a line stay in their left-to-right page order: "مرحبا بالعالم" was
 * extracted as "بالعالم مرحبا". This puts the words of one line back in reading order.
 *
 * Input: the words of a line from left to right on the page, each already in logical order. Output: the words in
 * reading (logical) order. The paragraph direction is right-to-left when the outermost strong words say so.
 */
object BidiWords {
    private enum class Dir { L, R, NUM, NEUTRAL }

    private fun dirOf(word: String): Dir {
        var digits = false
        for (ch in word) {
            when (Character.getDirectionality(ch)) {
                Character.DIRECTIONALITY_RIGHT_TO_LEFT, Character.DIRECTIONALITY_RIGHT_TO_LEFT_ARABIC -> return Dir.R
                Character.DIRECTIONALITY_LEFT_TO_RIGHT -> return Dir.L
                Character.DIRECTIONALITY_EUROPEAN_NUMBER, Character.DIRECTIONALITY_ARABIC_NUMBER -> digits = true
            }
        }
        return if (digits) Dir.NUM else Dir.NEUTRAL
    }

    private fun letters(words: List<String>, rtl: Boolean): Int = words.sumOf { w ->
        w.count {
            val d = Character.getDirectionality(it)
            if (rtl) d == Character.DIRECTIONALITY_RIGHT_TO_LEFT || d == Character.DIRECTIONALITY_RIGHT_TO_LEFT_ARABIC
            else d == Character.DIRECTIONALITY_LEFT_TO_RIGHT
        }
    }

    fun logicalOrder(visualWords: List<String>): List<String> {
        if (visualWords.size < 2) return visualWords
        val dirs = visualWords.map(::dirOf)
        if (Dir.R !in dirs) return visualWords
        val strong = dirs.filter { it == Dir.L || it == Dir.R }
        val rtlBase = when {
            strong.first() == Dir.R && strong.last() == Dir.R -> true
            strong.first() == Dir.L && strong.last() == Dir.L -> false
            else -> letters(visualWords, rtl = true) >= letters(visualWords, rtl = false)
        }
        val levels = ByteArray(dirs.size) { i ->
            when (dirs[i]) {
                Dir.R -> 1
                Dir.L -> if (rtlBase) 2 else 0
                Dir.NUM -> if (rtlBase) 2 else 0
                Dir.NEUTRAL -> {
                    val left = dirs.subList(0, i).lastOrNull { it == Dir.L || it == Dir.R }
                    val right = dirs.subList(i + 1, dirs.size).firstOrNull { it == Dir.L || it == Dir.R }
                    if (left == Dir.R && right == Dir.R) 1 else if (rtlBase) 1 else 0
                }
            }.toByte()
        }
        // Reordering by levels is its own inverse here (each run is reversed in place), so applying the visual
        // reordering to the page order gives the reading order.
        val items: Array<Any> = visualWords.toTypedArray()
        java.text.Bidi.reorderVisually(levels, 0, items, 0, items.size)
        return items.map { it as String }
    }
}
