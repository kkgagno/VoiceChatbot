package com.voicechatbot.android

import android.app.Application
import android.content.ContentValues
import android.content.Intent
import android.net.Uri
import android.provider.CalendarContract
import android.provider.MediaStore
import android.provider.Settings
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.TimeZone

data class UiState(
    val profile: ServerProfile = ServerProfile(),
    val status: ServerStatus? = null,
    val activeRoute: String = "",
    val activeBaseUrl: String = "",
    val connectionMessage: String = "Not connected",
    val connecting: Boolean = false,
    val messages: List<ChatEntry> = emptyList(),
    val typedMessage: String = "",
    val attachments: List<PendingAttachment> = emptyList(),
    val keepDocumentsActive: Boolean = false,
    val activeDocumentCount: Int = 0,
    val conversationState: ConversationState = ConversationState.Idle,
    val serviceRunning: Boolean = false,
    val playingAudioUrl: String? = null,
    val comfyAction: ComfyAction = ComfyAction.CreateImage,
    val comfyPrompt: String = "",
    val comfySeconds: Int = 6,
    val comfyBusy: Boolean = false,
    val comfyImageUri: Uri? = null,
    val comfyVideoUri: Uri? = null,
    val kreaPrompt: String = "",
    val kreaEnableLora: Boolean = false,
    val kreaSelectedLora: String = "",
    val kreaSelectedAspect: String = Krea2Options.fallbackAspectRatios.first(),
    val kreaOptions: Krea2Options = Krea2Options(),
    val kreaBusy: Boolean = false,
    val kreaImageUri: Uri? = null
)

class AppViewModel(application: Application) : AndroidViewModel(application) {
    private val app: Application
        get() = getApplication()
    private val prefs = application.getSharedPreferences("voicechatbot", 0)
    private val recorder = AudioRecorder(application)
    private val wavPlayer = WavAudioPlayer()
    private var api: VoiceChatApi? = null

    private val _state = MutableStateFlow(loadState())
    val state: StateFlow<UiState> = _state

    init {
        viewModelScope.launch { refreshStatus() }
    }

    fun updateProfile(profile: ServerProfile) {
        prefs.edit()
            .putString("name", profile.name)
            .putString("localUrl", profile.localUrl)
            .putString("vpnUrl", profile.vpnUrl)
            .putString("pin", profile.pin)
            .apply()
        _state.update { it.copy(profile = profile) }
    }

    fun setTyped(text: String) = _state.update { it.copy(typedMessage = text) }
    fun setKeepDocumentsActive(value: Boolean) = _state.update { it.copy(keepDocumentsActive = value) }
    fun setComfyAction(value: ComfyAction) = _state.update { it.copy(comfyAction = value, attachments = emptyList()) }
    fun setComfyPrompt(value: String) = _state.update { it.copy(comfyPrompt = value) }
    fun setComfySeconds(value: Int) = _state.update { it.copy(comfySeconds = value.coerceIn(1, 30)) }
    fun setKreaPrompt(value: String) = _state.update { it.copy(kreaPrompt = value) }
    fun setKreaEnableLora(value: Boolean) = _state.update { it.copy(kreaEnableLora = value) }
    fun setKreaLora(value: String) = _state.update { it.copy(kreaSelectedLora = value) }
    fun setKreaAspect(value: String) = _state.update { it.copy(kreaSelectedAspect = value) }

    fun addAttachment(uri: Uri, name: String, mimeType: String, kind: AttachmentKind) {
        _state.update {
            it.copy(attachments = it.attachments + PendingAttachment(name = name, mimeType = mimeType, uri = uri, kind = kind))
        }
    }

    fun clearAttachments() = _state.update { it.copy(attachments = emptyList()) }

    suspend fun refreshStatus() {
        val profile = _state.value.profile
        _state.update { it.copy(connecting = true, connectionMessage = "Connecting...") }
        for ((route, url) in profile.candidates) {
            try {
                val probe = VoiceChatApi(app, profile, url)
                val status = probe.status()
                api = probe
                _state.update {
                    it.copy(
                        status = status,
                        activeRoute = route,
                        activeBaseUrl = url,
                        connectionMessage = "$route connected",
                        connecting = false
                    )
                }
                return
            } catch (e: Exception) {
                _state.update { it.copy(connectionMessage = "$route failed: ${e.message}") }
            }
        }
        _state.update {
            it.copy(status = null, activeRoute = "", activeBaseUrl = "", connecting = false, connectionMessage = "Could not connect")
        }
    }

