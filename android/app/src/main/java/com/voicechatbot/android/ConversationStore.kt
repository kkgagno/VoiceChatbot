package com.voicechatbot.android

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

object ConversationStore {
    private const val PREFS = "voicechatbot_conversation"
    private const val KEY_MESSAGES = "messages"
    private const val MAX_MESSAGES = 100

    fun append(context: Context, role: Role, text: String, audioUrl: String? = null) {
        if (text.isBlank() && audioUrl.isNullOrBlank()) return
        append(context, ChatEntry(role = role, text = text, audioUrl = audioUrl))
    }

    fun append(context: Context, entry: ChatEntry) {
        if (entry.text.isBlank() && entry.audioUrl.isNullOrBlank()) return
        val messages = load(context).toMutableList()
        messages += entry
        save(context, messages.takeLast(MAX_MESSAGES))
    }

    fun load(context: Context): List<ChatEntry> {
        val raw = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_MESSAGES, "[]") ?: "[]"
        return runCatching {
            val array = JSONArray(raw)
            buildList {
                for (index in 0 until array.length()) {
                    val obj = array.optJSONObject(index) ?: continue
                    val role = runCatching {
                        Role.valueOf(obj.optString("role", Role.System.name))
                    }.getOrDefault(Role.System)
                    add(
                        ChatEntry(
                            id = obj.optString("id").ifBlank { java.util.UUID.randomUUID().toString() },
                            role = role,
                            text = obj.optString("text"),
                            audioUrl = obj.optString("audioUrl").ifBlank { null }
                        )
                    )
                }
            }
        }.getOrDefault(emptyList())
    }

    private fun save(context: Context, messages: List<ChatEntry>) {
        val array = JSONArray()
        messages.forEach { message ->
            array.put(
                JSONObject()
                    .put("id", message.id)
                    .put("role", message.role.name)
                    .put("text", message.text)
                    .put("audioUrl", message.audioUrl ?: "")
            )
        }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_MESSAGES, array.toString())
            .apply()
    }
}
