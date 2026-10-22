// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import android.graphics.Bitmap
import android.media.AudioFormat
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import android.media.MediaMuxer
import android.net.Uri
import android.os.Handler
import android.os.Looper
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.util.UnstableApi
import androidx.media3.effect.Presentation
import androidx.media3.transformer.Composition
import androidx.media3.transformer.DefaultEncoderFactory
import androidx.media3.transformer.EditedMediaItem
import androidx.media3.transformer.Effects
import androidx.media3.transformer.ExportException
import androidx.media3.transformer.ExportResult
import androidx.media3.transformer.ProgressHolder
import androidx.media3.transformer.Transformer
import androidx.media3.transformer.VideoEncoderSettings
import dev.scouthub.dropflow.core.MediaPlans
import dev.scouthub.dropflow.core.MediaPlans.VideoCodec
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.core.WavHeader
import dev.scouthub.dropflow.work.InputFile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * Video and audio work on the device's own hardware codecs: Media3 Transformer for re-encoding, trimming and
 * scaling; MediaExtractor + MediaMuxer for lossless stream copies; MediaCodec for decoding to WAV.
 */
@OptIn(UnstableApi::class)
object MediaOps {
    data class Tracks(val video: List<String>, val audio: List<String>, val durationUs: Long)

    fun probe(file: File): Tracks {
        val ex = MediaExtractor()
        try {
            ex.setDataSource(file.absolutePath)
            val video = mutableListOf<String>()
            val audio = mutableListOf<String>()
            var duration = 0L
            for (i in 0 until ex.trackCount) {
                val f = ex.getTrackFormat(i)
                val mime = f.getString(MediaFormat.KEY_MIME) ?: continue
                if (mime.startsWith("video/")) video += mime
                if (mime.startsWith("audio/")) audio += mime
                if (f.containsKey(MediaFormat.KEY_DURATION)) duration = maxOf(duration, f.getLong(MediaFormat.KEY_DURATION))
            }
            return Tracks(video, audio, duration)
        } catch (e: Exception) {
            throw OpFailure("error_media_read", e.message)
        } finally {
            ex.release()
        }
    }

    private fun item(file: File, startMs: Long? = null, endMs: Long? = null): MediaItem {
        val b = MediaItem.Builder().setUri(Uri.fromFile(file))
        if (startMs != null || endMs != null) {
            val clip = MediaItem.ClippingConfiguration.Builder()
            startMs?.let { clip.setStartPositionMs(it.coerceAtLeast(0)) }
            endMs?.let { clip.setEndPositionMs(it) }
            b.setClippingConfiguration(clip.build())
        }
        return b.build()
    }

    /** Runs one Media3 export and suspends until it finishes; cancelling the coroutine cancels the export. */
    private suspend fun export(
        ctx: OpContext,
        edited: EditedMediaItem,
        out: File,
        videoMime: String?,
        audioMime: String?,
        videoBitrate: Int? = null,
        progress: (Float?) -> Unit,
    ) = withContext(Dispatchers.Main) {
        val builder = Transformer.Builder(ctx.app)
        videoMime?.let { builder.setVideoMimeType(it) }
        audioMime?.let { builder.setAudioMimeType(it) }
        if (videoBitrate != null) {
            builder.setEncoderFactory(
                DefaultEncoderFactory.Builder(ctx.app)
                    .setRequestedVideoEncoderSettings(VideoEncoderSettings.Builder().setBitrate(videoBitrate).build())
                    .setEnableFallback(true)
                    .build(),
            )
        }
        coroutineScope {
            var transformer: Transformer? = null
            val poller = launch {
                val holder = ProgressHolder()
                while (isActive) {
                    val t = transformer
                    if (t != null && t.getProgress(holder) == Transformer.PROGRESS_STATE_AVAILABLE) progress(holder.progress / 100f)
                    delay(300)
                }
            }
            try {
                suspendCancellableCoroutine { cont ->
                    builder.addListener(object : Transformer.Listener {
                        override fun onCompleted(composition: Composition, exportResult: ExportResult) {
                            if (cont.isActive) cont.resume(Unit)
                        }

                        override fun onError(composition: Composition, exportResult: ExportResult, exportException: ExportException) {
                            out.delete()
                            if (cont.isActive) cont.resumeWithException(OpFailure("error_media_encode", exportException.message))
                        }
                    })
                    val t = builder.build()
                    transformer = t
                    t.start(edited, out.absolutePath)
                    cont.invokeOnCancellation {
                        Handler(Looper.getMainLooper()).post {
                            t.cancel()
                            out.delete()
                        }
                    }
                }
            } finally {
                poller.cancel()
            }
        }
        progress(1f)
    }

