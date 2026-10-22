// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** DropFlow identity (web/src/app/globals.css, icon.svg): indigo + cyan on white, the wheel in navy and lavender. */
object DfColors {
    val Bg = Color(0xFFF7F8FC)
    val Surface = Color(0xFFFFFFFF)
    val Surface2 = Color(0xFFECEFFA)
    val Fg = Color(0xFF0A0F24)
    val Muted = Color(0xFF4A5372)
    val Border = Color(0xFFDADFF0)
    val Primary = Color(0xFF4338CA)
    val PrimarySoft = Color(0xFFE3E8FF)
    val Accent = Color(0xFF0E7490)
    val Ok = Color(0xFF0B7A4B)
    val Error = Color(0xFFB42318)

    // The wheel (logo colours)
    val Navy = Color(0xFF070B1C)
    val Petal = Color(0xFF141B3D)
    val PetalEdge = Color(0xFF8B95FB)
    val Cyan = Color(0xFF22D3EE)
    val Lavender = Color(0xFF8B95FB)
    val Scrim = Color(0x99070B1C)
}

private val scheme = lightColorScheme(
    primary = DfColors.Primary,
    onPrimary = Color.White,
    primaryContainer = DfColors.PrimarySoft,
    onPrimaryContainer = DfColors.Fg,
    secondary = DfColors.Accent,
    onSecondary = Color.White,
    background = DfColors.Bg,
    onBackground = DfColors.Fg,
    surface = DfColors.Surface,
    onSurface = DfColors.Fg,
    surfaceVariant = DfColors.Surface2,
    onSurfaceVariant = DfColors.Muted,
    outline = DfColors.Border,
    error = DfColors.Error,
)

@Composable
fun DropFlowTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = scheme, content = content)
}
