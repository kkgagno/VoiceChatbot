package com.voicechatbot.android

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Message
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Waves
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Divider
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.lifecycle.viewmodel.compose.viewModel
import coil.compose.rememberAsyncImagePainter
import kotlinx.coroutines.launch
import java.io.File

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF39D7FF), secondary = Color(0xFF6B5DF6))) {
                val viewModel: AppViewModel = viewModel()
                LaunchedEffect(Unit) {
                    viewModel.reloadStoredMessages()
                }
                ServiceBroadcastBridge(viewModel)
                VoiceChatbotApp(viewModel)
            }
        }
    }
}

private enum class Tab(val title: String, val icon: ImageVector) {
    Chat("Chat", Icons.Default.Message),
    Transcribe("Transcribe", Icons.Default.Waves),
    Models("Models", Icons.Default.Computer),
    Comfy("ComfyUI", Icons.Default.Image),
    Krea2("Krea2", Icons.Default.PhotoCamera),
    More("More", Icons.Default.MoreHoriz)
}

@Composable
private fun VoiceChatbotApp(viewModel: AppViewModel) {
    val state by viewModel.state.collectAsState()
    var tab by remember { mutableStateOf(Tab.Chat) }
    val context = LocalContext.current

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {}

    LaunchedEffect(Unit) {
        permissionLauncher.launch(
            arrayOf(
                Manifest.permission.RECORD_AUDIO,
                Manifest.permission.CAMERA,
                Manifest.permission.POST_NOTIFICATIONS,
                Manifest.permission.READ_CONTACTS,
                Manifest.permission.READ_CALENDAR,
                Manifest.permission.WRITE_CALENDAR
            )
        )
    }

    Scaffold(
        bottomBar = {
            NavigationBar(containerColor = Color(0xEE101114)) {
                Tab.entries.forEach { item ->
                    NavigationBarItem(
                        selected = tab == item,
                        onClick = { tab = item },
                        icon = { Icon(item.icon, item.title) },
                        label = { Text(item.title) }
                    )
                }
            }
        }
    ) { padding ->
        Box(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .background(
                    Brush.linearGradient(
                        listOf(Color(0xFF050608), Color(0xFF071B20), Color(0xFF050608))
                    )
                )
        ) {
            Column(Modifier.fillMaxSize()) {
                Header(state, viewModel)
                when (tab) {
                    Tab.Chat -> ChatScreen(state, viewModel)
                    Tab.Transcribe -> TranscribeScreen(state, viewModel)
                    Tab.Models -> ModelsScreen(state, viewModel) { tab = Tab.Chat }
                    Tab.Comfy -> ComfyScreen(state, viewModel)
                    Tab.Krea2 -> Krea2Screen(state, viewModel)
                    Tab.More -> MoreScreen(state, viewModel, context)
                }
            }
        }
    }
}

@Composable
private fun Header(state: UiState, viewModel: AppViewModel) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(18.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text("Voice Chatbot", style = MaterialTheme.typography.headlineSmall)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Default.CheckCircle,
                    contentDescription = null,
                    tint = if (state.status?.ok == true) Color(0xFF35D66B) else Color(0xFFFFA726),
                    modifier = Modifier.size(18.dp)
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    if (state.status?.ok == true) "PC connected" else "Not connected",
                    color = Color.White
                )
            }
            Text(
                listOfNotNull(state.status?.activeModel, state.activeRoute.takeIf { it.isNotBlank() })
                    .joinToString(" • ")
                    .ifBlank { state.connectionMessage },
                color = Color.Gray,
                maxLines = 2
            )
        }
        IconButton(onClick = { viewModel.oneShotVoice() }) {
            Icon(Icons.Default.Mic, "Mic", tint = Color(0xFF39D7FF), modifier = Modifier.size(38.dp))
        }
    }
}