    fun sendTypedMessage() {
        viewModelScope.launch {
            val text = _state.value.typedMessage.trim()
            val attachments = _state.value.attachments
            if (text.isBlank() && attachments.isEmpty()) return@launch
            _state.update {
                it.copy(
                    typedMessage = "",
                    attachments = emptyList(),
                    messages = it.messages + ChatEntry(role = Role.User, text = text.ifBlank { "Sent attachments" }),
                    conversationState = ConversationState.Thinking
                )
            }
            runCatching {
                val response = requireApi().message(text, attachments, _state.value.keepDocumentsActive)
                applyAssistantResponse(response)
            }.onFailure { fail(it) }
        }
    }

    fun oneShotVoice() {
        viewModelScope.launch {
            runCatching {
                _state.update { it.copy(conversationState = ConversationState.Listening) }
                val wav = recorder.recordSegment()
                _state.update { it.copy(conversationState = ConversationState.Transcribing) }
                val transcript = requireApi().transcribe(wav)
                if (transcript.isBlank()) {
                    _state.update {
                        it.copy(
                            messages = it.messages + ChatEntry(role = Role.Assistant, text = "I did not catch that."),
                            conversationState = ConversationState.Idle
                        )
                    }
                    return@launch
                }
                _state.update { it.copy(messages = it.messages + ChatEntry(role = Role.User, text = transcript)) }
                _state.update { it.copy(conversationState = ConversationState.Thinking) }
                applyAssistantResponse(requireApi().respond(transcript, _state.value.keepDocumentsActive), play = true)
            }.onFailure { fail(it) }
        }
    }

    fun startBackgroundConversation() {
        val current = _state.value
        val intent = Intent(app, ConversationService::class.java)
            .setAction(ConversationService.ACTION_START)
            .putExtra(ConversationService.EXTRA_LOCAL_URL, current.profile.localUrl)
            .putExtra(ConversationService.EXTRA_VPN_URL, current.profile.vpnUrl)
            .putExtra(ConversationService.EXTRA_PIN, current.profile.pin)
            .putExtra(ConversationService.EXTRA_BASE_URL, current.activeBaseUrl.ifBlank { current.profile.candidates.firstOrNull()?.second.orEmpty() })
        ContextCompat.startForegroundService(app, intent)
        _state.update { it.copy(serviceRunning = true, conversationState = ConversationState.Listening) }
    }

    fun stopBackgroundConversation() {
        app.startService(
            Intent(app, ConversationService::class.java).setAction(ConversationService.ACTION_STOP)
        )
        _state.update { it.copy(serviceRunning = false, conversationState = ConversationState.Idle) }
    }

    fun receiveServiceMessage(role: String, text: String, audioUrl: String?) {
        val parsedRole = runCatching { Role.valueOf(role) }.getOrDefault(Role.System)
        _state.update { it.copy(messages = it.messages + ChatEntry(role = parsedRole, text = text, audioUrl = audioUrl)) }
        if (parsedRole == Role.Assistant && !audioUrl.isNullOrBlank() && !_state.value.serviceRunning) {
            playAudio(audioUrl)
        }
    }

    fun receiveServiceState(text: String) {
        val state = ConversationState.entries.firstOrNull { it.label == text } ?: ConversationState.Idle
        _state.update { it.copy(conversationState = state) }
    }

    fun playAudio(relativeUrl: String?) {
        if (relativeUrl.isNullOrBlank()) return
        if (_state.value.playingAudioUrl == relativeUrl) {
            stopAudio()
            return
        }
        viewModelScope.launch {
            runCatching {
                stopAudio()
                val file = requireApi().mediaFile(relativeUrl, "voicechat-audio-${System.currentTimeMillis()}.wav")
                _state.update { it.copy(playingAudioUrl = relativeUrl) }
                wavPlayer.play(file) {
                    _state.update { it.copy(playingAudioUrl = null) }
                }
            }.onFailure {
                _state.update { old -> old.copy(playingAudioUrl = null) }
                fail(it)
            }
        }
    }

