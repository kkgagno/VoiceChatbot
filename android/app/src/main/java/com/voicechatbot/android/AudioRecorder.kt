package com.voicechatbot.android

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import androidx.core.content.ContextCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import kotlin.coroutines.coroutineContext
import kotlin.math.abs
import kotlin.math.sqrt

class AudioRecorder(private val context: Context) {
    private val sampleRate = 16_000
    private val channel = AudioFormat.CHANNEL_IN_MONO
    private val encoding = AudioFormat.ENCODING_PCM_16BIT

    suspend fun recordSegment(
        maxMillis: Long = 300_000,
        silenceMillis: Long = 2_400,
        preRollMillis: Long = 1_000,
        containsSpeech: (suspend (File) -> Boolean)? = null
    ): File = withContext(Dispatchers.IO) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED
        ) error("Microphone permission is required.")

        val minBuffer = AudioRecord.getMinBufferSize(sampleRate, channel, encoding)
        val frame = maxOf(minBuffer, sampleRate / 4)
        val recorder = AudioRecord(
            MediaRecorder.AudioSource.VOICE_RECOGNITION,
            sampleRate,
            channel,
            encoding,
            frame
        )

        val pcm = ByteArrayOutputStream()
        val preRoll = ArrayDeque<ByteArray>()
        val maxPreRollBytes = (sampleRate * 2 * preRollMillis / 1000).toInt()
        var preRollBytes = 0
        var speechStarted = false
        var speechConfirmed = containsSpeech == null
        var candidatePending = false
        var lastVadCheckAt = System.currentTimeMillis()
        var vadNonSpeechMillis = 0L
        var noiseFloor = 0.004
        val start = System.currentTimeMillis()
        val buffer = ByteArray(frame)

        recorder.startRecording()
        try {
            while (coroutineContext.isActive && System.currentTimeMillis() - start < maxMillis) {
                val read = recorder.read(buffer, 0, buffer.size)
                if (read <= 0) continue
                val chunk = buffer.copyOf(read)
                val rms = chunk.normalizedRms()
                val startThreshold = maxOf(0.009, noiseFloor * 2.4)
                if (!speechStarted) {
                    preRoll.addLast(chunk)
                    preRollBytes += chunk.size
                    while (preRollBytes > maxPreRollBytes && preRoll.isNotEmpty()) {
                        preRollBytes -= preRoll.removeFirst().size
                    }
                    if (rms < startThreshold) {
                        noiseFloor = noiseFloor * 0.97 + rms * 0.03
                    } else {
                        speechStarted = true
                        speechConfirmed = containsSpeech == null
                        candidatePending = false
                        lastVadCheckAt = System.currentTimeMillis()
                        vadNonSpeechMillis = 0
                        preRoll.forEach { pcm.write(it) }
                        preRoll.clear()
                    }
                }
                if (speechStarted) {
                    pcm.write(chunk)
                    val recordedMillis = pcm.size().toLong() * 1000L / (sampleRate * 2L)
                    val detector = containsSpeech
                    if (detector != null && !candidatePending) {
                        if (!speechConfirmed && recordedMillis >= 750) {
                            candidatePending = true
                            val candidate = writeCandidateWav(pcm.toByteArray())
                            val ok = runCatching { detector(candidate) }.getOrDefault(false)
                            candidate.delete()
                            candidatePending = false
                            if (ok) {
                                speechConfirmed = true
                                lastVadCheckAt = System.currentTimeMillis()
                                vadNonSpeechMillis = 0
                            } else {
                                pcm.reset()
                                preRoll.clear()
                                preRollBytes = 0
                                speechStarted = false
                                speechConfirmed = false
                                noiseFloor = 0.004
                                continue
                            }
                        } else if (speechConfirmed && System.currentTimeMillis() - lastVadCheckAt >= 600) {
                            candidatePending = true
                            val recent = pcm.toByteArray().takeLast(sampleRate * 2).toByteArray()
                            val candidate = writeCandidateWav(recent)
                            val ok = runCatching { detector(candidate) }.getOrDefault(true)
                            candidate.delete()
                            candidatePending = false
                            lastVadCheckAt = System.currentTimeMillis()
                            if (ok) {
                                vadNonSpeechMillis = 0
                            } else {
                                vadNonSpeechMillis += 600
                            }
                        }
                    } else if (containsSpeech == null) {
                        if (rms >= startThreshold) vadNonSpeechMillis = 0 else vadNonSpeechMillis += 250
                    }

                    if (speechConfirmed &&
                        recordedMillis >= 350 &&
                        vadNonSpeechMillis >= silenceMillis
                    ) {
                        break
                    }
                }
            }
        } finally {
            runCatching { recorder.stop() }
            recorder.release()
        }

        val output = File(context.cacheDir, "voicechat-${System.currentTimeMillis()}.wav")
        output.writeWav(pcm.toByteArray(), sampleRate)
        output
    }

    private fun writeCandidateWav(pcm: ByteArray): File {
        val output = File(context.cacheDir, "voicechat-vad-${System.nanoTime()}.wav")
        output.writeWav(pcm, sampleRate)
        return output
    }

    private fun ByteArray.rms(): Int {
        var sum = 0L
        var count = 0
        var i = 0
        while (i + 1 < size) {
            val sample = ((this[i + 1].toInt() shl 8) or (this[i].toInt() and 0xff)).toShort().toInt()
            sum += abs(sample)
            count++
            i += 2
        }
        return if (count == 0) 0 else (sum / count).toInt()
    }

    private fun ByteArray.normalizedRms(): Double {
        var sum = 0.0
        var count = 0
        var i = 0
        while (i + 1 < size) {
            val sample = ((this[i + 1].toInt() shl 8) or (this[i].toInt() and 0xff)).toShort().toInt()
            val normalized = sample / 32768.0
            sum += normalized * normalized
            count++
            i += 2
        }
        return if (count == 0) 0.0 else sqrt(sum / count)
    }

    private fun File.writeWav(pcm: ByteArray, sampleRate: Int) {
        outputStream().use { out ->
            val byteRate = sampleRate * 2
            val dataSize = pcm.size
            val totalSize = 36 + dataSize
            out.write("RIFF".toByteArray())
            out.writeLE32(totalSize)
            out.write("WAVEfmt ".toByteArray())
            out.writeLE32(16)
            out.writeLE16(1)
            out.writeLE16(1)
            out.writeLE32(sampleRate)
            out.writeLE32(byteRate)
            out.writeLE16(2)
            out.writeLE16(16)
            out.write("data".toByteArray())
            out.writeLE32(dataSize)
            out.write(pcm)
        }
    }

    private fun java.io.OutputStream.writeLE16(value: Int) {
        write(byteArrayOf((value and 0xff).toByte(), ((value shr 8) and 0xff).toByte()))
    }

    private fun java.io.OutputStream.writeLE32(value: Int) {
        write(
            byteArrayOf(
                (value and 0xff).toByte(),
                ((value shr 8) and 0xff).toByte(),
                ((value shr 16) and 0xff).toByte(),
                ((value shr 24) and 0xff).toByte()
            )
        )
    }
}
