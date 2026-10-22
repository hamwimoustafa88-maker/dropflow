// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.core.content.FileProvider
import androidx.documentfile.provider.DocumentFile
import dev.scouthub.dropflow.work.InputFile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Share, open with and copy to a folder: handing files to other apps or places the person chooses. */
object Actions {
    private fun authority(context: Context) = "${context.packageName}.files"

    fun uriOf(context: Context, input: InputFile): Uri = FileProvider.getUriForFile(context, authority(context), input.file, input.name)

    private fun commonMime(mimes: List<String>): String {
        val distinct = mimes.distinct()
        if (distinct.size == 1) return distinct[0]
        val top = distinct.map { it.substringBefore('/') }.distinct()
        return if (top.size == 1) "${top[0]}/*" else "*/*"
    }

    /** Opens the system share sheet for [uris]. Returns false when nothing can receive them. */
    fun share(context: Context, uris: List<Uri>, mimes: List<String>): Boolean {
        if (uris.isEmpty()) return false
        val intent = if (uris.size == 1) {
            Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
        }
        intent.type = commonMime(mimes)
        intent.clipData = ClipData.newRawUri(null, uris[0]).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        return start(context, Intent.createChooser(intent, context.getString(R.string.share_chooser)))
    }

    fun openWith(context: Context, uri: Uri, mime: String): Boolean {
        val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        return start(context, Intent.createChooser(intent, context.getString(R.string.open_chooser)))
    }

    /** Opens a result with the default app for its type (or a chooser when there is none). */
    fun open(context: Context, uri: Uri, mime: String): Boolean {
        val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        return start(context, intent, quiet = true) || openWith(context, uri, mime)
    }

    private fun start(context: Context, intent: Intent, quiet: Boolean = false): Boolean = try {
        if (context !is android.app.Activity) intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
        true
    } catch (_: ActivityNotFoundException) {
        if (!quiet) Toast.makeText(context, R.string.no_app_to_open, Toast.LENGTH_LONG).show()
        false
    }

    /** Copies the private copies into a folder chosen with the system picker. Existing files are never replaced. */
    suspend fun copyToFolder(context: Context, tree: Uri, inputs: List<InputFile>): Int = withContext(Dispatchers.IO) {
        val dir = DocumentFile.fromTreeUri(context, tree) ?: return@withContext 0
        var copied = 0
        for (input in inputs) {
            val target = dir.createFile(input.mime, input.name) ?: continue // the provider picks a free name
            context.contentResolver.openOutputStream(target.uri)?.use { out -> input.file.inputStream().use { it.copyTo(out) } } ?: continue
            copied++
        }
        copied
    }

    fun copyText(context: Context, text: String) {
        context.getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("DropFlow", text))
        Toast.makeText(context, R.string.copied, Toast.LENGTH_SHORT).show()
    }
}
