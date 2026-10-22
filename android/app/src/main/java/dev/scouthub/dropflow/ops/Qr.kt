// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.ops

import android.graphics.Bitmap
import android.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.EncodeHintType
import com.google.zxing.LuminanceSource
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.WriterException
import com.google.zxing.common.GlobalHistogramBinarizer
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import kotlin.math.max

/** QR codes with ZXing core (Apache-2.0). */
object Qr {
    fun read(bitmap: Bitmap): String? {
        attempt(bitmap)?.let { return it }
        for (edge in listOf(1024, 600)) {
            val long = max(bitmap.width, bitmap.height)
            if (long <= edge) continue
            val s = edge.toFloat() / long
            val small = Bitmap.createScaledBitmap(bitmap, max(1, (bitmap.width * s).toInt()), max(1, (bitmap.height * s).toInt()), true)
            attempt(small)?.let { return it }
        }
        return null
    }

    private fun attempt(bitmap: Bitmap): String? {
        val w = bitmap.width
        val h = bitmap.height
        val pixels = IntArray(w * h)
        bitmap.getPixels(pixels, 0, w, 0, 0, w, h)
        val source: LuminanceSource = RGBLuminanceSource(w, h, pixels)
        val reader = MultiFormatReader().apply {
            setHints(mapOf(DecodeHintType.TRY_HARDER to true, DecodeHintType.CHARACTER_SET to "UTF-8"))
        }
        val candidates = listOf(
            { BinaryBitmap(HybridBinarizer(source)) },
            { BinaryBitmap(GlobalHistogramBinarizer(source)) },
            { BinaryBitmap(HybridBinarizer(source.invert())) },
        )
        for (make in candidates) {
            try {
                return reader.decodeWithState(make()).text
            } catch (_: ReaderException) {
            } finally {
                reader.reset()
            }
        }
        return null
    }

    fun create(text: String, targetPixels: Int = 1024): Bitmap {
        val matrix = try {
            QRCodeWriter().encode(
                text, BarcodeFormat.QR_CODE, 0, 0,
                mapOf(
                    EncodeHintType.CHARACTER_SET to "UTF-8",
                    EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
                    EncodeHintType.MARGIN to 4,
                ),
            )
        } catch (e: WriterException) {
            throw OpFailure("error_qr_too_long", e.message)
        } catch (e: IllegalArgumentException) {
            throw OpFailure("error_qr_too_long", e.message)
        }
        val modules = matrix.width
        val scale = max(1, targetPixels / modules)
        val size = modules * scale
        val pixels = IntArray(size * size)
        for (y in 0 until size) {
            val my = y / scale
            for (x in 0 until size) pixels[y * size + x] = if (matrix.get(x / scale, my)) Color.BLACK else Color.WHITE
        }
        return Bitmap.createBitmap(pixels, size, size, Bitmap.Config.ARGB_8888)
    }
}
