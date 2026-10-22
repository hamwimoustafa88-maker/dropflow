// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.wheel

import dev.scouthub.dropflow.wheel.WheelGeometry.Hit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.cos
import kotlin.math.sin

class WheelGeometryTest {
    private val g = WheelGeometry(count = 6, hubRadius = 50f, innerRadius = 80f, outerRadius = 200f)

    private fun at(angleDeg: Double, r: Float): Pair<Float, Float> {
        val a = Math.toRadians(angleDeg)
        return (r * cos(a)).toFloat() to (r * sin(a)).toFloat()
    }

    @Test
    fun `petal 0 is at the top and petals run clockwise`() {
        assertEquals(Hit.Petal(0), g.hitTap(0f, -150f)) // straight up (y grows downward)
        // 6 petals of 60 degrees: petal 1 is centred at -30 degrees (upper right), petal 2 at +30 (lower right)...
        val (x1, y1) = at(-30.0, 150f)
        assertEquals(Hit.Petal(1), g.hitTap(x1, y1))
        assertEquals(Hit.Petal(3), g.hitTap(0f, 150f)) // straight down
        val (x5, y5) = at(-150.0, 150f)
        assertEquals(Hit.Petal(5), g.hitTap(x5, y5)) // upper left
    }

    @Test
    fun `every petal centre hits its own petal`() {
        for (n in 1..12) {
            val w = WheelGeometry(n, 40f, 70f, 180f)
            for (i in 0 until n) {
                val (x, y) = w.labelCenter(i)
                assertEquals("count=$n index=$i", Hit.Petal(i), w.hitTap(x, y))
            }
        }
    }

    @Test
    fun `petal boundaries split exactly between neighbours`() {
        // The boundary between petal 0 and 1 is at -60 degrees; just either side of it.
        val (xa, ya) = at(-60.5, 150f)
        val (xb, yb) = at(-59.5, 150f)
        assertEquals(Hit.Petal(0), g.hitTap(xa, ya))
        assertEquals(Hit.Petal(1), g.hitTap(xb, yb))
        // Wrap-around at the top: -121 is petal 5, -119 is petal 0... (boundary of petal 0 on the left is -120)
        val (xc, yc) = at(-120.5, 150f)
        val (xd, yd) = at(-119.5, 150f)
        assertEquals(Hit.Petal(5), g.hitTap(xc, yc))
        assertEquals(Hit.Petal(0), g.hitTap(xd, yd))
    }

    @Test
    fun `hub, gap and outside`() {
        assertEquals(Hit.Hub, g.hitTap(0f, 0f))
        assertEquals(Hit.Hub, g.hitTap(30f, 30f))
        assertEquals(Hit.None, g.hitTap(0f, -65f)) // gap between hub and ring
        assertEquals(Hit.None, g.hitTap(0f, -400f)) // far outside: a tap there dismisses
        assertEquals(Hit.Petal(0), g.hitTap(0f, -210f)) // small tolerance past the outer edge
    }

    @Test
    fun `sliding picks by direction even past the edge`() {
        assertEquals(Hit.Hub, g.hitSlide(10f, 10f))
        assertEquals(Hit.None, g.hitSlide(0f, -60f)) // dead zone right outside the hub
        assertEquals(Hit.Petal(0), g.hitSlide(0f, -900f))
        assertEquals(Hit.Petal(3), g.hitSlide(0f, 900f))
    }

    @Test
    fun `a single petal takes every direction`() {
        val one = WheelGeometry(1, 40f, 70f, 180f)
        for (deg in -180..180 step 15) {
            val (x, y) = at(deg.toDouble(), 120f)
            assertEquals(Hit.Petal(0), one.hitTap(x, y))
        }
    }

    @Test
    fun `arcs start half a sweep before the centre`() {
        assertEquals(-120f, g.startAngle(0), 0.001f)
        assertEquals(-90f, g.centerAngle(0), 0.001f)
        assertEquals(150f, g.centerAngle(4), 0.001f)
    }

    @Test
    fun `pages keep a More petal when entries overflow`() {
        assertEquals(listOf(0 until 5), WheelGeometry.pages(5))
        assertEquals(listOf(0 until 8), WheelGeometry.pages(8))
        val nine = WheelGeometry.pages(9)
        assertEquals(listOf(0 until 7, 7 until 9), nine)
        val many = WheelGeometry.pages(20)
        assertEquals(3, many.size)
        assertTrue(many.all { it.count() <= 7 })
        assertEquals(20, many.sumOf { it.count() })
        assertTrue(WheelGeometry.pages(0).single().isEmpty())
    }
}
