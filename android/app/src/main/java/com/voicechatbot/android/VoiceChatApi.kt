package com.voicechatbot.android

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

class VoiceChatApi(
    private val context: Context,
    private val profile: ServerProfile,
    private val baseUrl: String
) {
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }
    private val client = unsafeClient()

    suspend fun status(): ServerStatus = get("/api/status")

    suspend fun transcribe(wav: File): String {
        val body = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart("audio", "android.wav", wav.asRequestBody("audio/wav".toMediaType()))
            .build()
        return postMultipart<TranscriptionResponse>("/api/transcribe", body).transcript.trim()
    }

    suspend fun detectSpeech(wav: File): Boolean {
        val body = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart("audio", "android.wav", wav.asRequestBody("audio/wav".toMediaType()))
            .build()
        return postMultipart<SpeechDetectionResponse>("/api/vad", body).speech
    }

    suspend fun respond(text: String, keepDocumentsActive: Boolean): AssistantResponse {
        return postJson(
            "/api/respond",
            mapOf("text" to text, "keepDocumentsActive" to keepDocumentsActive)
        )
    }

    suspend fun message(
        text: String,
        attachments: List<PendingAttachment>,
        keepDocumentsActive: Boolean
    ): AssistantResponse = withContext(Dispatchers.IO) {
        val builder = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart("text", text)
            .addFormDataPart("keepDocumentsActive", keepDocumentsActive.toString())

        attachments.forEach { attachment ->
            val file = copyUriToCache(attachment.uri, attachment.name)
            builder.addFormDataPart(
                "files",
                attachment.name,
                file.asRequestBody(attachment.mimeType.toMediaType())
            )
        }
        postMultipart("/api/message", builder.build())
    }

    suspend fun tool(prompt: String): String {
        val result: ToolResponse = postJson("/api/tool", mapOf("prompt" to prompt))
        return result.response
    }

    suspend fun groundedAnswer(prompt: String): String {
        val result: GroundedAnswerResponse = postJson("/api/grounded-answer", mapOf("prompt" to prompt))
        return result.answer
    }

    suspend fun speak(text: String): String {
        val result: SpeakResponse = postJson("/api/speak", mapOf("text" to text))
        return result.audioUrl
    }

    suspend fun prepareTextMessage(text: String): TextMessagePreparation {
        return postJson("/api/text-message", mapOf("text" to text))
    }

    suspend fun prepareCalendarEvent(
        text: String,
        currentDateTime: String,
        timeZone: String
    ): CalendarAIDraft {
        return postJson(
            "/api/calendar-draft",
            mapOf("text" to text, "currentDateTime" to currentDateTime, "timeZone" to timeZone)
        )
    }

    suspend fun krea2Options(): Krea2Options {
        val obj: JsonObject = get("/api/krea2/options")
        fun strings(name: String, fallback: String): List<String> {
            val element = obj[name] ?: obj[fallback] ?: return emptyList()
            return runCatching { element.jsonArray.map { it.jsonPrimitive.content } }.getOrDefault(emptyList())
        }
        return Krea2Options(
            loras = strings("loras", "Loras"),
            aspectRatios = strings("aspectRatios", "AspectRatios").ifEmpty { Krea2Options.fallbackAspectRatios }
        )
    }

    suspend fun createKrea2Image(
        prompt: String,
        enableLora: Boolean,
        loraName: String,
        aspectRatio: String
    ): AssistantResponse {
        return postJson(
            "/api/krea2/create",
            mapOf(
                "prompt" to prompt,
                "enableLora" to enableLora,
                "loraName" to loraName,
                "aspectRatio" to aspectRatio
            )
        )
    }

    suspend fun mediaFile(relativeUrl: String, filename: String): File = withContext(Dispatchers.IO) {
        val request = requestBuilder(relativeUrl).get().build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) error("HTTP ${response.code}: ${response.body?.string().orEmpty()}")
            val out = File(context.cacheDir, filename)
            response.body?.byteStream()?.use { input ->
                out.outputStream().use { output -> input.copyTo(output) }
            }
            out
        }
    }

    private suspend inline fun <reified T> get(path: String): T = withContext(Dispatchers.IO) {
        val request = requestBuilder(path).get().build()
        client.newCall(request).execute().use { response ->
            decode(response.code, response.body?.string().orEmpty())
        }
    }

    private suspend inline fun <reified T> postJson(path: String, payload: Map<String, Any>): T {
        val body = jsonObject(payload).toString().toRequestBody("application/json".toMediaType())
        return withContext(Dispatchers.IO) {
            val request = requestBuilder(path).post(body).build()
            client.newCall(request).execute().use { response ->
                decode(response.code, response.body?.string().orEmpty())
            }
        }
    }

    private fun jsonObject(payload: Map<String, Any>): JsonElement = buildJsonObject {
        payload.forEach { (key, value) ->
            put(
                key,
                when (value) {
                    is Boolean -> JsonPrimitive(value)
                    is Number -> JsonPrimitive(value)
                    else -> JsonPrimitive(value.toString())
                }
            )
        }
    }

    private suspend inline fun <reified T> postMultipart(path: String, body: MultipartBody): T =
        withContext(Dispatchers.IO) {
            val request = requestBuilder(path).post(body).build()
            client.newCall(request).execute().use { response ->
                decode(response.code, response.body?.string().orEmpty())
            }
        }

    private inline fun <reified T> decode(code: Int, text: String): T {
        if (code !in 200..299) error("HTTP $code: $text")
        return json.decodeFromString(text)
    }

    private fun requestBuilder(pathOrUrl: String): Request.Builder {
        val url = if (pathOrUrl.startsWith("http")) {
            pathOrUrl
        } else {
            baseUrl.trimEnd('/') + "/" + pathOrUrl.trimStart('/')
        }
        return Request.Builder().url(url).apply {
            if (profile.pin.isNotBlank()) header("X-Phone-Remote-Pin", profile.pin)
        }
    }

    private fun copyUriToCache(uri: Uri, name: String): File {
        val safe = name.replace(Regex("[^A-Za-z0-9._-]"), "_")
        val out = File(context.cacheDir, safe)
        context.contentResolver.openInputStream(uri).use { input ->
            requireNotNull(input) { "Could not open $name" }
            out.outputStream().use { output -> input.copyTo(output) }
        }
        return out
    }

    private fun unsafeClient(): OkHttpClient {
        val trustManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val sslContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf(trustManager), SecureRandom())
        }
        return OkHttpClient.Builder()
            .sslSocketFactory(sslContext.socketFactory, trustManager)
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(5, TimeUnit.MINUTES)
            .writeTimeout(5, TimeUnit.MINUTES)
            .build()
    }

    @kotlinx.serialization.Serializable
    private data class ToolResponse(val response: String = "")

    @kotlinx.serialization.Serializable
    private data class GroundedAnswerResponse(val answer: String = "")
}
