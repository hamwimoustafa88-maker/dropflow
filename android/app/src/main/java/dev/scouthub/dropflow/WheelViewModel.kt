// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.app.Application
import android.content.Intent
import android.media.MediaMetadataRetriever
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.scouthub.dropflow.catalog.AndroidTools
import dev.scouthub.dropflow.catalog.Ring
import dev.scouthub.dropflow.catalog.ToolDef
import dev.scouthub.dropflow.ops.ImageOps
import dev.scouthub.dropflow.work.InputFile
import dev.scouthub.dropflow.work.Inputs
import dev.scouthub.dropflow.work.JobCenter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** State of one wheel session: the copied inputs, the rings, and which screen is showing. Survives rotation. */
class WheelViewModel(app: Application) : AndroidViewModel(app) {
    sealed interface Screen {
        data object Loading : Screen
        data object Empty : Screen
        data class Error(val messageKey: String) : Screen
        data object Wheel : Screen
        data class Options(val tool: ToolDef, val title: String) : Screen
        data class MediaEditor(val tool: ToolDef, val title: String, val durationMs: Long) : Screen
        data class Job(val id: Long, val title: String) : Screen
    }

    var screen by mutableStateOf<Screen>(Screen.Loading)
        private set

    var inputs: List<InputFile> = emptyList()
        private set
    var rings: Map<Ring, List<ToolDef>> = emptyMap()
        private set
    var sharedText: String? = null
        private set

    private var started = false

    fun start(intent: Intent) {
        if (started) return
        started = true
        val jobId = intent.getLongExtra(WheelActivity.EXTRA_JOB_ID, -1L)
        if (jobId >= 0) {
            showJob(jobId)
            return
        }
        val app = getApplication<Application>()
        val uris = Inputs.urisOf(app, intent)
        sharedText = Inputs.textOf(intent)
        viewModelScope.launch {
            try {
                inputs = Inputs.copy(app, uris, sharedText)
                rings = AndroidTools.wheelFor(inputs.map { it.kind }, AndroidTools.Capabilities(heicEncoder = ImageOps.heicEncoderAvailable))
                screen = if (inputs.isEmpty() || rings.values.all { it.isEmpty() }) Screen.Empty else Screen.Wheel
            } catch (e: Exception) {
                Log.w("DropFlow", "Could not copy the shared files", e)
                screen = Screen.Error("error_input")
            }
        }
    }

    fun showJob(id: Long) {
        val title = JobCenter.request(id)?.title.orEmpty()
        screen = Screen.Job(id, title)
    }

    fun back() {
        screen = if (inputs.isEmpty()) Screen.Empty else Screen.Wheel
    }

    /** A wheel entry was chosen (actions are handled by the activity before this). */
    fun choose(tool: ToolDef, title: String) {
        when {
            tool.editor != null -> viewModelScope.launch {
                val duration = withContext(Dispatchers.IO) { durationOf(inputs.single()) }
                screen = Screen.MediaEditor(tool, title, duration)
            }
            tool.options.isNotEmpty() -> screen = Screen.Options(tool, title)
            else -> submit(tool, title, emptyMap())
        }
    }

    fun submit(tool: ToolDef, title: String, options: Map<String, String>) {
        val id = JobCenter.submit(getApplication(), tool, inputs, options, title)
        JobCenter.watching += id
        screen = Screen.Job(id, title)
    }

    fun prefillFor(tool: ToolDef): Map<String, String> =
        if (tool.key == "qr.create") mapOf("qr_text" to (sharedText ?: inputs.firstOrNull()?.file?.readText().orEmpty())) else emptyMap()

    private fun durationOf(input: InputFile): Long = runCatching {
        MediaMetadataRetriever().run {
            try {
                setDataSource(input.file.absolutePath)
                extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L
            } finally {
                release()
            }
        }
    }.getOrDefault(0L)
}
