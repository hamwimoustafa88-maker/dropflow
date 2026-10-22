// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ui

import android.content.Context
import dev.scouthub.dropflow.Language
import dev.scouthub.dropflow.R
import dev.scouthub.dropflow.catalog.Catalog
import dev.scouthub.dropflow.catalog.OptionSpec
import dev.scouthub.dropflow.catalog.ToolDef

/**
 * Text for wheel entries, options and errors. Catalog tools take their ar/en labels from catalog/tools.json;
 * everything else comes from strings.xml through this explicit table (so resource shrinking keeps them, and a unit
 * test can check that every key used by the tool table has a string in both languages).
 */
object Labels {
    val strings: Map<String, Int> = mapOf(
        // Format targets and Android-only tools
        "fmt_jpg" to R.string.fmt_jpg,
        "fmt_png" to R.string.fmt_png,
        "fmt_webp" to R.string.fmt_webp,
        "fmt_heic" to R.string.fmt_heic,
        "fmt_pdf" to R.string.fmt_pdf,
        "fmt_txt" to R.string.fmt_txt,
        "fmt_mp4_h264" to R.string.fmt_mp4_h264,
        "fmt_mp4_hevc" to R.string.fmt_mp4_hevc,
        "fmt_m4a" to R.string.fmt_m4a,
        "fmt_wav" to R.string.fmt_wav,
        "tool_img_rotate" to R.string.tool_img_rotate,
        "tool_qr_create" to R.string.tool_qr_create,
        "tool_pdf_strip" to R.string.tool_pdf_strip,
        "tool_act_open_with" to R.string.tool_act_open_with,
        "tool_act_copy_folder" to R.string.tool_act_copy_folder,
        // Options
        "opt_compress_by" to R.string.opt_compress_by,
        "opt_compress_by_quality" to R.string.opt_compress_by_quality,
        "opt_compress_by_size" to R.string.opt_compress_by_size,
        "opt_quality" to R.string.opt_quality,
        "opt_quality_85" to R.string.opt_quality_85,
        "opt_quality_75" to R.string.opt_quality_75,
        "opt_quality_60" to R.string.opt_quality_60,
        "opt_quality_45" to R.string.opt_quality_45,
        "opt_target_kb" to R.string.opt_target_kb,
        "opt_format" to R.string.opt_format,
        "opt_format_auto" to R.string.opt_format_auto,
        "opt_format_jpg" to R.string.opt_format_jpg,
        "opt_format_webp" to R.string.opt_format_webp,
        "opt_resize_by" to R.string.opt_resize_by,
        "opt_resize_by_long_edge" to R.string.opt_resize_by_long_edge,
        "opt_resize_by_percent" to R.string.opt_resize_by_percent,
        "opt_long_edge" to R.string.opt_long_edge,
        "opt_long_edge_720" to R.string.opt_long_edge_720,
        "opt_long_edge_1080" to R.string.opt_long_edge_1080,
        "opt_long_edge_1600" to R.string.opt_long_edge_1600,
        "opt_long_edge_2048" to R.string.opt_long_edge_2048,
        "opt_long_edge_3840" to R.string.opt_long_edge_3840,
        "opt_percent" to R.string.opt_percent,
        "opt_percent_25" to R.string.opt_percent_25,
        "opt_percent_50" to R.string.opt_percent_50,
        "opt_percent_75" to R.string.opt_percent_75,
        "opt_angle" to R.string.opt_angle,
        "opt_angle_90" to R.string.opt_angle_90,
        "opt_angle_180" to R.string.opt_angle_180,
        "opt_angle_270" to R.string.opt_angle_270,
        "opt_strip" to R.string.opt_strip,
        "opt_strip_location" to R.string.opt_strip_location,
        "opt_strip_all" to R.string.opt_strip_all,
        "opt_page_size" to R.string.opt_page_size,
        "opt_page_size_a4" to R.string.opt_page_size_a4,
        "opt_page_size_image" to R.string.opt_page_size_image,
        "opt_dpi" to R.string.opt_dpi,
        "opt_dpi_72" to R.string.opt_dpi_72,
        "opt_dpi_150" to R.string.opt_dpi_150,
        "opt_dpi_300" to R.string.opt_dpi_300,
        "opt_qr_text" to R.string.opt_qr_text,
        "opt_pdf_level" to R.string.opt_pdf_level,
        "opt_pdf_level_light" to R.string.opt_pdf_level_light,
        "opt_pdf_level_balanced" to R.string.opt_pdf_level_balanced,
        "opt_pdf_level_strong" to R.string.opt_pdf_level_strong,
        "opt_split_mode" to R.string.opt_split_mode,
        "opt_split_mode_each" to R.string.opt_split_mode_each,
        "opt_split_mode_ranges" to R.string.opt_split_mode_ranges,
        "opt_ranges" to R.string.opt_ranges,
        "opt_pages" to R.string.opt_pages,
        "opt_preset" to R.string.opt_preset,
        "opt_preset_small" to R.string.opt_preset_small,
        "opt_preset_balanced" to R.string.opt_preset_balanced,
        "opt_preset_high" to R.string.opt_preset_high,
        // Errors
        "error_decode_image" to R.string.error_decode_image,
        "error_format" to R.string.error_format,
        "error_no_heic_encoder" to R.string.error_no_heic_encoder,
        "error_encode" to R.string.error_encode,
        "error_target_size" to R.string.error_target_size,
        "error_strip_failed" to R.string.error_strip_failed,
        "error_no_qr" to R.string.error_no_qr,
        "error_qr_empty" to R.string.error_qr_empty,
        "error_qr_too_long" to R.string.error_qr_too_long,
        "error_pdf_protected" to R.string.error_pdf_protected,
        "error_pdf_damaged" to R.string.error_pdf_damaged,
        "error_page_range" to R.string.error_page_range,
        "error_media_read" to R.string.error_media_read,
        "error_media_encode" to R.string.error_media_encode,
        "error_no_video" to R.string.error_no_video,
        "error_no_audio" to R.string.error_no_audio,
        "error_range" to R.string.error_range,
        "error_frame" to R.string.error_frame,
        "error_zip_damaged" to R.string.error_zip_damaged,
        "error_zip_unsafe" to R.string.error_zip_unsafe,
        "error_zip_too_large" to R.string.error_zip_too_large,
        "error_zip_empty" to R.string.error_zip_empty,
        "error_memory" to R.string.error_memory,
        "error_unexpected" to R.string.error_unexpected,
        "error_input" to R.string.error_input,
    )

    /** Error keys the operations can raise (checked by a unit test against [strings]). */
    val errorKeys: Set<String> get() = strings.keys.filter { it.startsWith("error_") }.toSet()

    private fun text(context: Context, key: String): String = strings[key]?.let(context::getString) ?: key

    fun tool(context: Context, def: ToolDef): String {
        def.labelKey?.let { return text(context, it) }
        val entry = def.catalogId?.let(Catalog::tool) ?: return def.key
        return entry.label(Language.isArabic(context))
    }

    fun option(context: Context, spec: OptionSpec): String = text(context, "opt_${spec.key}")

    fun choice(context: Context, spec: OptionSpec, value: String): String = text(context, "opt_${spec.key}_$value")

    fun error(context: Context, key: String): String = strings[key]?.let(context::getString) ?: context.getString(R.string.error_unexpected)
}
