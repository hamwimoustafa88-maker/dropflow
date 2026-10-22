// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.work

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import dev.scouthub.dropflow.R
import dev.scouthub.dropflow.WheelActivity

object Notifications {
    const val CHANNEL_WORK = "work"
    const val CHANNEL_DONE = "done"
    const val FOREGROUND_ID = 1

    fun ensureChannels(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_WORK, context.getString(R.string.notif_channel_work), NotificationManager.IMPORTANCE_LOW).apply {
                setShowBadge(false)
            },
        )
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_DONE, context.getString(R.string.notif_channel_done), NotificationManager.IMPORTANCE_DEFAULT),
        )
    }

    fun progress(context: Context, title: String, progress: Float?, queued: Int, cancel: PendingIntent?): Notification =
        NotificationCompat.Builder(context, CHANNEL_WORK)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(context.getColor(R.color.df_primary))
            .setContentTitle(title)
            .setContentText(
                if (queued > 0) context.resources.getQuantityString(R.plurals.notif_queued, queued, queued) else context.getString(R.string.notif_working),
            )
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setProgress(100, ((progress ?: 0f) * 100).toInt(), progress == null)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .apply { if (cancel != null) addAction(0, context.getString(R.string.cancel), cancel) }
            .build()

    /** "Done" or "Failed" notification; tapping it opens the result sheet. */
    fun finished(context: Context, jobId: Long, title: String, text: String) {
        val open = PendingIntent.getActivity(
            context, jobId.toInt(),
            Intent(context, WheelActivity::class.java).putExtra(WheelActivity.EXTRA_JOB_ID, jobId)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val n = NotificationCompat.Builder(context, CHANNEL_DONE)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(context.getColor(R.color.df_primary))
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(open)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(jobId.toInt(), n)
        } catch (_: SecurityException) {
            // Notifications not allowed: the result sheet still shows when the wheel is open.
        }
    }
}
