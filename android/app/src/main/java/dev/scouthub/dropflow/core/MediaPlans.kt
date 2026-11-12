// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/** Codec decisions for video/audio work, kept free of Android classes so they can be unit tested. */
object MediaPlans {
    const val AVC = "video/avc"
    const val HEVC = "video/hevc"
    const val AAC = "audio/mp4a-latm"

    enum class VideoCodec(val mime: String) { H264(AVC), HEVC(MediaPlans.HEVC) }

    /**
     * True when the streams can be copied into MP4 untouched (lossless and fast) instead of re-encoded:
     * exactly one video track already in the requested codec, and at most one audio track that is AAC.
     */
    fun canRemuxToMp4(target: VideoCodec, videoMimes: List<String>, audioMimes: List<String>): Boolean =
        videoMimes.size == 1 && videoMimes[0].equals(target.mime, ignoreCase = true) &&
            audioMimes.size <= 1 && audioMimes.all { it.equals(AAC, ignoreCase = true) }

    /** True when an AAC track can go straight into an .m4a file. */
    fun canCopyAudioToM4a(audioMimes: List<String>): Boolean = audioMimes.size == 1 && audioMimes[0].equals(AAC, ignoreCase = true)

    /** Compression presets for "Compress videos": sized for sharing. */
    enum class VideoPreset(val id: String, val maxShortSide: Int?, val videoBitrate: Int, val audioBitrate: Int) {
        SMALL("small", 720, 1_500_000, 96_000),
        BALANCED("balanced", 1080, 4_000_000, 128_000),
        HIGH("high", null, 8_000_000, 160_000);

        companion object {
            fun of(id: String?): VideoPreset = entries.firstOrNull { it.id == id } ?: BALANCED
        }
    }

    /**
     * Output size for a video of [width] x [height] (as displayed) whose short side should not exceed [maxShortSide].
     * Never upscales, keeps the aspect ratio, and rounds to even numbers (encoders need them).
     */
    fun scaledSize(width: Int, height: Int, maxShortSide: Int?): Pair<Int, Int> {
        require(width > 0 && height > 0)
        val short = minOf(width, height)
        if (maxShortSide == null || short <= maxShortSide) return even(width) to even(height)
        val scale = maxShortSide.toDouble() / short
        return even((width * scale).toInt()) to even((height * scale).toInt())
    }

    private fun even(v: Int): Int = maxOf(2, v - (v and 1))

    /** Lowest video bitrate DropFlow asks an encoder for (below it H.264 falls apart). */
    const val MIN_VIDEO_BITRATE = 250_000

    /**
     * Video bitrate for "Compress videos": the preset's rate, but never more than about 80 % of what the source spends
     * on the same number of pixels. Without this, a 1 Mbit/s phone clip was re-encoded at 4 Mbit/s and came out bigger.
     * [sourceBitrate] is the whole file's rate (bits/s); null or unknown pixel counts fall back to the preset.
     */
    fun videoBitrateFor(preset: VideoPreset, sourceBitrate: Long?, sourcePixels: Long?, outputPixels: Long?): Int {
        val requested = preset.videoBitrate
        if (sourceBitrate == null || sourceBitrate <= 0) return requested
        val sourceVideo = maxOf(sourceBitrate - preset.audioBitrate, sourceBitrate / 2)
        val pixelRatio = if (sourcePixels != null && outputPixels != null && sourcePixels > 0 && outputPixels in 1..sourcePixels) {
            outputPixels.toDouble() / sourcePixels
        } else {
            1.0
        }
        val cap = (sourceVideo * pixelRatio * 0.8).toLong()
        return minOf(requested.toLong(), cap).coerceAtLeast(MIN_VIDEO_BITRATE.toLong()).toInt()
    }

    /** Estimated output size in bytes for a duration at the preset bitrates (used to warn about tiny gains). */
    fun estimateBytes(durationMs: Long, preset: VideoPreset): Long =
        (durationMs / 1000.0 * (preset.videoBitrate + preset.audioBitrate) / 8).toLong()
}
