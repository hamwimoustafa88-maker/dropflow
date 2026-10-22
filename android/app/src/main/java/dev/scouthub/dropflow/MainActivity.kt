// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import dev.scouthub.dropflow.ui.DfColors
import dev.scouthub.dropflow.ui.DropFlowTheme

/** Home screen: how to use DropFlow, a file picker for people who open the app directly, and the language switch. */
class MainActivity : ComponentActivity() {
    private val pick = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris: List<Uri> ->
        if (uris.isEmpty()) return@registerForActivityResult
        val intent = Intent(this, WheelActivity::class.java)
            .setAction(Intent.ACTION_SEND_MULTIPLE)
            .setType("*/*")
            .putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        intent.clipData = ClipData.newRawUri(null, uris[0]).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
        startActivity(intent)
    }

    override fun attachBaseContext(newBase: Context) = super.attachBaseContext(Language.wrap(newBase))

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent { DropFlowTheme { Home() } }
    }

    private fun createQr(text: String) {
        startActivity(
            Intent(this, WheelActivity::class.java).setAction(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text),
        )
    }

    private fun setLanguage(language: String) {
        Language.set(this, language)
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) recreate()
    }

    @Composable
    private fun Home() {
        var askQr by rememberSaveable { mutableStateOf(false) }
        val language = remember { Language.get(this) }
        Box(
            Modifier
                .fillMaxSize()
                .background(DfColors.Bg)
                .safeDrawingPadding(),
            contentAlignment = Alignment.TopCenter,
        ) {
            Column(
                Modifier
                    .widthIn(max = 560.dp)
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    Box(
                        Modifier
                            .size(64.dp)
                            .background(DfColors.Navy, RoundedCornerShape(16.dp)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Image(painterResource(R.drawable.ic_launcher_foreground), contentDescription = null, modifier = Modifier.size(96.dp))
                    }
                    Column {
                        Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
                        Text(stringResource(R.string.home_tagline), color = DfColors.Primary, fontWeight = FontWeight.SemiBold)
                    }
                }
                Text(stringResource(R.string.home_body), style = MaterialTheme.typography.bodyLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(onClick = { pick.launch(arrayOf("*/*")) }) { Text(stringResource(R.string.home_pick)) }
                    OutlinedButton(onClick = { askQr = true }) { Text(stringResource(R.string.home_create_qr)) }
                }
                InfoCard(stringResource(R.string.home_privacy_title), stringResource(R.string.home_privacy))
                InfoCard(stringResource(R.string.home_where_title), stringResource(R.string.home_where))
                Text(stringResource(R.string.home_language), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for ((code, label) in listOf(
                        Language.SYSTEM to stringResource(R.string.lang_system),
                        Language.ENGLISH to stringResource(R.string.lang_en),
                        Language.ARABIC to stringResource(R.string.lang_ar),
                    )) {
                        FilterChip(
                            selected = language == code,
                            onClick = { setLanguage(code) },
                            label = { Text(label) },
                            colors = FilterChipDefaults.filterChipColors(
                                selectedContainerColor = DfColors.PrimarySoft,
                                selectedLabelColor = DfColors.Primary,
                            ),
                        )
                    }
                }
                Text(
                    stringResource(R.string.home_about, BuildConfig.VERSION_NAME),
                    style = MaterialTheme.typography.bodySmall,
                    color = DfColors.Muted,
                )
            }
        }
        if (askQr) QrDialog(onDismiss = { askQr = false }, onCreate = { askQr = false; createQr(it) })
    }

    @Composable
    private fun InfoCard(title: String, body: String) {
        Card(
            colors = CardDefaults.cardColors(containerColor = DfColors.Surface),
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(title, fontWeight = FontWeight.SemiBold)
                Text(body, style = MaterialTheme.typography.bodyMedium, color = DfColors.Muted)
            }
        }
    }

    @Composable
    private fun QrDialog(onDismiss: () -> Unit, onCreate: (String) -> Unit) {
        var text by rememberSaveable { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(stringResource(R.string.home_create_qr)) },
            text = {
                OutlinedTextField(value = text, onValueChange = { text = it }, label = { Text(stringResource(R.string.home_qr_hint)) }, minLines = 2)
            },
            confirmButton = { Button(enabled = text.isNotBlank(), onClick = { onCreate(text) }) { Text(stringResource(R.string.start)) } },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
        )
    }
}
