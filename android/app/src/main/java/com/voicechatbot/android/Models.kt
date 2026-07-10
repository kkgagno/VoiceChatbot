package com.voicechatbot.android

import android.net.Uri
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.util.UUID

@Serializable
data class ServerProfile(
    val name: String = "Home PC",
    val localUrl: String = "https://192.168.4.114:5100",
    val vpnUrl: String = "http://minilagertha.tail2762b8.ts.net:5101",
    val pin: String = ""
) {
    val candidates: List<Pair<String, String>>
        get() = listOf("Home Wi-Fi" to localUrl, "VPN" to vpnUrl)
            .map { it.first to it.second.trim() }
            .filter { it.second.isNotEmpty() }
            .distinctBy { it.second }
}

@Serializable
data class ServerStatus(
    val ok: Boolean = false,
    val requiresPin: Boolean = false,
    val activeProvider: String = "",
    val activeModel: String = "",
    val activeEndpoint: String = ""
)

@Serializable
data class AssistantResponse(
    val transcript: String = "",
    val response: String = "",
    @SerialName("audioUrl") val audioUrl: String? = null,
    @SerialName("imageUrl") val imageUrl: String? = null,
    @SerialName("videoUrl") val videoUrl: String? = null,
    val activeDocumentCount: Int = 0,
    val activeProvider: String? = null,
    val activeModel: String? = null,
    val activeEndpoint: String? = null
)

@Serializable
data class TranscriptionResponse(val transcript: String = "")

@Serializable
data class SpeechDetectionResponse(val speech: Boolean = true)

@Serializable
data class SpeakResponse(@SerialName("audioUrl") val audioUrl: String = "")

@Serializable
data class Krea2Options(
    val loras: List<String> = emptyList(),
    val aspectRatios: List<String> = fallbackAspectRatios
) {
    companion object {
        val fallbackAspectRatios = listOf(
            "1:1 (Square)",
            "3:2 (Photo)",
            "4:3 (Standard)",
            "16:9 (Widescreen)",
            "21:9 (Ultrawide)",
            "2:3 (Portrait Photo)",
            "3:4 (Portrait Standard)",
            "9:16 (Portrait Widescreen)"
        )
    }
}

@Serializable
data class CalendarAIDraft(
    val title: String = "",
    val notes: String = "",
    val start: String = "",
    val end: String = ""
)

@Serializable
data class TextMessagePreparation(
    val isTextMessage: Boolean = false,
    val recipient: String = "",
    val body: String = "",
    val needsClarification: Boolean = false,
    val clarificationQuestion: String = ""
)

data class ChatEntry(
    val id: String = UUID.randomUUID().toString(),
    val role: Role,
    val text: String,
    val audioUrl: String? = null
)

enum class Role { User, Assistant, System }

data class PendingAttachment(
    val id: String = UUID.randomUUID().toString(),
    val name: String,
    val mimeType: String,
    val uri: Uri,
    val kind: AttachmentKind
)

enum class AttachmentKind { Image, Document, Audio }

enum class ConversationState(val label: String) {
    Idle("Ready"),
    Listening("Listening"),
    Transcribing("Transcribing"),
    Thinking("Thinking"),
    Speaking("Speaking"),
    Paused("Paused"),
    Failed("Failed")
}

enum class ComfyAction(val title: String, val commandPrefix: String) {
    CreateImage("New Image", "create an image of"),
    EditImage("Edit Image", "edit this image"),
    CreateVideo("Image to Video", "create a video"),
    CreateVideoWithAudio("Image + Audio Video", "create a video with audio")
}
