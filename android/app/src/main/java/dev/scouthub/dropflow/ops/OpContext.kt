// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import android.content.Context
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.work.InputFile
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.io.File

/** A file an operation made in its private work folder, waiting to be published to shared storage. */
data class Produced(
    val file: File,
    /** Name it gets in shared storage. */
    val name: String,
    val mime: String,
    /** Sub-folder under .../DropFlow/ for results made of many files (pages, parts, archive contents). */
    val folder: String? = null,
    /** Keep in Download/DropFlow whatever the type (archive contents stay together). */
    val inDownloads: Boolean = false,
)

/** What an operation returns: files to publish, and/or text to show (QR contents). */
data class OpResult(val files: List<Produced> = emptyList(), val text: String? = null)

/** An operation failed for a reason the person can act on; [messageKey] is a string resource name. */
class OpFailure(val messageKey: String, val detail: String? = null) : Exception(detail ?: messageKey)

/** Everything an operation needs: the app, a private folder for its outputs, options and progress reporting. */
class OpContext(
    val app: Context,
    val workDir: File,
    val options: Map<String, String>,
    private val onProgress: (Float?) -> Unit,
) {
    private val used = HashSet<String>()

    fun option(key: String, default: String): String = options[key]?.takeIf { it.isNotBlank() } ?: default

    fun intOption(key: String, default: Int): Int = options[key]?.trim()?.toIntOrNull() ?: default

    /** Progress 0..1 (null = unknown) for the current step. */
    fun progress(p: Float?) = onProgress(p?.coerceIn(0f, 1f))

    /** A new private file for an output; names never repeat within one job. */
    fun newFile(name: String): File {
        val unique = OutputNaming.unique(OutputNaming.sanitize(name)) { it in used || File(workDir, it).exists() }
        used += unique
        return File(workDir, unique)
    }

    /** A new private folder (for outputs made of many files). */
    fun newDir(name: String): File = newFile(name).also { it.mkdirs() }

    suspend fun checkCancelled() = currentCoroutineContext().ensureActive()

    /** Runs [block] once per input, mapping its progress into this job's overall progress. */
    suspend fun <T> eachInput(inputs: List<InputFile>, block: suspend (InputFile, (Float?) -> Unit) -> T): List<T> {
        val n = inputs.size
        return inputs.mapIndexed { i, input ->
            checkCancelled()
            block(input) { p -> onProgress(((i + (p ?: 0f)) / n).coerceIn(0f, 1f)) }
        }
    }
}
