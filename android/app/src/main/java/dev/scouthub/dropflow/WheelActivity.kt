// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import dev.scouthub.dropflow.WheelViewModel.Screen
import dev.scouthub.dropflow.catalog.Op
import dev.scouthub.dropflow.catalog.ToolDef
import dev.scouthub.dropflow.ui.DropFlowTheme
import dev.scouthub.dropflow.ui.JobScreen
import dev.scouthub.dropflow.ui.Labels
import dev.scouthub.dropflow.ui.MediaEditorScreen
import dev.scouthub.dropflow.ui.MessageScreen
import dev.scouthub.dropflow.ui.OptionsScreen
import dev.scouthub.dropflow.ui.WheelScreen
import dev.scouthub.dropflow.work.JobCenter
import kotlinx.coroutines.launch

/**
 * The share target. Files shared from any app (or opened with DropFlow) are copied into private storage and the
 * wheel appears over the sending app. Work then runs in [dev.scouthub.dropflow.work.WorkService].
 */
class WheelActivity : ComponentActivity() {
    private val vm: WheelViewModel by viewModels()

    private val pickFolder = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { tree: Uri? ->
        if (tree == null) return@registerForActivityResult
        lifecycleScope.launch {
            val n = runCatching { Actions.copyToFolder(this@WheelActivity, tree, vm.inputs) }.getOrDefault(0)
            if (n > 0) {
                Toast.makeText(this@WheelActivity, resources.getQuantityString(R.plurals.copy_done, n, n), Toast.LENGTH_LONG).show()
                finish()
            } else {
                Toast.makeText(this@WheelActivity, R.string.copy_failed, Toast.LENGTH_LONG).show()
            }
        }
    }

    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun attachBaseContext(newBase: Context) = super.attachBaseContext(Language.wrap(newBase))

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        vm.start(intent)
        setContent {
            DropFlowTheme { Content() }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        val id = intent.getLongExtra(EXTRA_JOB_ID, -1L)
        if (id >= 0) vm.showJob(id)
    }

    override fun onStart() {
        super.onStart()
        (vm.screen as? Screen.Job)?.let { JobCenter.watching += it.id }
    }

    override fun onStop() {
        super.onStop()
        // Not visible any more: let the service post the "done" notification.
        (vm.screen as? Screen.Job)?.let { JobCenter.watching -= it.id }
    }

    private fun maybeAskNotifications() {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private fun onChoose(tool: ToolDef) {
        val inputs = vm.inputs
        when (tool.op) {
            Op.SHARE -> if (Actions.share(this, inputs.map { Actions.uriOf(this, it) }, inputs.map { it.mime })) finish()
            Op.OPEN_WITH -> inputs.firstOrNull()?.let { if (Actions.openWith(this, Actions.uriOf(this, it), it.mime)) finish() }
            Op.COPY_TO_FOLDER -> pickFolder.launch(null)
            else -> {
                if (tool.editor == null && tool.options.isEmpty()) maybeAskNotifications()
                vm.choose(tool, Labels.tool(this, tool))
            }
        }
    }

    @androidx.compose.runtime.Composable
    private fun Content() {
        val context = LocalContext.current
        val screen = vm.screen
        BackHandler {
            when (screen) {
                is Screen.Options, is Screen.MediaEditor -> vm.back()
                else -> finish()
            }
        }
        when (screen) {
            Screen.Loading -> MessageScreen(stringResource(R.string.wheel_preparing), null, busy = true, onClose = ::finish)
            Screen.Empty -> MessageScreen(stringResource(R.string.wheel_nothing), null, busy = false, onClose = ::finish)
            is Screen.Error -> MessageScreen(stringResource(R.string.result_failed), Labels.error(context, screen.messageKey), busy = false, onClose = ::finish)
            Screen.Wheel -> {
                val inputs = vm.inputs
                val count = pluralStringResource(R.plurals.files_count, inputs.size, inputs.size)
                val summary = if (inputs.size == 1) inputs[0].name else "$count · ${inputs[0].name}…"
                WheelScreen(
                    summary = summary,
                    rings = vm.rings,
                    label = { Labels.tool(context, it) },
                    onChoose = ::onChoose,
                    onDismiss = ::finish,
                )
            }
            is Screen.Options -> OptionsScreen(
                title = screen.title,
                tool = screen.tool,
                prefill = vm.prefillFor(screen.tool),
                onBack = vm::back,
                onStart = { options ->
                    maybeAskNotifications()
                    vm.submit(screen.tool, screen.title, options)
                },
            )
            is Screen.MediaEditor -> MediaEditorScreen(
                file = vm.inputs.single().file,
                durationMs = screen.durationMs,
                mode = screen.tool.editor!!,
                onBack = vm::back,
                onStart = { options ->
                    maybeAskNotifications()
                    vm.submit(screen.tool, screen.title, options)
                },
            )
            is Screen.Job -> {
                val states by JobCenter.states.collectAsState()
                JobScreen(
                    title = screen.title,
                    state = states[screen.id],
                    onCancel = { JobCenter.cancel(this, screen.id) },
                    onClose = ::finish,
                    onOpen = { Actions.open(this, it.uri, it.mime) },
                    onShare = { outs -> Actions.share(this, outs.map { it.uri }, outs.map { it.mime }) },
                    onCopyText = { Actions.copyText(this, it) },
                )
            }
        }
    }

    companion object {
        const val EXTRA_JOB_ID = "dev.scouthub.dropflow.JOB_ID"
    }
}
