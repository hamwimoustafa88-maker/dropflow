// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.work

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.IntentCompat
import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.core.OutputNaming
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/** A private copy of something shared with DropFlow. Originals are never opened for writing. */
data class InputFile(val file: File, val name: String, val mime: String, val kind: FileKind)

object Inputs {
    /** Everything an incoming intent carries: streams (SEND, SEND_MULTIPLE, VIEW, clip data) and shared text. */
    fun urisOf(context: Context, intent: Intent): List<Uri> {
        val out = LinkedHashSet<Uri>()
        when (intent.action) {
            Intent.ACTION_SEND -> IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let(out::add)
            Intent.ACTION_SEND_MULTIPLE ->
                IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let(out::addAll)
            Intent.ACTION_VIEW -> intent.data?.let(out::add)
        }
        intent.clipData?.let { clip -> for (i in 0 until clip.itemCount) clip.getItemAt(i).uri?.let(out::add) }
        // Only content:// from other apps. file:// is refused: another app could point it at DropFlow's private files
        // and have them copied out. DropFlow's own FileProvider is refused for the same reason.
        val own = "${context.packageName}.files"
        return out.filter { it.scheme == "content" && it.authority != own }
    }

    fun textOf(intent: Intent): String? =
        if (intent.action == Intent.ACTION_SEND) intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()?.takeIf { it.isNotBlank() } else null

    /**
     * Copies each input into the app's cache so work never depends on (or changes) the original, and so the
     * permission the sharing app granted can lapse safely. Shared text becomes a small text input (for QR codes).
     */
    suspend fun copy(context: Context, uris: List<Uri>, text: String?, onProgress: (Float) -> Unit = {}): List<InputFile> =
        withContext(Dispatchers.IO) {
            val dir = File(context.cacheDir, "inputs/${System.currentTimeMillis()}").apply { mkdirs() }
            val taken = HashSet<String>()
            fun place(name: String): File {
                val unique = OutputNaming.unique(OutputNaming.sanitize(name)) { it in taken }
                taken += unique
                return File(dir, unique)
            }
            val result = mutableListOf<InputFile>()
            if (uris.isEmpty() && text != null) {
                val f = place("text.txt")
                f.writeText(text, Charsets.UTF_8)
                result += InputFile(f, f.name, "text/plain", FileKind.TEXT)
                return@withContext result
            }
            val resolver = context.contentResolver
            uris.forEachIndexed { i, uri ->
                val display = displayName(context, uri) ?: uri.lastPathSegment ?: "file"
                val mime = resolver.getType(uri) ?: FileKind.mimeFor(FileKind.extensionOf(display))
                val target = place(display)
                resolver.openInputStream(uri)?.use { input -> target.outputStream().use { input.copyTo(it, 256 * 1024) } }
                    ?: throw java.io.FileNotFoundException(uri.toString())
                result += InputFile(target, target.name, mime, FileKind.of(target.name, mime))
                onProgress((i + 1f) / uris.size)
            }
            result
        }

    private fun displayName(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        }
    }.getOrNull()

    /** Removes copies and job folders from earlier sessions (older than a day). */
    fun cleanOld(context: Context) {
        val cutoff = System.currentTimeMillis() - 24 * 60 * 60 * 1000L
        for (sub in listOf("inputs", "jobs")) {
            File(context.cacheDir, sub).listFiles()?.filter { it.lastModified() < cutoff }?.forEach { it.deleteRecursively() }
        }
    }
}
