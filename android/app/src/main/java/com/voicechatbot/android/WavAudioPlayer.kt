package com.voicechatbot.android

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaPlayer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

class WavAudioPlayer {
    private var track: AudioTrack? = null
    private var mediaPlayer: MediaPlayer? = null

    suspend fun play(file: File, onDone: () -> Unit) = withContext(Dispatchers.IO) {
        stop()
        val wav = runCatching { WavData.read(file) }.getOrElse { wavError ->
            playWithMediaPlayer(file, onDone, wavError)
            return@withContext
        }
        val channelConfig = if (wav.channels == 1) {
            AudioFormat.CHANNEL_OUT_MONO
        } else {
            AudioFormat.CHANNEL_OUT_STEREO
        }
        val encoding = when (wav.bitsPerSample) {
            8 -> AudioFormat.ENCODING_PCM_8BIT
            16 -> AudioFormat.ENCODING_PCM_16BIT
            32 -> AudioFormat.ENCODING_PCM_FLOAT
            else -> error("Unsupported WAV bit depth: ${wav.bitsPerSample}")
        }
        val minBuffer = AudioTrack.getMinBufferSize(wav.sampleRate, channelConfig, encoding)
        val player = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(wav.sampleRate)
                    .setChannelMask(channelConfig)
                    .setEncoding(encoding)
                    .build()
            )
            .setBufferSizeInBytes(maxOf(minBuffer, wav.pcm.size))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        track = player
        try {
            player.play()
            var offset = 0
            while (offset < wav.pcm.size && track === player) {
                val written = player.write(wav.pcm, offset, wav.pcm.size - offset)
                if (written <= 0) break
                offset += written
            }
            player.stop()
        } finally {
            if (track === player) track = null
            player.release()
            onDone()
        }
    }

    fun stop() {
        val mp = mediaPlayer
        mediaPlayer = null
        runCatching { mp?.stop() }
        runCatching { mp?.release() }

        val current = track ?: return
        track = null
        runCatching { current.pause() }
        runCatching { current.flush() }
        runCatching { current.stop() }
        runCatching { current.release() }
    }

    private fun playWithMediaPlayer(file: File, onDone: () -> Unit, wavError: Throwable) {
        val player = MediaPlayer()
        mediaPlayer = player
        try {
            player.setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            player.setDataSource(file.absolutePath)
            player.prepare()
            player.start()
            while (mediaPlayer === player && player.isPlaying) {
                Thread.sleep(100)
            }
        } catch (mediaError: Throwable) {
            throw IllegalStateException(
                "Audio playback failed. The PC returned an audio file Android could not play. " +
                    "WAV: ${wavError.message}. Fallback: ${mediaError.message}. ${describeFile(file)}",
                mediaError
            )
        } finally {
            if (mediaPlayer === player) mediaPlayer = null
            runCatching { player.release() }
            onDone()
        }
    }

    private fun describeFile(file: File): String {
        val bytes = runCatching { file.readBytes() }.getOrDefault(ByteArray(0))
        val header = bytes.take(8).joinToString(" ") { "%02X".format(it) }
        val ascii = bytes.take(16).map {
            val value = it.toInt() and 0xff
            if (value in 32..126) value.toChar() else '.'
        }.joinToString("")
        return "Downloaded ${file.length()} bytes, header [$header], ascii [$ascii]."
    }

    private data class WavData(
        val sampleRate: Int,
        val channels: Int,
        val bitsPerSample: Int,
        val pcm: ByteArray
    ) {
        companion object {
            fun read(file: File): WavData {
                val bytes = file.readBytes()
                fun ascii(offset: Int, length: Int) = bytes.decodeToString(offset, offset + length)
                fun u16(offset: Int) = ByteBuffer.wrap(bytes, offset, 2).order(ByteOrder.LITTLE_ENDIAN).short.toInt() and 0xffff
                fun i32(offset: Int) = ByteBuffer.wrap(bytes, offset, 4).order(ByteOrder.LITTLE_ENDIAN).int

                require(bytes.size >= 44 && ascii(0, 4) == "RIFF" && ascii(8, 4) == "WAVE") {
                    "Not a WAV file."
                }

                var offset = 12
                var channels = 1
                var sampleRate = 16_000
                var bits = 16
                var dataStart = -1
                var dataLength = 0

                while (offset + 8 <= bytes.size) {
                    val id = ascii(offset, 4)
                    val size = i32(offset + 4)
                    val body = offset + 8
                    if (size < 0 || body + size > bytes.size) {
                        break
                    }
                    when (id) {
                        "fmt " -> {
                            channels = u16(body + 2)
                            sampleRate = i32(body + 4)
                            bits = u16(body + 14)
                        }
                        "data" -> {
                            dataStart = body
                            dataLength = size
                            break
                        }
                    }
                    offset = body + size + (size and 1)
                }

                if (dataStart < 0) {
                    val marker = "data".toByteArray()
                    var i = 12
                    while (i + 8 <= bytes.size) {
                        if (bytes[i] == marker[0] &&
                            bytes[i + 1] == marker[1] &&
                            bytes[i + 2] == marker[2] &&
                            bytes[i + 3] == marker[3]
                        ) {
                            val size = i32(i + 4)
                            if (size > 0 && i + 8 + size <= bytes.size) {
                                dataStart = i + 8
                                dataLength = size
                                break
                            }
                        }
                        i++
                    }
                }

                require(dataStart >= 0 && dataLength > 0) { "WAV data chunk missing." }
                return WavData(sampleRate, channels, bits, bytes.copyOfRange(dataStart, dataStart + dataLength))
            }
        }
    }
}
