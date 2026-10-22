// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * Names results the same way as the Windows app (src/DropFlow.Core/Output/OutputPathResolver.cs):
 * "name (converted).ext", then "name (converted 2).ext", "name (converted 3).ext" when the name is taken.
 * Originals are never touched: DropFlow works on private copies and writes new files.
 */
object OutputNaming {
    private const val MAX_NAME = 120

    /** File name without its extension ("photo.final.jpg" -> "photo.final"). */
    fun stem(fileName: String): String {
        val name = fileName.substringAfterLast('/').substringAfterLast('\\')
        val dot = name.lastIndexOf('.')
        return if (dot <= 0) name else name.substring(0, dot)
    }

    /** "photo.png" + "jpg" -> "photo (converted).jpg". A null extension keeps the source extension. */
    fun forFile(sourceName: String, extension: String?, suffix: String = "converted"): String {
        val ext = (extension ?: FileKind.extensionOf(sourceName)).trimStart('.')
        val stem = stem(sanitize(sourceName))
        return if (ext.isEmpty()) "$stem ($suffix)" else "$stem ($suffix).$ext"
    }

    /** Folder for results made of many files: "report (pages)". */
    fun forFolder(sourceName: String, suffix: String): String = "${stem(sanitize(sourceName))} ($suffix)"

    /** First free name: "a (converted).jpg", "a (converted 2).jpg", ... */
    fun unique(name: String, exists: (String) -> Boolean): String {
        if (!exists(name)) return name
        val dot = name.lastIndexOf('.')
        val hasExt = dot > 0 && dot > name.lastIndexOf(')')
        val stem = if (hasExt) name.substring(0, dot) else name
        val ext = if (hasExt) name.substring(dot) else ""
        val close = stem.lastIndexOf(')')
        val open = stem.lastIndexOf('(')
        for (n in 2..100_000) {
            val candidate = if (open in 0 until close && close == stem.length - 1) {
                "${stem.substring(0, close)} $n)$ext"
            } else {
                "$stem ($n)$ext"
            }
            if (!exists(candidate)) return candidate
        }
        error("No free name for $name")
    }

    /**
     * A display name from another app made safe as a single file name: no path separators, no characters that
     * Android's FAT/exFAT storage rejects, no control characters, not empty, not "." or "..", and not too long.
     */
    fun sanitize(displayName: String?): String {
        val raw = displayName.orEmpty().substringAfterLast('/').substringAfterLast('\\')
        val cleaned = buildString {
            for (ch in raw) {
                when {
                    ch.code < 32 || ch.code == 127 -> {}
                    ch in "<>:\"|?*" -> append('_')
                    else -> append(ch)
                }
            }
        }.trim().trimEnd('.').trim()
        val safe = if (cleaned.isEmpty() || cleaned == "." || cleaned == "..") "file" else cleaned
        if (safe.length <= MAX_NAME) return safe
        val ext = FileKind.extensionOf(safe)
        val keep = MAX_NAME - (if (ext.isEmpty()) 0 else ext.length + 1)
        return safe.substring(0, keep).trimEnd() + (if (ext.isEmpty()) "" else ".$ext")
    }
}
