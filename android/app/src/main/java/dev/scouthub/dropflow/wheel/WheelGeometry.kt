// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.wheel

import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.floor
import kotlin.math.hypot
import kotlin.math.sin

/**
 * Layout and hit testing of the radial wheel, in screen coordinates (x right, y down) relative to the wheel centre.
 *
 * Petal 0 is centred at the top and the order runs clockwise. The geometry is the same in Arabic and English (a wheel
 * has no reading direction, and muscle memory should not flip with the language); only the labels render RTL.
 */
class WheelGeometry(
    val count: Int,
    val hubRadius: Float,
    val innerRadius: Float,
    val outerRadius: Float,
) {
    init {
        require(count >= 1) { "A wheel needs at least one petal" }
        require(hubRadius > 0 && innerRadius >= hubRadius && outerRadius > innerRadius)
    }

    sealed interface Hit {
        data object Hub : Hit
        data class Petal(val index: Int) : Hit
        data object None : Hit
    }

    /** Angular size of one petal, degrees. */
    val sweep: Float = 360f / count

    /** Angle (degrees, 0 = +x, clockwise) of the middle of petal [index]. */
    fun centerAngle(index: Int): Float = -90f + index * sweep

    /** Angle where petal [index] starts (for drawing arcs, same convention as Android's Canvas.drawArc). */
    fun startAngle(index: Int): Float = centerAngle(index) - sweep / 2f

    /** Where the label of petal [index] goes. */
    fun labelCenter(index: Int): Pair<Float, Float> {
        val r = (innerRadius + outerRadius) / 2f
        val a = Math.toRadians(centerAngle(index).toDouble())
        return (r * cos(a)).toFloat() to (r * sin(a)).toFloat()
    }

    /** Petal index for a direction (dx, dy) from the centre, whatever the distance. */
    fun petalAt(dx: Float, dy: Float): Int {
        val deg = Math.toDegrees(atan2(dy.toDouble(), dx.toDouble())) // -180..180, clockwise because y points down
        val fromTop = ((deg + 90.0 + sweep / 2.0) % 360.0 + 360.0) % 360.0
        return floor(fromTop / sweep).toInt().coerceIn(0, count - 1)
    }

    /**
     * Tap hit test: the hub, a petal (inside the ring, with a small tolerance outward), or nothing (the gap between
     * hub and ring, or outside the wheel — a tap there dismisses the wheel).
     */
    fun hitTap(dx: Float, dy: Float): Hit {
        val r = hypot(dx, dy)
        return when {
            r <= hubRadius -> Hit.Hub
            r < innerRadius * 0.92f -> Hit.None
            r <= outerRadius * 1.08f -> Hit.Petal(petalAt(dx, dy))
            else -> Hit.None
        }
    }

    /**
     * Slide hit test while a finger drags from the hub: once the finger has left the hub (plus a dead zone), the
     * direction alone chooses the petal, even past the outer edge, so a quick flick works.
     */
    fun hitSlide(dx: Float, dy: Float): Hit {
        val r = hypot(dx, dy)
        return when {
            r <= hubRadius -> Hit.Hub
            r < hubRadius + (innerRadius - hubRadius) * 0.5f -> Hit.None
            else -> Hit.Petal(petalAt(dx, dy))
        }
    }

    companion object {
        const val MAX_PETALS = 8

        /**
         * Splits [total] entries into pages of at most [MAX_PETALS] petals. When there is more than one page, each page
         * keeps its last petal for "More", so it holds MAX_PETALS - 1 entries.
         */
        fun pages(total: Int, maxPetals: Int = MAX_PETALS): List<IntRange> {
            require(maxPetals >= 2)
            if (total <= 0) return listOf(IntRange.EMPTY)
            if (total <= maxPetals) return listOf(0 until total)
            val per = maxPetals - 1
            return (0 until total step per).map { start -> start until minOf(total, start + per) }
        }
    }
}
