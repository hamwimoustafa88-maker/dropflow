// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.app.Application
import dev.scouthub.dropflow.work.Inputs
import dev.scouthub.dropflow.work.Notifications
import kotlin.concurrent.thread

class DropFlowApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Notifications.ensureChannels(this)
        thread(name = "dropflow-clean", isDaemon = true) { Inputs.cleanOld(this) }
    }
}
