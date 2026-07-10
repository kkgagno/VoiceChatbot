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

        worker?.cancel()
        worker = scope.launch {
            while (true) {
                try {
                    sendBroadcast(Intent(BROADCAST_STATE).putExtra("state", "Listening"))
                    val wav = recorder.recordSegment()
                    sendBroadcast(Intent(BROADCAST_STATE).putExtra("state", "Transcribing"))
                    val response = api.chatAudio(wav)
                    val heard = response.transcript.trim()
                    if (heard.isNotBlank()) {
                        sendBroadcast(
                            Intent(BROADCAST_MESSAGE)
                                .putExtra("role", "User")
                                .putExtra("text", heard)
                        )
                        sendBroadcast(Intent(BROADCAST_STATE).putExtra("state", "Thinking"))
                        sendBroadcast(
                            Intent(BROADCAST_MESSAGE)
                                .putExtra("role", "Assistant")
                                .putExtra("text", response.response)
                                .putExtra("audioUrl", response.audioUrl)
                        )
                    }
                    delay(250)
                } catch (e: Exception) {
                    sendBroadcast(
                        Intent(BROADCAST_MESSAGE)
                            .putExtra("role", "System")
                            .putExtra("text", e.message ?: "Conversation service error")
                    )
                    delay(1500)
                }
            }
        }
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
