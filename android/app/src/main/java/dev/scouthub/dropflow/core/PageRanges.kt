// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * Page selections typed by people: "1-3, 5, 8-" (1-based, inclusive). Arabic-Indic and Persian digits are accepted,
 * as are the Arabic comma and en/em dashes. An empty text means every page.
 */
object PageRanges {
    class InvalidRange(message: String) : IllegalArgumentException(message)

    fun parse(text: String, pageCount: Int): List<IntRange> {
        require(pageCount > 0) { "The document has no pages" }
        val normalized = normalizeDigits(text).replace('،', ',').replace('–', '-').replace('—', '-').replace(" ", "")
        if (normalized.isEmpty()) return listOf(1..pageCount)
        val ranges = mutableListOf<IntRange>()
        for (part in normalized.split(',', ';')) {
            if (part.isEmpty()) continue
            val range = when {
                part.contains('-') -> {
                    val a = part.substringBefore('-')
                    val b = part.substringAfter('-')
                    if (b.contains('-')) throw InvalidRange(part)
                    val start = if (a.isEmpty()) 1 else a.toIntOrNull() ?: throw InvalidRange(part)
                    val end = if (b.isEmpty()) pageCount else b.toIntOrNull() ?: throw InvalidRange(part)
                    start..end
                }
                else -> {
                    val n = part.toIntOrNull() ?: throw InvalidRange(part)
                    n..n
                }
            }
            if (range.first < 1 || range.last > pageCount || range.first > range.last) throw InvalidRange(part)
            ranges += range
        }
        if (ranges.isEmpty()) throw InvalidRange(text)
        return ranges
    }

    /** Every selected page once, in the order typed (0-based indexes). */
    fun pageIndexes(ranges: List<IntRange>): List<Int> {
        val seen = LinkedHashSet<Int>()
        for (r in ranges) for (p in r) seen += p - 1
        return seen.toList()
    }

    fun normalizeDigits(text: String): String = buildString(text.length) {
        for (ch in text) {
            append(
                when (ch) {
                    in '٠'..'٩' -> '0' + (ch - '٠')
                    in '۰'..'۹' -> '0' + (ch - '۰')
                    else -> ch
                }
            )
        }
    }
}
