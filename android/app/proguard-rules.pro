# DropFlow release shrinking rules (R8).
# PdfBox-Android loads fonts, glyph lists and CMaps from its own resources and reaches a few classes by reflection.
-keep class com.tom_roush.pdfbox.** { *; }
-keep class com.tom_roush.fontbox.** { *; }
-keep class com.tom_roush.harmony.** { *; }
# "Remove location and EXIF" lists ExifInterface's TAG_ constants by reflection (ops/ImageOps.kt): keep them as fields.
-keepclassmembers class androidx.exifinterface.media.ExifInterface {
    public static final java.lang.String TAG_*;
}
# Optional JPEG 2000 decoder that PdfBox-Android can use when present; DropFlow does not ship it.
-dontwarn com.gemalto.jp2.**
