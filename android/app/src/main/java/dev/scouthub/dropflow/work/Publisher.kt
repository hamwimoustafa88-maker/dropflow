// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.work

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.ops.Produced

/** A result saved in shared storage. */
data class OutputItem(val uri: Uri, val name: String, val mime: String, val size: Long, val location: String)

/**
 * Saves results through MediaStore (no storage permission needed on Android 10+):
 * images in Pictures/DropFlow, video in Movies/DropFlow, audio in Music/DropFlow, everything else in Download/DropFlow.
 */
object Publisher {
    private data class Target(val collection: Uri, val directory: String)

    private val downloads: Target
        get() = Target(MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), Environment.DIRECTORY_DOWNLOADS)

    private fun targetFor(mime: String, inDownloads: Boolean = false): Target = if (inDownloads) downloads else when {
        mime.startsWith("image/") -> Target(MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), Environment.DIRECTORY_PICTURES)
        mime.startsWith("video/") -> Target(MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), Environment.DIRECTORY_MOVIES)
        mime.startsWith("audio/") -> Target(MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY), Environment.DIRECTORY_MUSIC)
        else -> downloads
    }

    /** Relative folder a result lands in, e.g. "Pictures/DropFlow/report (pages)". Sub-folders may nest ("a/b"). */
    fun locationFor(mime: String, folder: String?, inDownloads: Boolean = false): String {
        val sub = folder?.split('/')?.map { OutputNaming.sanitize(it) }?.joinToString("/")
        return targetFor(mime, inDownloads).directory + "/DropFlow" + (sub?.let { "/$it" } ?: "")
    }

    /**
     * The MIME type this device associates with the extension, when it agrees on the top-level type. MediaProvider
     * renames files whose extension does not match the MIME type it knows (for example audio/wav vs audio/x-wav).
     */
    private fun deviceMime(name: String, fallback: String): String {
        val ext = dev.scouthub.dropflow.core.FileKind.extensionOf(name)
        val known = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: return fallback
        return if (known.substringBefore('/') == fallback.substringBefore('/')) known else fallback
    }

    fun publish(context: Context, item: Produced): OutputItem {
        val mime = deviceMime(item.name, item.mime)
        val target = targetFor(mime, item.inDownloads)
        val relative = locationFor(mime, item.folder, item.inDownloads) + "/"
        val resolver = context.contentResolver
        val name = OutputNaming.unique(OutputNaming.sanitize(item.name)) { exists(context, target.collection, relative, it) }
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, relative)
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(target.collection, values) ?: throw java.io.IOException("MediaStore refused $name")
        try {
            resolver.openOutputStream(uri, "w")?.use { out -> item.file.inputStream().use { it.copyTo(out, 256 * 1024) } }
                ?: throw java.io.IOException("Cannot write $name")
            values.clear()
            values.put(MediaStore.MediaColumns.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
        } catch (e: Exception) {
            runCatching { resolver.delete(uri, null, null) }
            throw e
        }
        return OutputItem(uri, name, mime, item.file.length(), relative.trimEnd('/'))
    }

    private fun exists(context: Context, collection: Uri, relative: String, name: String): Boolean = runCatching {
        context.contentResolver.query(
            collection,
            arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME} = ? AND ${MediaStore.MediaColumns.RELATIVE_PATH} = ?",
            arrayOf(name, relative),
            null,
        )?.use { it.count > 0 } ?: false
    }.getOrDefault(false)
}
