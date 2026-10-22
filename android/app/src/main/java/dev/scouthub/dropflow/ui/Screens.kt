// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ui

import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RangeSlider
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.SeekParameters
import androidx.media3.ui.PlayerView
import dev.scouthub.dropflow.R
import dev.scouthub.dropflow.catalog.Editor
import dev.scouthub.dropflow.catalog.OptionSpec
import dev.scouthub.dropflow.catalog.Ring
import dev.scouthub.dropflow.catalog.ToolDef
import dev.scouthub.dropflow.ops.MediaOps
import dev.scouthub.dropflow.wheel.WheelGeometry
import dev.scouthub.dropflow.work.JobState
import dev.scouthub.dropflow.work.OutputItem
import java.io.File

// ------------------------------------------------------------------------------------------------------------ frame

/** The dim layer over the app that shared the files. A tap on it (not on a card) runs [onDismiss]. */
@Composable
fun Scrim(onDismiss: () -> Unit, content: @Composable () -> Unit) {
    val dismiss by rememberUpdatedState(onDismiss)
    Box(
        Modifier
            .fillMaxSize()
            .background(DfColors.Scrim)
            .pointerInput(Unit) { detectTapGestures { dismiss() } }
            .safeDrawingPadding()
            .imePadding(),
        contentAlignment = Alignment.Center,
    ) { content() }
}

/** A white card that keeps its taps from reaching the scrim. */
@Composable
fun SheetCard(content: @Composable ColumnScope.() -> Unit) {
    Card(
        modifier = Modifier
            .padding(16.dp)
            .widthIn(max = 520.dp)
            .fillMaxWidth()
            .pointerInput(Unit) { detectTapGestures { } },
        shape = RoundedCornerShape(24.dp),
        colors = CardDefaults.cardColors(containerColor = DfColors.Surface, contentColor = DfColors.Fg),
    ) {
        Column(
            Modifier
                .heightIn(max = 640.dp)
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            content = content,
        )
    }
}

@Composable
private fun ButtonsRow(content: @Composable () -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End), verticalAlignment = Alignment.CenterVertically) {
        content()
    }
}

@Composable
fun ringName(ring: Ring): String = when (ring) {
    Ring.CONVERT -> stringResource(R.string.tab_convert)
    Ring.TOOLS -> stringResource(R.string.tab_tools)
    Ring.ACTIONS -> stringResource(R.string.tab_actions)
}

// ------------------------------------------------------------------------------------------------------------ wheel

