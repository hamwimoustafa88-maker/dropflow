// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.catalog

import dev.scouthub.dropflow.core.FileKind

/** The three rings of the wheel, as on Windows (Convert / Tools / Actions). */
enum class Ring { CONVERT, TOOLS, ACTIONS }

/** What actually runs. One op can serve several wheel entries (for example every image format target). */
enum class Op {
    IMG_CONVERT, IMG_COMPRESS, IMG_RESIZE, IMG_ROTATE, IMG_STRIP, IMG_TO_PDF, QR_READ, QR_CREATE,
    PDF_TO_IMAGES, PDF_MERGE, PDF_SPLIT, PDF_ROTATE, PDF_STRIP, PDF_COMPRESS, PDF_TEXT,
    VID_CONVERT, VID_TRIM, VID_MUTE, VID_COMPRESS, VID_FRAME, MEDIA_TO_M4A, MEDIA_TO_WAV, AUD_TRIM,
    ZIP, UNZIP,
    SHARE, OPEN_WITH, COPY_TO_FOLDER,
}

/** A screen that replaces the options form: a range (trim) or a single position (frame) over a media preview. */
enum class Editor { RANGE, POSITION }

/** One field of the options form. Labels live in strings.xml as `opt_<key>` and `opt_<key>_<value>`. */
sealed class OptionSpec(val key: String, val default: String, val showIf: Pair<String, String>?) {
    class Choice(key: String, val values: List<String>, default: String, showIf: Pair<String, String>? = null) :
        OptionSpec(key, default, showIf)

    class Number(key: String, default: String, showIf: Pair<String, String>? = null) : OptionSpec(key, default, showIf)

    class Text(key: String, default: String, showIf: Pair<String, String>? = null) : OptionSpec(key, default, showIf)
}

/**
 * One wheel entry on Android.
 *
 * [catalogId] ties the entry to catalog/tools.json (labels then come from the catalog, ar + en). Entries the catalog
 * does not have yet (the Android-only rotate and open-with, and format targets) use the string resource [labelKey].
 */
data class ToolDef(
    val key: String,
    val ring: Ring,
    val op: Op,
    val catalogId: String? = null,
    val labelKey: String? = null,
    /** Input kinds this applies to; empty = any file. */
    val kinds: Set<FileKind> = emptySet(),
    /** Target extension for Convert entries (must be one of the catalog category outputs). */
    val target: String? = null,
    /** All inputs become one result (merge, images to PDF, zip). Otherwise each input is processed on its own. */
    val combine: Boolean = false,
    val minFiles: Int = 1,
    val maxFiles: Int? = null,
    val options: List<OptionSpec> = emptyList(),
    val editor: Editor? = null,
    val needsHeicEncoder: Boolean = false,
)

object AndroidTools {
    private val image = setOf(FileKind.IMAGE)
    private val pdf = setOf(FileKind.PDF)
    private val video = setOf(FileKind.VIDEO)
    private val audio = setOf(FileKind.AUDIO)

    private val angle = OptionSpec.Choice("angle", listOf("90", "180", "270"), "90")
    private val pageSize = OptionSpec.Choice("page_size", listOf("a4", "image"), "a4")
    private val dpi = OptionSpec.Choice("dpi", listOf("72", "150", "300"), "150")

