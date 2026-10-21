package com.maifeipin.webmusic.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioManager
import android.util.Log

class AudioNoisyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == AudioManager.ACTION_AUDIO_BECOMING_NOISY) {
            Log.d("WebMusic", "检测到耳机断开/拔出，触发强制暂停防外放")
        }
    }
}