@Composable
private fun ChatScreen(state: UiState, viewModel: AppViewModel) {
    val listState = rememberLazyListState()
    LaunchedEffect(state.messages.size) {
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.lastIndex)
        }
    }
    Column(Modifier.fillMaxSize()) {
        LazyColumn(
            state = listState,
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            items(state.messages, key = { it.id }) { entry ->
                ChatBubble(entry, state.playingAudioUrl == entry.audioUrl, viewModel)
            }
        }
        ActiveConversationBar(state, viewModel)
        Composer(state, viewModel)
    }
}

@Composable
private fun ChatBubble(entry: ChatEntry, playing: Boolean, viewModel: AppViewModel) {
    val isUser = entry.role == Role.User
    Row(Modifier.fillMaxWidth(), horizontalArrangement = if (isUser) Arrangement.End else Arrangement.Start) {
        Card(
            colors = CardDefaults.cardColors(
                containerColor = when (entry.role) {
                    Role.User -> Color(0xFFFFF3C4)
                    Role.Assistant -> Color(0xFFDFF7DA)
                    Role.System -> Color(0xFF22252B)
                }
            ),
            shape = RoundedCornerShape(22.dp),
            modifier = Modifier.fillMaxWidth(0.82f)
        ) {
            Column(Modifier.padding(14.dp)) {
                Text(entry.role.name, color = if (entry.role == Role.System) Color.LightGray else Color(0xFF0B0B0B))
                Text(entry.text, color = if (entry.role == Role.System) Color.White else Color.Black)
                if (!entry.audioUrl.isNullOrBlank()) {
                    TextButton(onClick = { viewModel.playAudio(entry.audioUrl) }) {
                        Icon(if (playing) Icons.Default.Stop else Icons.Default.PlayArrow, null)
                        Text(if (playing) "Stop" else "Play")
                    }
                }
            }
        }
    }
}

@Composable
private fun ActiveConversationBar(state: UiState, viewModel: AppViewModel) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 6.dp),
        colors = CardDefaults.cardColors(containerColor = Color(0xDD20242A))
    ) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Waves, null, tint = Color(0xFF39D7FF))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text("Conversation", color = Color.White)
                Text(state.conversationState.label, color = Color.Gray)
            }
            if (state.serviceRunning) {
                Button(onClick = { viewModel.stopBackgroundConversation() }) {
                    Icon(Icons.Default.Stop, null)
                    Text("Stop")
                }
            } else {
                Button(onClick = { viewModel.startBackgroundConversation() }) {
                    Icon(Icons.Default.Mic, null)
                    Text("Background")
                }
            }
        }
    }
}

@Composable
private fun Composer(state: UiState, viewModel: AppViewModel) {
    val context = LocalContext.current
    val keyboardController = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current
    fun sendAndHideKeyboard() {
        viewModel.sendTypedMessage()
        keyboardController?.hide()
        focusManager.clearFocus(force = true)
    }
    val docPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        uris.forEach { uri ->
            val name = uri.lastPathSegment?.substringAfterLast('/') ?: "document"
            viewModel.addAttachment(uri, name, context.contentResolver.getType(uri) ?: "application/octet-stream", AttachmentKind.Document)
        }
    }
    val imagePicker = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        uris.forEachIndexed { index, uri ->
            viewModel.addAttachment(uri, "Photo-${index + 1}.jpg", context.contentResolver.getType(uri) ?: "image/jpeg", AttachmentKind.Image)
        }
    }
    var cameraUri by remember { mutableStateOf<Uri?>(null) }
    val cameraLauncher = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        val uri = cameraUri
        if (ok && uri != null) {
            viewModel.addAttachment(uri, "Camera-${System.currentTimeMillis()}.jpg", "image/jpeg", AttachmentKind.Image)
        }
    }

    Column(
        Modifier
            .fillMaxWidth()
            .background(Color(0xEE15171B))
            .padding(10.dp)
    ) {
        if (state.attachments.isNotEmpty()) {
            Text("${state.attachments.size} attachment(s) ready", color = Color(0xFF39D7FF))
            Row {
                Checkbox(state.keepDocumentsActive, onCheckedChange = { viewModel.setKeepDocumentsActive(it) })
                Text("Keep doc active", modifier = Modifier.align(Alignment.CenterVertically))
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { viewModel.clearAttachments() }) { Text("Clear") }
            }
        }
        Row(verticalAlignment = Alignment.Bottom) {
            IconButton(onClick = { imagePicker.launch("image/*") }) {
                Icon(Icons.Default.Image, "Photo", tint = Color(0xFF39D7FF))
            }
            IconButton(onClick = {
                val file = File(context.cacheDir, "camera-${System.currentTimeMillis()}.jpg")
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                cameraUri = uri
                cameraLauncher.launch(uri)
            }) {
                Icon(Icons.Default.PhotoCamera, "Camera", tint = Color(0xFF39D7FF))
            }
            IconButton(onClick = { docPicker.launch(arrayOf("*/*")) }) {
                Icon(Icons.Default.AttachFile, "Document", tint = Color(0xFF39D7FF))
            }
            OutlinedTextField(
                value = state.typedMessage,
                onValueChange = viewModel::setTyped,
                placeholder = { Text("Message or paste URL") },
                modifier = Modifier.weight(1f),
                maxLines = 4,
                keyboardOptions = KeyboardOptions(
                    capitalization = KeyboardCapitalization.Sentences,
                    imeAction = ImeAction.Send
                ),
                keyboardActions = KeyboardActions(onSend = { sendAndHideKeyboard() })
            )
            IconButton(onClick = { sendAndHideKeyboard() }) {
                Icon(Icons.Default.Send, "Send", tint = Color(0xFF39D7FF))
            }
        }
    }
}