    fun stopAudio() {
        wavPlayer.stop()
        _state.update { it.copy(playingAudioUrl = null) }
    }

    fun runModelCommand(command: String) {
        viewModelScope.launch {
            _state.update { it.copy(messages = it.messages + ChatEntry(role = Role.User, text = command), conversationState = ConversationState.Thinking) }
            runCatching {
                val reply = requireApi().tool(command)
                _state.update { it.copy(messages = it.messages + ChatEntry(role = Role.Assistant, text = reply), conversationState = ConversationState.Idle) }
                refreshStatus()
            }.onFailure { fail(it) }
        }
    }

    fun runComfy() {
        viewModelScope.launch {
            val s = _state.value
            val command = when (s.comfyAction) {
                ComfyAction.CreateImage -> "create an image of ${s.comfyPrompt}"
                ComfyAction.EditImage -> "edit this image ${s.comfyPrompt}"
                ComfyAction.CreateVideo -> "create a ${s.comfySeconds} second video ${s.comfyPrompt}"
                ComfyAction.CreateVideoWithAudio -> "create a ${s.comfySeconds} second video with audio ${s.comfyPrompt}"
            }
            _state.update { it.copy(comfyBusy = true) }
            runCatching {
                val response = requireApi().message(command, s.attachments, false)
                val imageUri = response.imageUrl?.let { downloadToMedia(it, "VoiceChatbot-${System.currentTimeMillis()}.jpg", "image/jpeg") }
                val videoUri = response.videoUrl?.let { downloadToMedia(it, "VoiceChatbot-${System.currentTimeMillis()}.mp4", "video/mp4") }
                _state.update {
                    it.copy(
                        comfyBusy = false,
                        comfyImageUri = imageUri,
                        comfyVideoUri = videoUri,
                        messages = it.messages + ChatEntry(role = Role.Assistant, text = response.response.ifBlank { "ComfyUI result ready." })
                    )
                }
            }.onFailure {
                _state.update { old -> old.copy(comfyBusy = false) }
                fail(it)
            }
        }
    }

    fun refreshKrea2Options() {
        viewModelScope.launch {
            runCatching {
                val options = requireApi().krea2Options()
                _state.update {
                    it.copy(
                        kreaOptions = options,
                        kreaSelectedLora = it.kreaSelectedLora.ifBlank { options.loras.firstOrNull().orEmpty() },
                        kreaSelectedAspect = it.kreaSelectedAspect.ifBlank { options.aspectRatios.firstOrNull() ?: Krea2Options.fallbackAspectRatios.first() }
                    )
                }
            }.onFailure { fail(it) }
        }
    }

    fun runKrea2() {
        viewModelScope.launch {
            val s = _state.value
            _state.update { it.copy(kreaBusy = true) }
            runCatching {
                val response = requireApi().createKrea2Image(
                    prompt = s.kreaPrompt,
                    enableLora = s.kreaEnableLora,
                    loraName = s.kreaSelectedLora,
                    aspectRatio = s.kreaSelectedAspect
                )
                val imageUri = response.imageUrl?.let { downloadToMedia(it, "Krea2-${System.currentTimeMillis()}.jpg", "image/jpeg") }
                _state.update {
                    it.copy(
                        kreaBusy = false,
                        kreaImageUri = imageUri,
                        messages = it.messages + ChatEntry(role = Role.Assistant, text = response.response.ifBlank { "Krea2 image ready." })
                    )
                }
            }.onFailure {
                _state.update { old -> old.copy(kreaBusy = false) }
                fail(it)
            }
        }
    }

    suspend fun calendarIntentForPrompt(prompt: String): Intent {
        val draft = requireApi().prepareCalendarEvent(
            prompt,
            ZonedDateTime.now().format(DateTimeFormatter.ISO_OFFSET_DATE_TIME),
            TimeZone.getDefault().id
        )
        val start = parseMillis(draft.start)
        val end = parseMillis(draft.end).takeIf { it > start } ?: start + 60 * 60 * 1000
        return Intent(Intent.ACTION_INSERT)
            .setData(CalendarContract.Events.CONTENT_URI)
            .putExtra(CalendarContract.Events.TITLE, draft.title)
            .putExtra(CalendarContract.Events.DESCRIPTION, draft.notes)
            .putExtra(CalendarContract.EXTRA_EVENT_BEGIN_TIME, start)
            .putExtra(CalendarContract.EXTRA_EVENT_END_TIME, end)
    }