    val all: List<ToolDef> = listOf(
        // ---- Convert ----
        ToolDef("convert.image.jpg", Ring.CONVERT, Op.IMG_CONVERT, labelKey = "fmt_jpg", kinds = image, target = "jpg"),
        ToolDef("convert.image.png", Ring.CONVERT, Op.IMG_CONVERT, labelKey = "fmt_png", kinds = image, target = "png"),
        ToolDef("convert.image.webp", Ring.CONVERT, Op.IMG_CONVERT, labelKey = "fmt_webp", kinds = image, target = "webp"),
        ToolDef("convert.image.heic", Ring.CONVERT, Op.IMG_CONVERT, labelKey = "fmt_heic", kinds = image, target = "heic", needsHeicEncoder = true),
        ToolDef("convert.image.pdf", Ring.CONVERT, Op.IMG_TO_PDF, labelKey = "fmt_pdf", kinds = image, target = "pdf", options = listOf(pageSize)),
        ToolDef("convert.pdf.png", Ring.CONVERT, Op.PDF_TO_IMAGES, labelKey = "fmt_png", kinds = pdf, target = "png", options = listOf(dpi)),
        ToolDef("convert.pdf.jpg", Ring.CONVERT, Op.PDF_TO_IMAGES, labelKey = "fmt_jpg", kinds = pdf, target = "jpg", options = listOf(dpi)),
        ToolDef("convert.pdf.txt", Ring.CONVERT, Op.PDF_TEXT, labelKey = "fmt_txt", kinds = pdf, target = "txt"),
        ToolDef("convert.video.mp4", Ring.CONVERT, Op.VID_CONVERT, labelKey = "fmt_mp4_h264", kinds = video, target = "mp4"),
        ToolDef("convert.video.mp4-hevc", Ring.CONVERT, Op.VID_CONVERT, labelKey = "fmt_mp4_hevc", kinds = video, target = "mp4"),
        ToolDef("convert.video.m4a", Ring.CONVERT, Op.MEDIA_TO_M4A, labelKey = "fmt_m4a", kinds = video, target = "m4a"),
        ToolDef("convert.video.wav", Ring.CONVERT, Op.MEDIA_TO_WAV, labelKey = "fmt_wav", kinds = video, target = "wav"),
        ToolDef("convert.audio.m4a", Ring.CONVERT, Op.MEDIA_TO_M4A, labelKey = "fmt_m4a", kinds = audio, target = "m4a"),
        ToolDef("convert.audio.wav", Ring.CONVERT, Op.MEDIA_TO_WAV, labelKey = "fmt_wav", kinds = audio, target = "wav"),

        // ---- Tools: images ----
        ToolDef(
            "img.compress", Ring.TOOLS, Op.IMG_COMPRESS, catalogId = "img.compress", kinds = image,
            options = listOf(
                OptionSpec.Choice("compress_by", listOf("quality", "size"), "quality"),
                OptionSpec.Choice("quality", listOf("85", "75", "60", "45"), "75", showIf = "compress_by" to "quality"),
                OptionSpec.Number("target_kb", "500", showIf = "compress_by" to "size"),
                OptionSpec.Choice("format", listOf("auto", "jpg", "webp"), "auto"),
            ),
        ),
        ToolDef(
            "img.resize", Ring.TOOLS, Op.IMG_RESIZE, catalogId = "img.resize", kinds = image,
            options = listOf(
                OptionSpec.Choice("resize_by", listOf("long_edge", "percent"), "long_edge"),
                OptionSpec.Choice("long_edge", listOf("720", "1080", "1600", "2048", "3840"), "1600", showIf = "resize_by" to "long_edge"),
                OptionSpec.Choice("percent", listOf("25", "50", "75"), "50", showIf = "resize_by" to "percent"),
            ),
        ),
        ToolDef("img.rotate", Ring.TOOLS, Op.IMG_ROTATE, labelKey = "tool_img_rotate", kinds = image, options = listOf(angle)),
        ToolDef(
            "img.strip-gps", Ring.TOOLS, Op.IMG_STRIP, catalogId = "img.strip-gps", kinds = image,
            options = listOf(OptionSpec.Choice("strip", listOf("location", "all"), "location")),
        ),
        ToolDef("img.to-pdf", Ring.TOOLS, Op.IMG_TO_PDF, catalogId = "img.to-pdf", kinds = image, combine = true, options = listOf(pageSize)),
        ToolDef("img.qr", Ring.TOOLS, Op.QR_READ, catalogId = "img.qr", kinds = image),

        // ---- Tools: text shared from another app ----
        ToolDef(
            "qr.create", Ring.TOOLS, Op.QR_CREATE, catalogId = "img.qr", labelKey = "tool_qr_create", kinds = setOf(FileKind.TEXT),
            maxFiles = 1, options = listOf(OptionSpec.Text("qr_text", "")),
        ),

        // ---- Tools: PDF ----
        ToolDef(
            "pdf.compress", Ring.TOOLS, Op.PDF_COMPRESS, catalogId = "pdf.compress", kinds = pdf,
            options = listOf(OptionSpec.Choice("pdf_level", listOf("light", "balanced", "strong"), "balanced")),
        ),
        ToolDef("pdf.merge", Ring.TOOLS, Op.PDF_MERGE, catalogId = "pdf.merge", kinds = pdf, combine = true, minFiles = 2),
        ToolDef(
            "pdf.split", Ring.TOOLS, Op.PDF_SPLIT, catalogId = "pdf.split", kinds = pdf,
            options = listOf(
                OptionSpec.Choice("split_mode", listOf("each", "ranges"), "each"),
                OptionSpec.Text("ranges", "1-2", showIf = "split_mode" to "ranges"),
            ),
        ),
        ToolDef("pdf.rotate", Ring.TOOLS, Op.PDF_ROTATE, catalogId = "pdf.rotate", kinds = pdf, options = listOf(angle, OptionSpec.Text("pages", ""))),
        ToolDef("pdf.metadata", Ring.TOOLS, Op.PDF_STRIP, catalogId = "pdf.metadata", labelKey = "tool_pdf_strip", kinds = pdf),
        ToolDef("pdf.to-images", Ring.TOOLS, Op.PDF_TO_IMAGES, catalogId = "pdf.to-images", kinds = pdf, target = "png", options = listOf(dpi)),
        ToolDef("pdf.extract-text", Ring.TOOLS, Op.PDF_TEXT, catalogId = "pdf.extract-text", kinds = pdf, target = "txt"),

        // ---- Tools: video ----
        ToolDef(
            "vid.compress", Ring.TOOLS, Op.VID_COMPRESS, catalogId = "vid.compress", kinds = video,
            options = listOf(OptionSpec.Choice("preset", listOf("small", "balanced", "high"), "balanced")),
        ),
        ToolDef("vid.trim", Ring.TOOLS, Op.VID_TRIM, catalogId = "vid.trim", kinds = video, maxFiles = 1, editor = Editor.RANGE),
        ToolDef("vid.mute", Ring.TOOLS, Op.VID_MUTE, catalogId = "vid.mute", kinds = video),
        ToolDef("vid.snapshot", Ring.TOOLS, Op.VID_FRAME, catalogId = "vid.snapshot", kinds = video, maxFiles = 1, editor = Editor.POSITION),

        // ---- Tools: audio ----
        ToolDef("aud.trim", Ring.TOOLS, Op.AUD_TRIM, catalogId = "aud.trim", kinds = audio, maxFiles = 1, editor = Editor.RANGE),

        // ---- Tools: archives and any file ----
        ToolDef("arc.extract", Ring.TOOLS, Op.UNZIP, catalogId = "arc.extract", kinds = setOf(FileKind.ZIP)),
        ToolDef("arc.zip", Ring.TOOLS, Op.ZIP, catalogId = "arc.zip", combine = true, minFiles = 1),

        // ---- Actions ----
        ToolDef("act.share", Ring.ACTIONS, Op.SHARE, catalogId = "act.share", combine = true),
        ToolDef("act.open-with", Ring.ACTIONS, Op.OPEN_WITH, labelKey = "tool_act_open_with", maxFiles = 1),
        ToolDef("act.copy", Ring.ACTIONS, Op.COPY_TO_FOLDER, catalogId = "act.copy", labelKey = "tool_act_copy_folder", combine = true),
    )

