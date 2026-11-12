// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.core

/**
 * Foreground-service type for the work service: mediaProcessing on Android 15+ (no 6-hour dataSync limit for long
 * video exports), dataSync before. Values are android.content.pm.ServiceInfo constants.
 *
 * Must be passed to Service.startForeground(id, notification, type) directly: androidx.core 1.18
 * ServiceCompat.startForeground masks the type to the API 34 set, so mediaProcessing became "none" and Android 15+
 * killed the app on every job ("Starting FGS with type none ... has been prohibited"). Found on the API 35 emulator.
 */
object ForegroundPlan {
    const val DATA_SYNC = 0x1
    const val MEDIA_PROCESSING = 0x2000
    const val USE_SERVICE_COMPAT = false

    fun type(sdk: Int): Int = if (sdk >= 35) MEDIA_PROCESSING else DATA_SYNC
}