    suspend fun prepareSmsIntent(prompt: String): Intent {
        val draft = requireApi().prepareTextMessage(prompt)
        return Intent(Intent.ACTION_SENDTO).apply {
            data = Uri.parse("smsto:")
            putExtra("sms_body", "To: ${draft.recipient}\n\n${draft.body}")
        }
    }

    fun healthAnswer(prompt: String) {
        viewModelScope.launch {
            _state.update { it.copy(messages = it.messages + ChatEntry(role = Role.User, text = prompt), conversationState = ConversationState.Thinking) }
            runCatching {
                val answer = requireApi().groundedAnswer(
                    """
                    The user is on Android. Health data is not connected yet. Have a brief conversation
                    explaining that Android Health Connect support needs to be enabled in this app before
                    live health questions can be answered.

                    User question:
                    $prompt
                    """.trimIndent()
                )
                _state.update { it.copy(messages = it.messages + ChatEntry(role = Role.Assistant, text = answer), conversationState = ConversationState.Idle) }
            }.onFailure { fail(it) }
        }
    }

    private suspend fun applyAssistantResponse(response: AssistantResponse, play: Boolean = false) {
        _state.update {
            it.copy(
                messages = it.messages + ChatEntry(role = Role.Assistant, text = response.response, audioUrl = response.audioUrl),
                activeDocumentCount = response.activeDocumentCount,
                status = it.status?.copy(
                    activeProvider = response.activeProvider ?: it.status.activeProvider,
                    activeModel = response.activeModel ?: it.status.activeModel,
                    activeEndpoint = response.activeEndpoint ?: it.status.activeEndpoint
                ),
                conversationState = ConversationState.Idle
            )
        }
        if (play) playAudio(response.audioUrl)
    }

    private suspend fun downloadToMedia(relativeUrl: String, filename: String, mimeType: String): Uri? {
        val file = requireApi().mediaFile(relativeUrl, filename)
        val resolver = app.contentResolver
        val collection = if (mimeType.startsWith("video")) MediaStore.Video.Media.EXTERNAL_CONTENT_URI
        else MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, filename)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
        }
        val uri = resolver.insert(collection, values) ?: return null
        resolver.openOutputStream(uri)?.use { out -> file.inputStream().use { it.copyTo(out) } }
        return uri
    }

    private fun parseMillis(value: String): Long {
        return runCatching { Instant.parse(value).toEpochMilli() }.getOrElse {
            runCatching {
                ZonedDateTime.parse(value).withZoneSameInstant(ZoneId.systemDefault()).toInstant().toEpochMilli()
            }.getOrDefault(System.currentTimeMillis())
        }
    }

    private fun requireApi(): VoiceChatApi {
        api?.let { return it }
        val s = _state.value
        val base = s.activeBaseUrl.ifBlank { s.profile.candidates.firstOrNull()?.second.orEmpty() }
        return VoiceChatApi(app, s.profile, base).also { api = it }
    }

    private fun fail(error: Throwable) {
        _state.update {
            it.copy(
                conversationState = ConversationState.Failed,
                messages = it.messages + ChatEntry(role = Role.System, text = error.message ?: "Unknown error")
            )
        }
    }

    private fun loadState(): UiState {
        val profile = ServerProfile(
            name = prefs.getString("name", "Home PC") ?: "Home PC",
            localUrl = prefs.getString("localUrl", "https://192.168.4.114:5100") ?: "https://192.168.4.114:5100",
            vpnUrl = prefs.getString("vpnUrl", "http://minilagertha.tail2762b8.ts.net:5101") ?: "http://minilagertha.tail2762b8.ts.net:5101",
            pin = prefs.getString("pin", "") ?: ""
        )
        return UiState(profile = profile)
    }
}
