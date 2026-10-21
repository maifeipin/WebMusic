package com.maifeipin.webmusic.core.audio

import android.content.Context
import android.content.SharedPreferences
import android.media.audiofx.Equalizer
import android.util.Log

data class EqualizerBand(
    val bandIndex: Short,
    val centerFreqHz: Int,
    val levelMb: Short
)

data class EqualizerState(
    val isEnabled: Boolean,
    val minBandLevelMb: Short,
    val maxBandLevelMb: Short,
    val bands: List<EqualizerBand>,
    val presets: List<String>,
    val currentPreset: Short
)

object EqualizerManager {
    private const val TAG = "EqualizerManager"
    private const val PREFS_NAME = "webmusic_equalizer_prefs"
    private const val KEY_ENABLED = "eq_enabled"
    private const val KEY_PRESET = "eq_preset"

    private var equalizer: Equalizer? = null
    private var currentSessionId: Int = 0

    @Synchronized
    fun attachAudioSession(context: Context, audioSessionId: Int) {
        if (audioSessionId == 0) return
        if (currentSessionId == audioSessionId && equalizer != null) return

        try {
            release()
            currentSessionId = audioSessionId
            val eq = Equalizer(0, audioSessionId)
            equalizer = eq

            val prefs = getPrefs(context)
            val isEnabled = prefs.getBoolean(KEY_ENABLED, false)
            eq.enabled = isEnabled

            val savedPreset = prefs.getInt(KEY_PRESET, -1).toShort()
            if (savedPreset >= 0 && savedPreset < eq.numberOfPresets) {
                eq.usePreset(savedPreset)
            } else {
                // 恢复自定义各频段增益
                for (b in 0 until eq.numberOfBands.toInt()) {
                    val band = b.toShort()
                    val savedLevel = prefs.getInt("band_$band", 0).toShort()
                    eq.setBandLevel(band, savedLevel)
                }
            }
            Log.i(TAG, "Attached Equalizer to audioSessionId: $audioSessionId, enabled: $isEnabled")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to attach Equalizer to audioSessionId $audioSessionId", e)
        }
    }

    @Synchronized
    fun release() {
        try {
            equalizer?.release()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to release equalizer", e)
        } finally {
            equalizer = null
            currentSessionId = 0
        }
    }

    @Synchronized
    fun setEnabled(context: Context, enabled: Boolean) {
        try {
            equalizer?.enabled = enabled
            getPrefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to setEnabled $enabled", e)
        }
    }

    @Synchronized
    fun setBandLevel(context: Context, band: Short, levelMb: Short) {
        try {
            equalizer?.setBandLevel(band, levelMb)
            getPrefs(context).edit()
                .putInt("band_$band", levelMb.toInt())
                .putInt(KEY_PRESET, -1) // 标记为自定义
                .apply()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to setBandLevel band $band to $levelMb", e)
        }
    }

    @Synchronized
    fun usePreset(context: Context, preset: Short) {
        try {
            val eq = equalizer ?: return
            if (preset in 0 until eq.numberOfPresets) {
                eq.usePreset(preset)
                val editor = getPrefs(context).edit().putInt(KEY_PRESET, preset.toInt())
                for (b in 0 until eq.numberOfBands.toInt()) {
                    val band = b.toShort()
                    editor.putInt("band_$band", eq.getBandLevel(band).toInt())
                }
                editor.apply()
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to usePreset $preset", e)
        }
    }

    @Synchronized
    fun getState(context: Context): EqualizerState? {
        val eq = equalizer ?: return null
        return try {
            val isEnabled = eq.enabled
            val minLevel = eq.bandLevelRange[0]
            val maxLevel = eq.bandLevelRange[1]
            val numBands = eq.numberOfBands.toInt()
            val bands = (0 until numBands).map { b ->
                val band = b.toShort()
                val centerHz = eq.getCenterFreq(band) / 1000 // 毫赫兹转赫兹
                val level = eq.getBandLevel(band)
                EqualizerBand(band, centerHz, level)
            }
            val numPresets = eq.numberOfPresets.toInt()
            val presets = (0 until numPresets).map { p ->
                eq.getPresetName(p.toShort()) ?: "预设 $p"
            }
            val savedPreset = getPrefs(context).getInt(KEY_PRESET, -1).toShort()
            EqualizerState(
                isEnabled = isEnabled,
                minBandLevelMb = minLevel,
                maxBandLevelMb = maxLevel,
                bands = bands,
                presets = presets,
                currentPreset = savedPreset
            )
        } catch (e: Exception) {
            Log.e(TAG, "Failed to read EqualizerState", e)
            null
        }
    }

    private fun getPrefs(context: Context): SharedPreferences {
        return context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }
}
