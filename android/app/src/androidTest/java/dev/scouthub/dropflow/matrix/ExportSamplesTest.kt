// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.matrix

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.scouthub.dropflow.core.FileKind
import dev.scouthub.dropflow.ops.Produced
import dev.scouthub.dropflow.work.Publisher
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Not part of the normal run: with `-Pandroid.testInstrumentationRunnerArguments.exportSamples=true` it saves every
 * generated sample to Download/DropFlow/samples, so they can be pulled (`adb pull`) or shared to DropFlow by
 * `android/scripts/share-smoke.sh`.
 */
@RunWith(AndroidJUnit4::class)
class ExportSamplesTest {
    @Test
    fun export() {
        // Not an assumption: AGP's XML report counts a failed assumption as a failure.
        if (InstrumentationRegistry.getArguments().getString("exportSamples") != "true") return
        val m = Matrix()
        val s = Samples(m.context)
        val files = listOfNotNull(
            s.jpegWithExif(), s.pngAlpha(), s.webp(), s.heic(), s.bmp(), s.qrPng(),
            s.pdfEnglish(), s.pdfArabic(), s.pdfArabicPresentationForms(), s.pdfScanned(), s.pdfManyPages(), s.pdfProtected(),
            s.mp4("phone clip.mp4"), s.mp4("portrait.mp4", rotation = 90), s.webm(), s.wav(), s.m4a(), s.ogg(),
            s.zipNested(), s.zipCp437(), s.zipSlip(),
        )
        for (f in files) {
            val mime = FileKind.mimeFor(FileKind.extensionOf(f.name))
            Publisher.publish(m.context, Produced(f, f.name, mime, folder = "samples", inDownloads = true))
        }
    }
}
