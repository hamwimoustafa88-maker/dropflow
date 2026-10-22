// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * Finds the highest encoder quality whose output fits a size budget, by binary search over [minQuality]..[maxQuality].
 * The encoder is assumed to grow (or stay equal) with quality, which holds for JPEG and lossy WebP in practice;
 * if it does not, the result is still a size that fits, just maybe not the best one.
 */
object TargetSize {
    data class Result<T>(val quality: Int, val value: T, val size: Long, val fits: Boolean, val attempts: Int)

    fun <T> search(
        targetBytes: Long,
        minQuality: Int = 5,
        maxQuality: Int = 95,
        encode: (quality: Int) -> T,
        sizeOf: (T) -> Long,
    ): Result<T> {
        require(targetBytes > 0) { "targetBytes must be positive" }
        require(minQuality in 1..maxQuality && maxQuality <= 100) { "bad quality range" }
        var attempts = 0
        fun run(q: Int): Pair<T, Long> {
            attempts++
            val v = encode(q)
            return v to sizeOf(v)
        }

        val (top, topSize) = run(maxQuality)
        if (topSize <= targetBytes) return Result(maxQuality, top, topSize, true, attempts)

        val (bottom, bottomSize) = run(minQuality)
        if (bottomSize > targetBytes) return Result(minQuality, bottom, bottomSize, false, attempts)

        var best = Triple(minQuality, bottom, bottomSize)
        var lo = minQuality + 1
        var hi = maxQuality - 1
        while (lo <= hi) {
            val mid = (lo + hi) ushr 1
            val (v, size) = run(mid)
            if (size <= targetBytes) {
                best = Triple(mid, v, size)
                lo = mid + 1
            } else {
                hi = mid - 1
            }
        }
        return Result(best.first, best.second, best.third, true, attempts)
    }

    /** "500 KB" style budgets typed by people (KB = 1024 bytes, like the Windows app); null if not a number. */
    fun parseKilobytes(text: String): Long? {
        val n = PageRanges.normalizeDigits(text).trim().replace(',', '.').toDoubleOrNull() ?: return null
        if (n <= 0 || n > 4_000_000) return null
        return (n * 1024).toLong()
    }
}
