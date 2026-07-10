package com.voicechatbot.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class ConversationService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var worker: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> stopSelf()
            ACTION_START -> startConversation(intent)
        }
        return START_STICKY
    }

    override fun onDestroy() {
        worker?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    private fun startConversation(intent: Intent) {
        createChannel()
        startForeground(101, notification("Listening for Voice Chatbot"))
        val profile = ServerProfile(
            localUrl = intent.getStringExtra(EXTRA_LOCAL_URL).orEmpty(),
            vpnUrl = intent.getStringExtra(EXTRA_VPN_URL).orEmpty(),
            pin = intent.getStringExtra(EXTRA_PIN).orEmpty()
        )
        val baseUrl = intent.getStringExtra(EXTRA_BASE_URL).orEmpty()
        val api = VoiceChatApi(applicationContext, profile, baseUrl)
        val recorder = AudioRecorder(applicationContext)
        val audioPlayer = WavAudioPlayer()

        worker?.cancel()
        worker = scope.launch {
            while (true) {
                try {
                    broadcastState("Listening")
                    val wav = recorder.recordSegment()
                    broadcastState("Transcribing")
                    val heard = api.transcribe(wav)
                    if (heard.isNotBlank()) {
                        storeAndBroadcast(Role.User, heard)
                        broadcastState("Thinking")
                        val response = api.respond(heard, false)
                        storeAndBroadcast(Role.Assistant, response.response, response.audioUrl)
                        if (!response.audioUrl.isNullOrBlank()) {
                            val audio = api.mediaFile(response.audioUrl, "voicechat-service-${System.currentTimeMillis()}.wav")
                            audioPlayer.play(audio) {}
                        }
                    }
                    delay(250)
                } catch (e: Exception) {
                    storeAndBroadcast(Role.System, e.message ?: "Conversation service error")
                    delay(1500)
                }
            }
        }
    }

    private fun storeAndBroadcast(role: Role, text: String, audioUrl: String? = null) {
        ConversationStore.append(applicationContext, role, text, audioUrl)
        sendBroadcast(
            Intent(BROADCAST_MESSAGE)
                .setPackage(packageName)
                .putExtra("role", role.name)
                .putExtra("text", text)
                .putExtra("audioUrl", audioUrl)
        )
    }

    private fun broadcastState(state: String) {
        sendBroadcast(
            Intent(BROADCAST_STATE)
                .setPackage(packageName)
                .putExtra("state", state)
        )
    }

    private fun createChannel() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Voice Chatbot", NotificationManager.IMPORTANCE_LOW)
        )
    }

    private fun notification(text: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            1,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val stopIntent = PendingIntent.getService(
            this,
            2,
            Intent(this, ConversationService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("Voice Chatbot")
            .setContentText(text)
            .setContentIntent(openIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Stop", stopIntent)
            .setOngoing(true)
            .build()
    }

    companion object {
        const val ACTION_START = "com.voicechatbot.android.START"
        const val ACTION_STOP = "com.voicechatbot.android.STOP"
        const val BROADCAST_MESSAGE = "com.voicechatbot.android.MESSAGE"
        const val BROADCAST_STATE = "com.voicechatbot.android.STATE"
        const val EXTRA_LOCAL_URL = "local"
        const val EXTRA_VPN_URL = "vpn"
        const val EXTRA_PIN = "pin"
        const val EXTRA_BASE_URL = "base"
        private const val CHANNEL_ID = "voicechatbot-conversation"
    }
}