    /** Copies streams into an MP4/M4A container without re-encoding (lossless). */
    private suspend fun remux(ctx: OpContext, input: File, out: File, keepVideo: Boolean, keepAudio: Boolean, progress: (Float?) -> Unit) =
        withContext(Dispatchers.IO) {
            val ex = MediaExtractor()
            var muxer: MediaMuxer? = null
            try {
                ex.setDataSource(input.absolutePath)
                val m = MediaMuxer(out.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
                muxer = m
                val map = HashMap<Int, Int>()
                var bufferSize = 1 shl 20
                var duration = 1L
                var rotation = 0
                var videoTaken = false
                for (i in 0 until ex.trackCount) {
                    val f = ex.getTrackFormat(i)
                    val mime = f.getString(MediaFormat.KEY_MIME) ?: continue
                    val isVideo = mime.startsWith("video/")
                    val take = (keepVideo && isVideo && !videoTaken) || (keepAudio && mime.startsWith("audio/"))
                    if (!take) continue
                    if (isVideo) videoTaken = true
                    if (mime.startsWith("video/") && f.containsKey(MediaFormat.KEY_ROTATION)) rotation = f.getInteger(MediaFormat.KEY_ROTATION)
                    if (f.containsKey(MediaFormat.KEY_MAX_INPUT_SIZE)) bufferSize = maxOf(bufferSize, f.getInteger(MediaFormat.KEY_MAX_INPUT_SIZE))
                    if (f.containsKey(MediaFormat.KEY_DURATION)) duration = maxOf(duration, f.getLong(MediaFormat.KEY_DURATION))
                    map[i] = m.addTrack(f)
                    ex.selectTrack(i)
                }
                if (map.isEmpty()) throw OpFailure("error_no_audio")
                if (rotation == 0 && keepVideo) rotation = retrieverRotation(input)
                if (rotation != 0) m.setOrientationHint(rotation)
                m.start()
                val buffer = ByteBuffer.allocate(maxOf(bufferSize, 8 shl 20))
                val info = MediaCodec.BufferInfo()
                var n = 0
                while (true) {
                    val size = ex.readSampleData(buffer, 0)
                    if (size < 0) break
                    val track = map[ex.sampleTrackIndex]
                    if (track == null) {
                        ex.advance()
                        continue
                    }
                    val flags = if (ex.sampleFlags and MediaExtractor.SAMPLE_FLAG_SYNC != 0) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                    info.set(0, size, ex.sampleTime, flags)
                    m.writeSampleData(track, buffer, info)
                    ex.advance()
                    if (++n % 200 == 0) {
                        ctx.checkCancelled()
                        progress((ex.sampleTime.toFloat() / duration).coerceIn(0f, 1f))
                    }
                }
                m.stop()
                progress(1f)
            } catch (e: Exception) {
                out.delete()
                throw e
            } finally {
                runCatching { muxer?.release() }
                ex.release()
            }
        }

    /** A lossless copy when possible; false (never an exception, except cancellation) means "re-encode instead". */
    private suspend fun tryRemux(ctx: OpContext, input: File, out: File, keepVideo: Boolean, keepAudio: Boolean, progress: (Float?) -> Unit): Boolean =
        try {
            remux(ctx, input, out, keepVideo, keepAudio, progress)
            true
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            android.util.Log.i("DropFlow", "Stream copy not possible, re-encoding: ${e.message}")
            false
        }

    private fun retrieverRotation(file: File): Int = runCatching {
        MediaMetadataRetriever().run {
            try {
                setDataSource(file.absolutePath)
                extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0
            } finally {
                release()
            }
        }
    }.getOrDefault(0)

    private fun videoSize(file: File): Pair<Int, Int>? = runCatching {
        MediaMetadataRetriever().run {
            try {
                setDataSource(file.absolutePath)
                val w = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull() ?: return@run null
                val h = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull() ?: return@run null
                val rot = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0
                if (rot % 180 != 0) h to w else w to h
            } finally {
                release()
            }
        }
    }.getOrNull()

    // ------------------------------------------------------------------ operations

    suspend fun convertVideo(ctx: OpContext, inputs: List<InputFile>, codec: VideoCodec): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            val tracks = probe(input.file)
            if (tracks.video.isEmpty()) throw OpFailure("error_no_video")
            val out = ctx.newFile(OutputNaming.forFile(input.name, "mp4"))
            val done = MediaPlans.canRemuxToMp4(codec, tracks.video, tracks.audio) &&
                tryRemux(ctx, input.file, out, keepVideo = true, keepAudio = true, progress)
            if (!done) {
                export(ctx, EditedMediaItem.Builder(item(input.file)).build(), out, codec.mime, MimeTypes.AUDIO_AAC, progress = progress)
            }
            Produced(out, out.name, "video/mp4")
        }
        return OpResult(files)
    }

    suspend fun mute(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            val tracks = probe(input.file)
            val out = ctx.newFile(OutputNaming.forFile(input.name, "mp4", "muted"))
            val copyable = tracks.video.size == 1 && (tracks.video[0] == MediaPlans.AVC || tracks.video[0] == MediaPlans.HEVC)
            val done = copyable && tryRemux(ctx, input.file, out, keepVideo = true, keepAudio = false, progress)
            if (!done) {
                val edited = EditedMediaItem.Builder(item(input.file)).setRemoveAudio(true).build()
                export(ctx, edited, out, MimeTypes.VIDEO_H264, null, progress = progress)
            }
            Produced(out, out.name, "video/mp4")
        }
        return OpResult(files)
    }

    suspend fun compressVideo(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val preset = MediaPlans.VideoPreset.of(ctx.option("preset", "balanced"))
        val files = ctx.eachInput(inputs) { input, progress ->
            val size = videoSize(input.file)
            val effects = mutableListOf<androidx.media3.common.Effect>()
            val max = preset.maxShortSide
            if (size != null && max != null && minOf(size.first, size.second) > max) effects += Presentation.createForShortSide(max)
            val edited = EditedMediaItem.Builder(item(input.file)).setEffects(Effects(emptyList(), effects)).build()
            val out = ctx.newFile(OutputNaming.forFile(input.name, "mp4", "compressed"))
            export(ctx, edited, out, MimeTypes.VIDEO_H264, MimeTypes.AUDIO_AAC, videoBitrate = preset.videoBitrate, progress = progress)
            Produced(out, out.name, "video/mp4")
        }
        return OpResult(files)
    }

    suspend fun trimVideo(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val input = inputs.single()
        val (start, end) = range(ctx)
        val out = ctx.newFile(OutputNaming.forFile(input.name, "mp4", "trimmed"))
        export(ctx, EditedMediaItem.Builder(item(input.file, start, end)).build(), out, MimeTypes.VIDEO_H264, MimeTypes.AUDIO_AAC, progress = ctx::progress)
        return OpResult(listOf(Produced(out, out.name, "video/mp4")))
    }

    suspend fun trimAudio(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val input = inputs.single()
        val (start, end) = range(ctx)
        val out = ctx.newFile(OutputNaming.forFile(input.name, "m4a", "trimmed"))
        val edited = EditedMediaItem.Builder(item(input.file, start, end)).setRemoveVideo(true).build()
        export(ctx, edited, out, null, MimeTypes.AUDIO_AAC, progress = ctx::progress)
        return OpResult(listOf(Produced(out, out.name, "audio/mp4")))
    }

    private fun range(ctx: OpContext): Pair<Long, Long> {
        val start = ctx.options["start_ms"]?.toLongOrNull() ?: 0L
        val end = ctx.options["end_ms"]?.toLongOrNull() ?: throw OpFailure("error_range")
        if (end <= start) throw OpFailure("error_range")
        return start to end
    }

    suspend fun toM4a(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            val tracks = probe(input.file)
            if (tracks.audio.isEmpty()) throw OpFailure("error_no_audio")
            val out = ctx.newFile(OutputNaming.forFile(input.name, "m4a"))
            val copied = MediaPlans.canCopyAudioToM4a(tracks.audio) &&
                tryRemux(ctx, input.file, out, keepVideo = false, keepAudio = true, progress)
            if (!copied) {
                val edited = EditedMediaItem.Builder(item(input.file)).setRemoveVideo(true).build()
                export(ctx, edited, out, null, MimeTypes.AUDIO_AAC, progress = progress)
            }
            Produced(out, out.name, "audio/mp4")
        }
        return OpResult(files)
    }

    suspend fun toWav(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            val out = ctx.newFile(OutputNaming.forFile(input.name, "wav"))
            decodeToWav(ctx, input.file, out, progress)
            Produced(out, out.name, "audio/x-wav")
        }
        return OpResult(files)
    }

    private suspend fun decodeToWav(ctx: OpContext, input: File, out: File, progress: (Float?) -> Unit) = withContext(Dispatchers.IO) {
        val ex = MediaExtractor()
        var codec: MediaCodec? = null
        try {
            ex.setDataSource(input.absolutePath)
            val index = (0 until ex.trackCount).firstOrNull { ex.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true }
                ?: throw OpFailure("error_no_audio")
            ex.selectTrack(index)
            val format = ex.getTrackFormat(index)
            val duration = if (format.containsKey(MediaFormat.KEY_DURATION)) format.getLong(MediaFormat.KEY_DURATION) else 0L
            var sampleRate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            var isFloat = false
            val c = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            codec = c
            c.configure(format, null, null, 0)
            c.start()
            RandomAccessFile(out, "rw").use { raf ->
                raf.setLength(0)
                raf.write(ByteArray(WavHeader.SIZE))
                val info = MediaCodec.BufferInfo()
                var inputDone = false
                var outputDone = false
                var dataBytes = 0L
                var scratch = ByteArray(64 * 1024)
                var loops = 0
                while (!outputDone) {
                    if (++loops % 64 == 0) ctx.checkCancelled()
                    if (!inputDone) {
                        val i = c.dequeueInputBuffer(10_000)
                        if (i >= 0) {
                            val buf = c.getInputBuffer(i)!!
                            val n = ex.readSampleData(buf, 0)
                            if (n < 0) {
                                c.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                inputDone = true
                            } else {
                                c.queueInputBuffer(i, 0, n, ex.sampleTime, 0)
                                ex.advance()
                            }
                        }
                    }
                    val o = c.dequeueOutputBuffer(info, 10_000)
                    when {
                        o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                            val f = c.outputFormat
                            if (f.containsKey(MediaFormat.KEY_SAMPLE_RATE)) sampleRate = f.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                            if (f.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) channels = f.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                            isFloat = f.containsKey(MediaFormat.KEY_PCM_ENCODING) && f.getInteger(MediaFormat.KEY_PCM_ENCODING) == AudioFormat.ENCODING_PCM_FLOAT
                        }
                        o >= 0 -> {
                            val buf = c.getOutputBuffer(o)!!
                            buf.position(info.offset)
                            buf.limit(info.offset + info.size)
                            if (info.size > 0) {
                                val needed = if (isFloat) info.size / 2 else info.size
                                if (scratch.size < needed) scratch = ByteArray(needed)
                                val written = if (isFloat) WavHeader.floatTo16(buf.slice(), scratch) else info.size.also { buf.get(scratch, 0, it) }
                                raf.write(scratch, 0, written)
                                dataBytes += written
                            }
                            c.releaseOutputBuffer(o, false)
                            if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outputDone = true
                            if (duration > 0) progress((info.presentationTimeUs.toFloat() / duration).coerceIn(0f, 1f))
                        }
                    }
                }
                raf.seek(0)
                raf.write(WavHeader.build(dataBytes, sampleRate, channels))
            }
            progress(1f)
        } catch (e: OpFailure) {
            out.delete()
            throw e
        } catch (e: kotlinx.coroutines.CancellationException) {
            out.delete()
            throw e
        } catch (e: Exception) {
            out.delete()
            throw OpFailure("error_media_encode", e.message)
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            ex.release()
        }
    }

    suspend fun saveFrame(ctx: OpContext, inputs: List<InputFile>): OpResult = withContext(Dispatchers.IO) {
        val input = inputs.single()
        val atMs = ctx.options["at_ms"]?.toLongOrNull() ?: 0L
        val bitmap: Bitmap = MediaMetadataRetriever().run {
            try {
                setDataSource(input.file.absolutePath)
                getFrameAtTime(atMs * 1000, MediaMetadataRetriever.OPTION_CLOSEST) ?: throw OpFailure("error_frame")
            } catch (e: OpFailure) {
                throw e
            } catch (e: Exception) {
                throw OpFailure("error_frame", e.message)
            } finally {
                release()
            }
        }
        val stem = OutputNaming.stem(input.name)
        val out = ctx.newFile("$stem (frame ${formatTime(atMs).replace(':', '.')}).png")
        ImageOps.encode(bitmap, "png", 100, out)
        ctx.progress(1f)
        OpResult(listOf(Produced(out, out.name, "image/png")))
    }

    fun formatTime(ms: Long): String {
        val total = ms / 1000
        val h = total / 3600
        val m = (total % 3600) / 60
        val s = total % 60
        val tenths = (ms % 1000) / 100
        return if (h > 0) String.format(java.util.Locale.ROOT, "%d:%02d:%02d.%d", h, m, s, tenths)
        else String.format(java.util.Locale.ROOT, "%d:%02d.%d", m, s, tenths)
    }
}
