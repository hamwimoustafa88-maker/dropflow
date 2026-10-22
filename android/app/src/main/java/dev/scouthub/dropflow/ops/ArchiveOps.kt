// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.core.OutputNaming
import dev.scouthub.dropflow.core.ZipGuard
import dev.scouthub.dropflow.work.InputFile
import java.io.File
import java.nio.charset.Charset
import java.util.zip.ZipEntry
import java.util.zip.ZipException
import java.util.zip.ZipFile
import java.util.zip.ZipOutputStream

/** ZIP archives with java.util.zip. Extraction goes through [ZipGuard] (no zip slip, bounded size). */
object ArchiveOps {
    suspend fun zip(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val out = ctx.newFile(OutputNaming.forFile(inputs.first().name, "zip", "zipped"))
        val total = inputs.sumOf { it.file.length() }.coerceAtLeast(1)
        var done = 0L
        ZipOutputStream(out.outputStream().buffered()).use { zip ->
            for (input in inputs) {
                ctx.checkCancelled()
                zip.putNextEntry(ZipEntry(input.name).apply { time = input.file.lastModified() })
                input.file.inputStream().use { src ->
                    val buf = ByteArray(256 * 1024)
                    while (true) {
                        val n = src.read(buf)
                        if (n < 0) break
                        zip.write(buf, 0, n)
                        done += n
                        ctx.progress(done.toFloat() / total)
                    }
                }
                zip.closeEntry()
            }
        }
        return OpResult(listOf(Produced(out, out.name, "application/zip")))
    }

    private fun openZip(file: File): ZipFile {
        val charsets = listOf(Charsets.UTF_8) + listOf("IBM437", "Cp437", "ISO-8859-1").mapNotNull { runCatching { Charset.forName(it) }.getOrNull() }
        var last: Exception? = null
        for (cs in charsets) {
            try {
                val z = ZipFile(file, cs)
                z.entries().toList() // names decode now, or fail and try the next charset
                return z
            } catch (e: IllegalArgumentException) {
                last = e
            } catch (e: ZipException) {
                throw OpFailure("error_zip_damaged", e.message)
            }
        }
        throw OpFailure("error_zip_damaged", last?.message)
    }

    suspend fun unzip(ctx: OpContext, inputs: List<InputFile>): OpResult {
        val files = ctx.eachInput(inputs) { input, progress ->
            val folderName = OutputNaming.forFolder(input.name, "extracted")
            val dest = ctx.newDir(folderName)
            val budget = ZipGuard.Budget()
            val produced = mutableListOf<Produced>()
            openZip(input.file).use { zip ->
                val entries = zip.entries().toList()
                // Refuse the whole archive if any entry would escape, before writing anything.
                val targets = entries.map { e ->
                    try {
                        e to ZipGuard.resolve(dest, e.name)
                    } catch (x: ZipGuard.UnsafeEntry) {
                        throw OpFailure("error_zip_unsafe", x.message)
                    }
                }
                targets.forEachIndexed { i, (entry, target) ->
                    ctx.checkCancelled()
                    budget.addEntry()
                    if (entry.isDirectory) {
                        target.mkdirs()
                        return@forEachIndexed
                    }
                    target.parentFile?.mkdirs()
                    try {
                        zip.getInputStream(entry).use { src ->
                            target.outputStream().use { dst ->
                                val buf = ByteArray(256 * 1024)
                                while (true) {
                                    val n = src.read(buf)
                                    if (n < 0) break
                                    budget.addBytes(n.toLong())
                                    dst.write(buf, 0, n)
                                }
                            }
                        }
                    } catch (e: ZipGuard.TooLarge) {
                        throw OpFailure("error_zip_too_large", e.message)
                    } catch (e: ZipException) {
                        throw OpFailure("error_zip_damaged", e.message)
                    }
                    val relativeParent = target.parentFile!!.relativeTo(dest.canonicalFile).path.replace(File.separatorChar, '/')
                    val folder = if (relativeParent.isEmpty()) folderName else "$folderName/$relativeParent"
                    produced += Produced(target, target.name, FileKind.mimeFor(FileKind.extensionOf(target.name)), folder, inDownloads = true)
                    progress((i + 1f) / targets.size)
                }
            }
            if (produced.isEmpty()) throw OpFailure("error_zip_empty")
            produced
        }.flatten()
        return OpResult(files)
    }
}