    fun byKey(key: String): ToolDef? = all.firstOrNull { it.key == key }

    /** Device features that decide whether some entries are shown. */
    data class Capabilities(val heicEncoder: Boolean)

    /**
     * The wheel for a selection: entries per ring, in table order. A tool that is not "combine" runs once per input,
     * so it is offered only when every input has a kind it accepts.
     */
    fun wheelFor(kinds: List<FileKind>, caps: Capabilities): Map<Ring, List<ToolDef>> {
        if (kinds.isEmpty()) return Ring.entries.associateWith { emptyList() }
        val distinct = kinds.toSet()
        val count = kinds.size
        val applicable = all.filter { def ->
            val kindOk = def.kinds.isEmpty() || def.kinds.containsAll(distinct)
            val countOk = count >= def.minFiles && (def.maxFiles == null || count <= def.maxFiles)
            val featureOk = !def.needsHeicEncoder || caps.heicEncoder
            val textOnlyAction = FileKind.TEXT in distinct && def.op == Op.OPEN_WITH
            kindOk && countOk && featureOk && !textOnlyAction
        }
        return Ring.entries.associateWith { ring -> applicable.filter { it.ring == ring } }
    }

    /** All option keys and choice values, for the label coverage test. */
    fun optionLabelKeys(): Set<String> = buildSet {
        for (def in all) for (o in def.options) {
            add("opt_${o.key}")
            if (o is OptionSpec.Choice) for (v in o.values) add("opt_${o.key}_$v")
        }
    }
}