@Composable
fun WheelScreen(
    summary: String,
    rings: Map<Ring, List<ToolDef>>,
    label: (ToolDef) -> String,
    onChoose: (ToolDef) -> Unit,
    onDismiss: () -> Unit,
) {
    val available = Ring.entries.filter { rings[it].orEmpty().isNotEmpty() }
    var ring by rememberSaveable { mutableStateOf(available.first()) }
    var page by rememberSaveable { mutableIntStateOf(0) }
    val all = rings[ring].orEmpty()
    val pages = WheelGeometry.pages(all.size)
    val range = pages[page.coerceIn(0, pages.size - 1)]
    val more = stringResource(R.string.hub_more)
    val labels = all.slice(range).map(label) + if (pages.size > 1) listOf(more) else emptyList()

    Scrim(onDismiss) {
        Column(
            Modifier.padding(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text(
                summary,
                color = DfColors.Surface,
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier
                    .background(DfColors.Navy.copy(alpha = 0.75f), RoundedCornerShape(50))
                    .padding(horizontal = 14.dp, vertical = 6.dp),
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (r in Ring.entries) {
                    FilterChip(
                        selected = r == ring,
                        enabled = r in available,
                        onClick = {
                            ring = r
                            page = 0
                        },
                        label = { Text(ringName(r)) },
                        colors = FilterChipDefaults.filterChipColors(
                            containerColor = DfColors.Navy.copy(alpha = 0.8f),
                            labelColor = DfColors.Surface,
                            selectedContainerColor = DfColors.Cyan,
                            selectedLabelColor = DfColors.Navy,
                            disabledContainerColor = DfColors.Navy.copy(alpha = 0.4f),
                            disabledLabelColor = DfColors.Surface.copy(alpha = 0.4f),
                        ),
                    )
                }
            }
            Wheel(
                labels = labels,
                hubTitle = ringName(ring),
                hubSubtitle = if (pages.size > 1) stringResource(R.string.wheel_page, page + 1, pages.size) else stringResource(R.string.hub_switch),
                onSelect = { i ->
                    if (pages.size > 1 && i == labels.lastIndex) page = (page + 1) % pages.size
                    else onChoose(all[range.first + i])
                },
                onHub = {
                    ring = available[(available.indexOf(ring) + 1) % available.size]
                    page = 0
                },
                onOutside = onDismiss,
                modifier = Modifier
                    .widthIn(max = 400.dp)
                    .fillMaxWidth()
                    .aspectRatio(1f),
            )
            Text(stringResource(R.string.wheel_hint), color = DfColors.Surface.copy(alpha = 0.9f), style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
fun MessageScreen(title: String, body: String?, busy: Boolean, onClose: () -> Unit) {
    Scrim(onClose) {
        SheetCard {
            if (busy) CircularProgressIndicator(Modifier.size(32.dp), color = DfColors.Primary)
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            body?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = DfColors.Muted) }
            if (!busy) ButtonsRow { TextButton(onClick = onClose) { Text(stringResource(R.string.close)) } }
        }
    }
}

// ------------------------------------------------------------------------------------------------------------ options

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun OptionsScreen(
    title: String,
    tool: ToolDef,
    prefill: Map<String, String>,
    onBack: () -> Unit,
    onStart: (Map<String, String>) -> Unit,
) {
    val context = LocalContext.current
    val values = remember(tool.key) {
        mutableStateMapOf<String, String>().apply {
            for (o in tool.options) put(o.key, prefill[o.key] ?: o.default)
        }
    }
    Scrim(onBack) {
        SheetCard {
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            for (spec in tool.options) {
                val gate = spec.showIf
                if (gate != null && values[gate.first] != gate.second) continue
                Text(Labels.option(context, spec), style = MaterialTheme.typography.labelLarge, color = DfColors.Muted)
                when (spec) {
                    is OptionSpec.Choice -> FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (v in spec.values) {
                            FilterChip(
                                selected = values[spec.key] == v,
                                onClick = { values[spec.key] = v },
                                label = { Text(Labels.choice(context, spec, v)) },
                                colors = FilterChipDefaults.filterChipColors(
                                    selectedContainerColor = DfColors.PrimarySoft,
                                    selectedLabelColor = DfColors.Primary,
                                ),
                            )
                        }
                    }
                    is OptionSpec.Number -> OutlinedTextField(
                        value = values[spec.key].orEmpty(),
                        onValueChange = { values[spec.key] = it },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    is OptionSpec.Text -> OutlinedTextField(
                        value = values[spec.key].orEmpty(),
                        onValueChange = { values[spec.key] = it },
                        minLines = if (spec.key == "qr_text") 3 else 1,
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
            ButtonsRow {
                TextButton(onClick = onBack) { Text(stringResource(R.string.back)) }
                Button(onClick = { onStart(values.toMap()) }) { Text(stringResource(R.string.start)) }
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------------------ editor

/** Range (trim) or single position (frame) over an ExoPlayer preview. Positions are milliseconds. */
@androidx.annotation.OptIn(markerClass = [UnstableApi::class])
@Composable
fun MediaEditorScreen(
    file: File,
    durationMs: Long,
    mode: Editor,
    onBack: () -> Unit,
    onStart: (Map<String, String>) -> Unit,
) {
    val context = LocalContext.current
    val player = remember(file) {
        ExoPlayer.Builder(context).build().apply {
            setMediaItem(MediaItem.fromUri(Uri.fromFile(file)))
            setSeekParameters(SeekParameters.EXACT)
            prepare()
        }
    }
    DisposableEffect(player) { onDispose { player.release() } }
    val total = durationMs.coerceAtLeast(1L).toFloat()
    var range by remember { mutableStateOf(0f..total) }
    var position by remember { mutableFloatStateOf(0f) }

    Scrim(onBack) {
        SheetCard {
            Text(
                stringResource(if (mode == Editor.RANGE) R.string.editor_trim_title else R.string.editor_frame_title),
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )
            AndroidView(
                factory = { PlayerView(it).apply { this.player = player } },
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(16f / 9f)
                    .background(DfColors.Navy, RoundedCornerShape(12.dp)),
            )
            if (mode == Editor.RANGE) {
                RangeSlider(
                    value = range,
                    onValueChange = { new ->
                        val seekTo = if (new.start != range.start) new.start else new.endInclusive
                        range = new
                        player.pause()
                        player.seekTo(seekTo.toLong())
                    },
                    valueRange = 0f..total,
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(stringResource(R.string.editor_start, MediaOps.formatTime(range.start.toLong())), style = MaterialTheme.typography.bodySmall)
                    Text(
                        stringResource(R.string.editor_length, MediaOps.formatTime((range.endInclusive - range.start).toLong())),
                        style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.SemiBold,
                    )
                    Text(stringResource(R.string.editor_end, MediaOps.formatTime(range.endInclusive.toLong())), style = MaterialTheme.typography.bodySmall)
                }
            } else {
                Slider(
                    value = position,
                    onValueChange = {
                        position = it
                        player.pause()
                        player.seekTo(it.toLong())
                    },
                    valueRange = 0f..total,
                )
                Text(stringResource(R.string.editor_at, MediaOps.formatTime(position.toLong())), style = MaterialTheme.typography.bodySmall)
            }
            ButtonsRow {
                TextButton(onClick = onBack) { Text(stringResource(R.string.back)) }
                Button(
                    enabled = mode != Editor.RANGE || range.endInclusive - range.start >= 100f,
                    onClick = {
                        player.pause()
                        onStart(
                            if (mode == Editor.RANGE) mapOf("start_ms" to range.start.toLong().toString(), "end_ms" to range.endInclusive.toLong().toString())
                            else mapOf("at_ms" to position.toLong().toString()),
                        )
                    },
                ) { Text(stringResource(R.string.start)) }
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------------------ job

@Composable
fun JobScreen(
    title: String,
    state: JobState?,
    onCancel: () -> Unit,
    onClose: () -> Unit,
    onOpen: (OutputItem) -> Unit,
    onShare: (List<OutputItem>) -> Unit,
    onCopyText: (String) -> Unit,
) {
    val context = LocalContext.current
    val finished = state is JobState.Done || state is JobState.Failed || state is JobState.Cancelled
    Scrim(onClose) {
        SheetCard {
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            when (state) {
                null -> Text(stringResource(R.string.result_gone), color = DfColors.Muted)
                JobState.Queued, is JobState.Running -> {
                    val p = (state as? JobState.Running)?.progress
                    if (p == null || state == JobState.Queued) {
                        LinearProgressIndicator(Modifier.fillMaxWidth(), color = DfColors.Primary)
                    } else {
                        LinearProgressIndicator(progress = { p }, modifier = Modifier.fillMaxWidth(), color = DfColors.Primary)
                    }
                    Text(stringResource(R.string.working), color = DfColors.Muted, style = MaterialTheme.typography.bodyMedium)
                    ButtonsRow {
                        TextButton(onClick = onCancel) { Text(stringResource(R.string.cancel)) }
                        OutlinedButton(onClick = onClose) { Text(stringResource(R.string.keep_in_background)) }
                    }
                }
                is JobState.Done -> {
                    Text(stringResource(R.string.result_done), color = DfColors.Ok, fontWeight = FontWeight.SemiBold)
                    state.text?.let { text ->
                        Text(stringResource(R.string.result_text), style = MaterialTheme.typography.labelLarge, color = DfColors.Muted)
                        SelectionContainer {
                            Text(
                                text,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .background(DfColors.Surface2, RoundedCornerShape(12.dp))
                                    .padding(12.dp),
                            )
                        }
                    }
                    if (state.outputs.isNotEmpty()) {
                        Text(
                            pluralStringResource(R.plurals.result_saved, state.outputs.size, state.outputs.size, state.outputs.first().location),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        for (o in state.outputs.take(4)) {
                            Text("• ${o.name}", style = MaterialTheme.typography.bodySmall, color = DfColors.Muted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        if (state.outputs.size > 4) Text("…", color = DfColors.Muted)
                    } else if (state.text == null) {
                        Text(stringResource(R.string.result_nothing), color = DfColors.Muted)
                    }
                    ButtonsRow {
                        TextButton(onClick = onClose) { Text(stringResource(R.string.close)) }
                        state.text?.let { t -> OutlinedButton(onClick = { onCopyText(t) }) { Text(stringResource(R.string.copy_text)) } }
                        if (state.outputs.isNotEmpty()) {
                            OutlinedButton(onClick = { onShare(state.outputs) }) { Text(stringResource(R.string.share)) }
                            Button(onClick = { onOpen(state.outputs.first()) }) { Text(stringResource(R.string.open)) }
                        }
                    }
                }
                is JobState.Failed -> {
                    Text(stringResource(R.string.result_failed), color = DfColors.Error, fontWeight = FontWeight.SemiBold)
                    Text(Labels.error(context, state.messageKey), style = MaterialTheme.typography.bodyMedium)
                    ButtonsRow { Button(onClick = onClose) { Text(stringResource(R.string.close)) } }
                }
                JobState.Cancelled -> {
                    Text(stringResource(R.string.result_cancelled), color = DfColors.Muted)
                    ButtonsRow { Button(onClick = onClose) { Text(stringResource(R.string.close)) } }
                }
            }
            if (!finished) Spacer(Modifier.size(0.dp))
        }
    }
}
