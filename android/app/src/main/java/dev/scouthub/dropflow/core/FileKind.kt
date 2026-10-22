// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/** What kind of file an input is. Decides which petals the wheel offers. */
enum class FileKind(val catalogCategory: String?) {
    IMAGE("image"),
    VIDEO("video"),
    AUDIO("audio"),
    PDF("pdf"),
    ZIP("archive"),
    TEXT(null),
    OTHER(null);

    companion object {
        private val images = setOf("jpg", "jpeg", "png", "webp", "heic", "heif", "avif", "bmp", "gif")
        private val videos = setOf("mp4", "m4v", "mov", "mkv", "webm", "3gp", "3gpp", "ts")
        private val audios = setOf("mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "amr", "3ga")
        private val texts = setOf("txt", "text")

        /** Kind from a MIME type first (what the sharing app says), then from the file extension. */
        fun of(fileName: String, mime: String?): FileKind {
            val m = mime?.lowercase()?.substringBefore(';')?.trim().orEmpty()
            when {
                m == "application/pdf" -> return PDF
                m == "application/zip" || m == "application/x-zip-compressed" -> return ZIP
                m == "image/gif" -> return IMAGE
                m.startsWith("image/") && m != "image/svg+xml" -> return IMAGE
                m.startsWith("video/") -> return VIDEO
                m.startsWith("audio/") -> return AUDIO
                m == "text/plain" -> return TEXT
            }
            return when (extensionOf(fileName)) {
                in images -> IMAGE
                in videos -> VIDEO
                in audios -> AUDIO
                "pdf" -> PDF
                "zip" -> ZIP
                in texts -> TEXT
                else -> OTHER
            }
        }

        fun extensionOf(fileName: String): String {
            val name = fileName.substringAfterLast('/').substringAfterLast('\\')
            val dot = name.lastIndexOf('.')
            return if (dot <= 0 || dot == name.length - 1) "" else name.substring(dot + 1).lowercase()
        }

        /** MIME type for a file DropFlow writes. */
        fun mimeFor(extension: String): String = when (extension.lowercase()) {
            "jpg", "jpeg" -> "image/jpeg"
            "png" -> "image/png"
            "webp" -> "image/webp"
            "heic", "heif" -> "image/heic"
            "gif" -> "image/gif"
            "bmp" -> "image/bmp"
            "avif" -> "image/avif"
            "pdf" -> "application/pdf"
            "zip" -> "application/zip"
            "txt" -> "text/plain"
            "mp4", "m4v" -> "video/mp4"
            "mov" -> "video/quicktime"
            "mkv" -> "video/x-matroska"
            "webm" -> "video/webm"
            "3gp" -> "video/3gpp"
            "m4a" -> "audio/mp4"
            "aac" -> "audio/aac"
            "mp3" -> "audio/mpeg"
            "wav" -> "audio/wav"
            "flac" -> "audio/flac"
            "ogg", "oga" -> "audio/ogg"
            "opus" -> "audio/opus"
            else -> "application/octet-stream"
        }
    }
}
