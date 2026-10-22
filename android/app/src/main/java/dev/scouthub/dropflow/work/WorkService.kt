// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.work

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import dev.scouthub.dropflow.Language
import dev.scouthub.dropflow.R
import dev.scouthub.dropflow.ops.OpFailure
import dev.scouthub.dropflow.ui.Labels
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Runs jobs one after another in a foreground service with a progress notification, so long video exports keep
 * going when the wheel is closed. Results are published through MediaStore; nothing leaves the device.
 */
class WorkService : LifecycleService() {
    private val queue = Channel<Long>(Channel.UNLIMITED)
    private val pending = AtomicInteger(0)
    private val running = ConcurrentHashMap<Long, Job>()
    private var foreground = false
    private var lastNotify = 0L
    private var lastStartId = 0

    override fun attachBaseContext(base: Context) = super.attachBaseContext(Language.wrap(base))

    override fun onCreate() {
        super.onCreate()
        Notifications.ensureChannels(this)
        lifecycleScope.launch {
            for (id in queue) {
                runJob(id)
                if (pending.decrementAndGet() <= 0) {
                    ServiceCompat.stopForeground(this@WorkService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    foreground = false
                    // With the newest start id: if another job arrived meanwhile, the service keeps running.
                    stopSelf(lastStartId)
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        lastStartId = startId
        val id = intent?.getLongExtra(EXTRA_JOB_ID, -1L) ?: -1L
        when (intent?.action) {
            ACTION_START -> {
                goForeground(id)
                if (id >= 0) {
                    pending.incrementAndGet()
                    queue.trySend(id)
                }
            }
            ACTION_CANCEL -> {
                val job = running[id]
                if (job != null) job.cancel() else if (JobCenter.state(id) == JobState.Queued) JobCenter.set(id, JobState.Cancelled)
            }
        }
        if (!foreground) stopSelf(startId)
        return START_NOT_STICKY
    }

    private fun goForeground(id: Long) {
        if (foreground) return
        val title = JobCenter.request(id)?.title ?: getString(R.string.app_name)
        val type = if (Build.VERSION.SDK_INT >= 35) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROCESSING else ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
        ServiceCompat.startForeground(this, Notifications.FOREGROUND_ID, Notifications.progress(this, title, null, 0, cancelIntent(id)), type)
        foreground = true
    }

    private fun cancelIntent(id: Long): PendingIntent = PendingIntent.getService(
        this, id.toInt(), cancelIntent(this, id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun updateNotification(request: JobRequest, progress: Float?, force: Boolean = false) {
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastNotify < 500) return
        lastNotify = now
        try {
            NotificationManagerCompat.from(this).notify(
                Notifications.FOREGROUND_ID,
                Notifications.progress(this, request.title, progress, (pending.get() - 1).coerceAtLeast(0), cancelIntent(request.id)),
            )
        } catch (_: SecurityException) {
        }
    }

    private suspend fun runJob(id: Long) {
        val request = JobCenter.request(id) ?: return
        if (JobCenter.state(id) == JobState.Cancelled) return
        val workDir = File(cacheDir, "jobs/$id").apply { mkdirs() }
        JobCenter.set(id, JobState.Running(0f))
        updateNotification(request, 0f, force = true)
        val job = lifecycleScope.launch(Dispatchers.Default) {
            try {
                val result = Runner.run(this@WorkService, request, workDir) { p ->
                    JobCenter.set(id, JobState.Running(p))
                    updateNotification(request, p)
                }
                val outputs = withContext(Dispatchers.IO + NonCancellable) { result.files.map { Publisher.publish(this@WorkService, it) } }
                JobCenter.set(id, JobState.Done(outputs, result.text))
                if (id !in JobCenter.watching) {
                    val text = when {
                        result.text != null -> result.text
                        outputs.isEmpty() -> getString(R.string.result_nothing)
                        else -> resources.getQuantityString(R.plurals.result_saved, outputs.size, outputs.size, outputs.first().location)
                    }
                    Notifications.finished(this@WorkService, id, getString(R.string.notif_done_title, request.title), text)
                }
            } catch (e: CancellationException) {
                JobCenter.set(id, JobState.Cancelled)
            } catch (e: OpFailure) {
                Log.w(TAG, "Job $id failed: ${e.messageKey} ${e.detail}")
                fail(id, request, e.messageKey, e.detail)
            } catch (e: OutOfMemoryError) {
                Log.w(TAG, "Job $id ran out of memory", e)
                fail(id, request, "error_memory", e.toString())
            } catch (e: Exception) {
                Log.w(TAG, "Job $id failed", e)
                fail(id, request, "error_unexpected", e.toString())
            } finally {
                workDir.deleteRecursively()
            }
        }
        running[id] = job
        job.join()
        running.remove(id)
    }

    private fun fail(id: Long, request: JobRequest, key: String, detail: String?) {
        JobCenter.set(id, JobState.Failed(key, detail))
        if (id !in JobCenter.watching) {
            Notifications.finished(this, id, getString(R.string.notif_failed_title, request.title), Labels.error(this, key))
        }
    }

    companion object {
        private const val TAG = "DropFlow"
        private const val ACTION_START = "dev.scouthub.dropflow.START"
        private const val ACTION_CANCEL = "dev.scouthub.dropflow.CANCEL"
        private const val EXTRA_JOB_ID = "job"

        fun startIntent(context: Context, id: Long) =
            Intent(context, WorkService::class.java).setAction(ACTION_START).putExtra(EXTRA_JOB_ID, id)

        fun cancelIntent(context: Context, id: Long) =
            Intent(context, WorkService::class.java).setAction(ACTION_CANCEL).putExtra(EXTRA_JOB_ID, id)
    }
}