@Composable
private fun TranscribeScreen(state: UiState, viewModel: AppViewModel) {
    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text("Transcribe", style = MaterialTheme.typography.headlineSmall)
        Text("Use the mic for one-shot transcription/chat or Background to keep the mic alive with a foreground notification.")
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = { viewModel.oneShotVoice() }) { Text("Mic once") }
            Button(onClick = { if (state.serviceRunning) viewModel.stopBackgroundConversation() else viewModel.startBackgroundConversation() }) {
                Text(if (state.serviceRunning) "Stop background" else "Background listen")
            }
        }
        Text("State: ${state.conversationState.label}")
    }
}

@Composable
private fun ModelsScreen(state: UiState, viewModel: AppViewModel, showChat: () -> Unit) {
    val commands = listOf(
        "Hermes stop current running llama.cpp model",
        "Hermes start gpt-oss:120b",
        "Hermes start gemma",
        "Hermes start gemma 4b",
        "Hermes start gemma 12b",
        "Hermes start gemma speculative",
        "Hermes start gemma 26b a4b",
        "Hermes start mistral",
        "Hermes start qwen",
        "Hermes start comfyui",
        "Hermes stop comfyui"
    )
    LazyColumn(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item {
            Text("Models", style = MaterialTheme.typography.headlineSmall)
            Text("Active: ${state.status?.activeModel ?: "unknown"}", color = Color.Gray)
        }
        items(commands) { command ->
            OutlinedButton(onClick = { showChat(); viewModel.runModelCommand(command) }, modifier = Modifier.fillMaxWidth()) {
                Text(command)
            }
        }
    }
}

