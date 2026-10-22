// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.work

import android.content.Context
import androidx.core.content.ContextCompat
import dev.scouthub.dropflow.catalog.Op
import dev.scouthub.dropflow.catalog.ToolDef
import dev.scouthub.dropflow.core.MediaPlans
import dev.scouthub.dropflow.ops.ArchiveOps
import dev.scouthub.dropflow.ops.ImageOps
import dev.scouthub.dropflow.ops.MediaOps
import dev.scouthub.dropflow.ops.OpContext
import dev.scouthub.dropflow.ops.OpResult
import dev.scouthub.dropflow.ops.PdfOps
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import java.io.File
import java.util.concurrent.atomic.AtomicLong

data class JobRequest(val id: Long, val tool: ToolDef, val inputs: List<InputFile>, val options: Map<String, String>, val title: String)

sealed interface JobState {
    data object Queued : JobState
    data class Running(val progress: Float?) : JobState
    data class Done(val outputs: List<OutputItem>, val text: String?) : JobState
    /** [messageKey] is a string resource name; [detail] is technical text for the log. */
    data class Failed(val messageKey: String, val detail: String?) : JobState
    data object Cancelled : JobState
}

/** Process-wide job registry: the wheel submits, the foreground service runs, both observe [states]. */
object JobCenter {
    private val nextId = AtomicLong(System.currentTimeMillis())
    private val requests = java.util.concurrent.ConcurrentHashMap<Long, JobRequest>()
    private val _states = MutableStateFlow<Map<Long, JobState>>(emptyMap())
    val states: StateFlow<Map<Long, JobState>> = _states.asStateFlow()

    /** Jobs whose result a visible wheel is showing: no "done" notification for those. */
    val watching: MutableSet<Long> = java.util.concurrent.ConcurrentHashMap.newKeySet()

    fun submit(context: Context, tool: ToolDef, inputs: List<InputFile>, options: Map<String, String>, title: String): Long {
        val id = nextId.incrementAndGet()
        requests[id] = JobRequest(id, tool, inputs, options, title)
        set(id, JobState.Queued)
        ContextCompat.startForegroundService(context, WorkService.startIntent(context, id))
        return id
    }

    fun request(id: Long): JobRequest? = requests[id]

    fun set(id: Long, state: JobState) = _states.update { it + (id to state) }

    fun state(id: Long): JobState? = _states.value[id]

    fun cancel(context: Context, id: Long) {
        context.startService(WorkService.cancelIntent(context, id))
    }
}

/** Maps a wheel entry to the operation that does the work. */
object Runner {
    suspend fun run(context: Context, request: JobRequest, workDir: File, onProgress: (Float?) -> Unit): OpResult {
        val ctx = OpContext(context.applicationContext, workDir, request.options, onProgress)
        val inputs = request.inputs
        val tool = request.tool
        return when (tool.op) {
            Op.IMG_CONVERT -> ImageOps.convert(ctx, inputs, tool.target!!)
            Op.IMG_COMPRESS -> ImageOps.compress(ctx, inputs)
            Op.IMG_RESIZE -> ImageOps.resize(ctx, inputs)
            Op.IMG_ROTATE -> ImageOps.rotate(ctx, inputs)
            Op.IMG_STRIP -> ImageOps.stripMetadata(ctx, inputs)
            Op.IMG_TO_PDF -> ImageOps.toPdf(ctx, inputs, combine = tool.combine)
            Op.QR_READ -> ImageOps.readQr(ctx, inputs)
            Op.QR_CREATE -> ImageOps.createQr(ctx, inputs)
            Op.PDF_TO_IMAGES -> PdfOps.toImages(ctx, inputs, tool.target ?: "png")
            Op.PDF_MERGE -> PdfOps.merge(ctx, inputs)
            Op.PDF_SPLIT -> PdfOps.split(ctx, inputs)
            Op.PDF_ROTATE -> PdfOps.rotate(ctx, inputs)
            Op.PDF_STRIP -> PdfOps.stripMetadata(ctx, inputs)
            Op.PDF_COMPRESS -> PdfOps.compress(ctx, inputs)
            Op.PDF_TEXT -> PdfOps.extractText(ctx, inputs)
            Op.VID_CONVERT -> MediaOps.convertVideo(
                ctx, inputs, if (tool.key.endsWith("hevc")) MediaPlans.VideoCodec.HEVC else MediaPlans.VideoCodec.H264,
            )
            Op.VID_TRIM -> MediaOps.trimVideo(ctx, inputs)
            Op.VID_MUTE -> MediaOps.mute(ctx, inputs)
            Op.VID_COMPRESS -> MediaOps.compressVideo(ctx, inputs)
            Op.VID_FRAME -> MediaOps.saveFrame(ctx, inputs)
            Op.MEDIA_TO_M4A -> MediaOps.toM4a(ctx, inputs)
            Op.MEDIA_TO_WAV -> MediaOps.toWav(ctx, inputs)
            Op.AUD_TRIM -> MediaOps.trimAudio(ctx, inputs)
            Op.ZIP -> ArchiveOps.zip(ctx, inputs)
            Op.UNZIP -> ArchiveOps.unzip(ctx, inputs)
            Op.SHARE, Op.OPEN_WITH, Op.COPY_TO_FOLDER -> error("Actions run in the activity, not as jobs")
        }
    }
}
