// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ui

import android.os.Build
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.min
import androidx.compose.ui.unit.sp
import dev.scouthub.dropflow.wheel.WheelGeometry
import dev.scouthub.dropflow.wheel.WheelGeometry.Hit
import kotlin.math.hypot
import kotlin.math.sin

/**
 * DropFlow's radial wheel. Select by tapping a petal, or by putting a finger down (on the hub or a petal), sliding to
 * a petal and lifting; each petal crossed gives a haptic tick. Tapping the hub switches ring.
 *
 * The geometry is always laid out left-to-right (petal 0 at the top, clockwise) in Arabic too; only the label text
 * is shaped and aligned by its own direction.
 */
@Composable
fun Wheel(
    labels: List<String>,
    hubTitle: String,
    hubSubtitle: String,
    onSelect: (Int) -> Unit,
    onHub: () -> Unit,
    onOutside: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val view = LocalView.current
    val select by rememberUpdatedState(onSelect)
    val hub by rememberUpdatedState(onHub)
    val outside by rememberUpdatedState(onOutside)
    var hovered by remember { mutableStateOf<Int?>(null) }
    var hubPressed by remember { mutableStateOf(false) }

    CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
        BoxWithConstraints(modifier, contentAlignment = Alignment.Center) {
            val side = min(min(maxWidth, maxHeight), 400.dp)
            val density = LocalDensity.current
            val outerPx = with(density) { (side / 2 - 6.dp).toPx() }
            val innerPx = outerPx * 0.42f
            val hubPx = outerPx * 0.33f
            val count = labels.size.coerceAtLeast(1)
            val geometry = remember(count, outerPx) { WheelGeometry(count, hubPx, innerPx, outerPx) }
            val gapDeg = if (count > 1) 2.2f else 0f

            Box(
                Modifier
                    .size(side)
                    .pointerInput(geometry) {
                        awaitEachGesture {
                            val down = awaitFirstDown(requireUnconsumed = false)
                            down.consume()
                            val center = Offset(size.width / 2f, size.height / 2f)
                            val start = down.position - center
                            val startHit = geometry.hitTap(start.x, start.y)
                            val canSlide = startHit != Hit.None
                            var moved = false
                            var current = (startHit as? Hit.Petal)?.index
                            hovered = current
                            hubPressed = startHit == Hit.Hub
                            while (true) {
                                val event = awaitPointerEvent()
                                val change = event.changes.firstOrNull { it.id == down.id } ?: break
                                val rel = change.position - center
                                if (!change.pressed) {
                                    change.consume()
                                    val final = if (moved && canSlide) geometry.hitSlide(rel.x, rel.y) else geometry.hitTap(rel.x, rel.y)
                                    when {
                                        final is Hit.Petal && (canSlide || !moved) -> {
                                            view.performHapticFeedback(
                                                if (Build.VERSION.SDK_INT >= 30) HapticFeedbackConstants.CONFIRM else HapticFeedbackConstants.VIRTUAL_KEY,
                                            )
                                            select(final.index)
                                        }
                                        final == Hit.Hub && !moved -> {
                                            view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                                            hub()
                                        }
                                        startHit == Hit.None && !moved && hypot(rel.x, rel.y) > geometry.outerRadius -> outside()
                                    }
                                    break
                                }
                                if ((change.position - down.position).getDistance() > viewConfiguration.touchSlop) moved = true
                                if (moved && canSlide) {
                                    val h = geometry.hitSlide(rel.x, rel.y)
                                    val idx = (h as? Hit.Petal)?.index
                                    if (idx != current) {
                                        current = idx
                                        hovered = idx
                                        if (idx != null) view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                    }
                                    hubPressed = h == Hit.Hub
                                }
                                change.consume()
                            }
                            hovered = null
                            hubPressed = false
                        }
                    },
                contentAlignment = Alignment.Center,
            ) {
                Canvas(Modifier.fillMaxSize()) {
                    val c = Offset(size.width / 2f, size.height / 2f)
                    val edge = Stroke(width = 1.5.dp.toPx())
                    if (labels.size <= 1) {
                        val mid = (innerPx + outerPx) / 2f
                        drawCircle(if (hovered == 0) DfColors.Cyan else DfColors.Petal, mid, c, style = Stroke(outerPx - innerPx))
                        drawCircle(DfColors.PetalEdge, outerPx, c, style = edge)
                        drawCircle(DfColors.PetalEdge, innerPx, c, style = edge)
                    } else {
                        for (i in labels.indices) {
                            val startA = geometry.startAngle(i) + gapDeg / 2f
                            val sweep = geometry.sweep - gapDeg
                            val path = Path().apply {
                                arcTo(Rect(c, outerPx), startA, sweep, forceMoveTo = true)
                                arcTo(Rect(c, innerPx), startA + sweep, -sweep, forceMoveTo = false)
                                close()
                            }
                            drawPath(path, if (hovered == i) DfColors.Cyan else DfColors.Petal)
                            drawPath(path, if (hovered == i) DfColors.Cyan else DfColors.PetalEdge.copy(alpha = 0.55f), style = edge)
                        }
                    }
                    drawCircle(if (hubPressed) DfColors.Accent else DfColors.Primary, hubPx, c)
                    drawCircle(DfColors.Lavender.copy(alpha = 0.6f), hubPx, c, style = edge)
                }

                // Labels, placed at each petal's centre (LTR geometry); the text itself follows its own direction.
                val ringPx = outerPx - innerPx
                val midPx = (innerPx + outerPx) / 2f
                val chordPx = if (count == 1) ringPx * 2 else 2f * midPx * sin(Math.toRadians(geometry.sweep / 2.0)).toFloat()
                val labelWidth = with(density) { minOf(chordPx * 0.9f, ringPx * 1.05f).toDp() }
                labels.forEachIndexed { i, label ->
                    val (lx, ly) = if (count == 1) 0f to -midPx else geometry.labelCenter(i)
                    Text(
                        text = label,
                        modifier = Modifier
                            .offset(x = with(density) { lx.toDp() }, y = with(density) { ly.toDp() })
                            .width(labelWidth)
                            .semantics {
                                role = Role.Button
                                contentDescription = label
                                selected = hovered == i
                                onClick(label) {
                                    select(i)
                                    true
                                }
                            },
                        style = TextStyle(
                            color = if (hovered == i) DfColors.Navy else DfColors.Surface,
                            fontSize = if (labels.size > 6) 12.sp else 13.sp,
                            lineHeight = 15.sp,
                            fontWeight = FontWeight.Medium,
                            textAlign = TextAlign.Center,
                            textDirection = TextDirection.Content,
                        ),
                        maxLines = 3,
                        overflow = TextOverflow.Ellipsis,
                    )
                }

                Column(
                    Modifier
                        .width(with(density) { (hubPx * 1.7f).toDp() })
                        .semantics {
                            role = Role.Button
                            contentDescription = "$hubTitle. $hubSubtitle"
                            onClick(hubSubtitle) {
                                hub()
                                true
                            }
                        },
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(
                        hubTitle,
                        style = TextStyle(
                            color = DfColors.Surface, fontSize = 16.sp, fontWeight = FontWeight.Bold,
                            textAlign = TextAlign.Center, textDirection = TextDirection.Content,
                        ),
                        maxLines = 1,
                    )
                    Text(
                        hubSubtitle,
                        style = TextStyle(
                            color = DfColors.Surface.copy(alpha = 0.8f), fontSize = 11.sp,
                            textAlign = TextAlign.Center, textDirection = TextDirection.Content,
                        ),
                        maxLines = 2,
                    )
                }
            }
        }
    }
}
