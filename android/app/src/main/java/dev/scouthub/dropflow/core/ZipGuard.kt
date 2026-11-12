// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

import java.io.File

/**
 * Protects archive extraction from "zip slip" (entries such as `../../x` or `/etc/x` that would land outside the
 * destination) and from archives that expand without limit.
 */
object ZipGuard {
    class UnsafeEntry(name: String) : SecurityException("Unsafe path in archive: $name")
    class TooLarge(message: String) : SecurityException(message)

    /** Where [entryName] may be written inside [destination], or [UnsafeEntry] when it would escape it. */
    fun resolve(destination: File, entryName: String): File {
        if (entryName.isEmpty() || entryName.indexOf('\u0000') >= 0) throw UnsafeEntry(entryName)
        val name = entryName.replace('\\', '/')
        if (name.startsWith("/") || name.startsWith("~")) throw UnsafeEntry(entryName)
        if (name.length >= 2 && name[1] == ':') throw UnsafeEntry(entryName) // C:/...
        val parts = name.split('/').filter { it.isNotEmpty() && it != "." }
        if (parts.isEmpty() || parts.any { it == ".." }) throw UnsafeEntry(entryName)
        val root = destination.canonicalFile
        val target = File(root, parts.joinToString(File.separator)).canonicalFile
        if (target == root || !target.path.startsWith(root.path + File.separator)) throw UnsafeEntry(entryName)
        return target
    }

    /**
     * Android 14+ (apps targeting API 34+) checks zip entry names itself and throws a ZipException
     * "Invalid zip entry path: ..." for names with ".." or a leading "/". That is the same refusal as [resolve], not
     * a damaged archive, and must be reported as unsafe.
     */
    fun isPlatformRefusal(e: Throwable): Boolean = e.message?.startsWith("Invalid zip entry path") == true

    /**
     * Android's ZipFile does not fail on names that are not UTF-8: it puts U+FFFD in them. Archives from old
     * Windows (no UTF-8 flag, DOS code page) then extracted as "R?sum?.txt" (with U+FFFD). Such names mean: reopen with CP437.
     */
    fun needsLegacyNames(names: List<String>): Boolean = names.any { it.indexOf('�') >= 0 }

    /** Counts what an extraction writes and stops it past the limits. */
    class Budget(private val maxEntries: Int = 20_000, private val maxBytes: Long = 8L * 1024 * 1024 * 1024) {
        var entries = 0
            private set
        var bytes = 0L
            private set

        fun addEntry() {
            if (++entries > maxEntries) throw TooLarge("More than $maxEntries entries")
        }

        fun addBytes(n: Long) {
            bytes += n
            if (bytes > maxBytes) throw TooLarge("Expands to more than $maxBytes bytes")
        }
    }
}
