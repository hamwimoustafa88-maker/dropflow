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