@Composable
private fun ComfyScreen(state: UiState, viewModel: AppViewModel) {
    val context = LocalContext.current
    val mediaPicker = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        viewModel.clearAttachments()
        uris.forEachIndexed { index, uri ->
            viewModel.addAttachment(uri, "Comfy-${index + 1}.jpg", context.contentResolver.getType(uri) ?: "image/jpeg", AttachmentKind.Image)
        }
    }
    Column(
        Modifier
            .verticalScroll(rememberScrollState())
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("ComfyUI", style = MaterialTheme.typography.headlineSmall)
        Dropdown(
            label = "Workflow",
            selected = state.comfyAction,
            options = ComfyAction.entries,
            text = { it.title },
            onSelect = { viewModel.setComfyAction(it) }
        )
        OutlinedTextField(state.comfyPrompt, viewModel::setComfyPrompt, label = { Text("Prompt") }, modifier = Modifier.fillMaxWidth(), minLines = 3)
        if (state.comfyAction == ComfyAction.CreateVideo || state.comfyAction == ComfyAction.CreateVideoWithAudio) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Seconds: ${state.comfySeconds}")
                Spacer(Modifier.width(8.dp))
                OutlinedButton(onClick = { viewModel.setComfySeconds(state.comfySeconds - 1) }) { Text("-") }
                OutlinedButton(onClick = { viewModel.setComfySeconds(state.comfySeconds + 1) }) { Text("+") }
            }
        }
        if (state.comfyAction != ComfyAction.CreateImage) {
            OutlinedButton(onClick = { mediaPicker.launch("image/*") }) { Text("Choose source image") }
            Text("${state.attachments.size} source attachment(s)", color = Color.Gray)
        }
        Button(onClick = { viewModel.runComfy() }, enabled = !state.comfyBusy && state.comfyPrompt.isNotBlank()) {
            Text(if (state.comfyBusy) "Running..." else state.comfyAction.title)
        }
        state.comfyImageUri?.let { ResultImage(it) }
        state.comfyVideoUri?.let { Text("Video saved: $it", color = Color(0xFF39D7FF)) }
    }
}

@Composable
private fun Krea2Screen(state: UiState, viewModel: AppViewModel) {
    LaunchedEffect(Unit) { viewModel.refreshKrea2Options() }
    Column(
        Modifier
            .verticalScroll(rememberScrollState())
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Krea2", style = MaterialTheme.typography.headlineSmall)
        OutlinedTextField(state.kreaPrompt, viewModel::setKreaPrompt, label = { Text("Prompt") }, modifier = Modifier.fillMaxWidth(), minLines = 4)
        Dropdown("Picture size", state.kreaSelectedAspect, state.kreaOptions.aspectRatios.ifEmpty { Krea2Options.fallbackAspectRatios }, { it }, viewModel::setKreaAspect)
        Row(verticalAlignment = Alignment.CenterVertically) {
            Switch(state.kreaEnableLora, onCheckedChange = viewModel::setKreaEnableLora)
            Text("Enable LoRA?")
        }
        if (state.kreaEnableLora) {
            Dropdown("Krea2 LoRA", state.kreaSelectedLora, state.kreaOptions.loras.ifEmpty { listOf("") }, { it.ifBlank { "No Krea2 LoRAs found" } }, viewModel::setKreaLora)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedButton(onClick = { viewModel.refreshKrea2Options() }) {
                Icon(Icons.Default.Refresh, null)
                Text("Refresh LoRAs")
            }
            Button(onClick = { viewModel.runKrea2() }, enabled = !state.kreaBusy && state.kreaPrompt.isNotBlank()) {
                Text(if (state.kreaBusy) "Running..." else "Create")
            }
        }
        state.kreaImageUri?.let { ResultImage(it) }
    }
}

@Composable
private fun MoreScreen(state: UiState, viewModel: AppViewModel, context: Context) {
    val scope = rememberCoroutineScope()
    var calendarPrompt by remember { mutableStateOf("") }
    var smsPrompt by remember { mutableStateOf("") }
    Column(
        Modifier
            .verticalScroll(rememberScrollState())
            .padding(18.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Text("More", style = MaterialTheme.typography.headlineSmall)
        ConnectionCard(state, viewModel)
        Divider()
        Text("Calendar", style = MaterialTheme.typography.titleLarge)
        OutlinedTextField(calendarPrompt, { calendarPrompt = it }, label = { Text("Add calendar event...") }, modifier = Modifier.fillMaxWidth())
        Button(onClick = {
            scope.launch {
                runCatching { context.startActivity(viewModel.calendarIntentForPrompt(calendarPrompt)) }
            }
        }) {
            Icon(Icons.Default.CalendarMonth, null)
            Text("Prepare event")
        }
        Divider()
        Text("Text message", style = MaterialTheme.typography.titleLarge)
        OutlinedTextField(smsPrompt, { smsPrompt = it }, label = { Text("Send a text to...") }, modifier = Modifier.fillMaxWidth())
        Button(onClick = {
            scope.launch {
                runCatching { context.startActivity(viewModel.prepareSmsIntent(smsPrompt)) }
            }
        }) {
            Icon(Icons.Default.Message, null)
            Text("Prepare SMS")
        }
        Divider()
        Text("Health", style = MaterialTheme.typography.titleLarge)
        Text("Android Health Connect is stubbed for now. The tab is here, but live health data needs a Health Connect pass next.")
        OutlinedButton(onClick = { viewModel.healthAnswer("Can you use my Android health data?") }) {
            Icon(Icons.Default.Favorite, null)
            Text("Ask health status")
        }
        OutlinedButton(onClick = {
            context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}")))
        }) { Text("Android app settings") }
    }
}

@Composable
private fun ConnectionCard(state: UiState, viewModel: AppViewModel) {
    var profile by remember(state.profile) { mutableStateOf(state.profile) }
    Card(colors = CardDefaults.cardColors(containerColor = Color(0xDD15181D))) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Computer, null, tint = Color(0xFF39D7FF))
                Spacer(Modifier.width(8.dp))
                Text("Connection", style = MaterialTheme.typography.titleLarge)
            }
            OutlinedTextField(profile.name, { profile = profile.copy(name = it) }, label = { Text("Name") }, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(profile.localUrl, { profile = profile.copy(localUrl = it) }, label = { Text("Home Wi-Fi URL") }, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
            OutlinedTextField(profile.vpnUrl, { profile = profile.copy(vpnUrl = it) }, label = { Text("VPN / Tailscale URL") }, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
            OutlinedTextField(profile.pin, { profile = profile.copy(pin = it) }, label = { Text("PIN optional") }, modifier = Modifier.fillMaxWidth(), visualTransformation = PasswordVisualTransformation())
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = { viewModel.updateProfile(profile); kotlinx.coroutines.MainScope().launch { viewModel.refreshStatus() } }) {
                    Text(if (state.connecting) "Testing..." else "Save/Test")
                }
            }
            Text(state.connectionMessage, color = Color.Gray)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun <T> Dropdown(label: String, selected: T, options: List<T>, text: (T) -> String, onSelect: (T) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { expanded = !expanded }) {
        OutlinedTextField(
            value = text(selected),
            onValueChange = {},
            readOnly = true,
            label = { Text(label) },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded) },
            modifier = Modifier
                .menuAnchor()
                .fillMaxWidth()
        )
        ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            options.forEach { option ->
                DropdownMenuItem(
                    text = { Text(text(option)) },
                    onClick = { onSelect(option); expanded = false }
                )
            }
        }
    }
}

@Composable
private fun ResultImage(uri: Uri) {
    Image(
        painter = rememberAsyncImagePainter(uri),
        contentDescription = "Generated image",
        modifier = Modifier
            .fillMaxWidth()
            .height(360.dp)
            .background(Color.Black, RoundedCornerShape(18.dp)),
        contentScale = ContentScale.Fit
    )
}

@Composable
private fun ServiceBroadcastBridge(viewModel: AppViewModel) {
    val context = LocalContext.current
    DisposableEffect(Unit) {
        val messageReceiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                viewModel.receiveServiceMessage(
                    intent?.getStringExtra("role").orEmpty(),
                    intent?.getStringExtra("text").orEmpty(),
                    intent?.getStringExtra("audioUrl")
                )
            }
        }
        val stateReceiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                viewModel.receiveServiceState(intent?.getStringExtra("state").orEmpty())
            }
        }
        ContextCompat.registerReceiver(
            context,
            messageReceiver,
            IntentFilter(ConversationService.BROADCAST_MESSAGE),
            ContextCompat.RECEIVER_NOT_EXPORTED
        )
        ContextCompat.registerReceiver(
            context,
            stateReceiver,
            IntentFilter(ConversationService.BROADCAST_STATE),
            ContextCompat.RECEIVER_NOT_EXPORTED
        )
        onDispose {
            runCatching { context.unregisterReceiver(messageReceiver) }
            runCatching { context.unregisterReceiver(stateReceiver) }
        }
    }
}
