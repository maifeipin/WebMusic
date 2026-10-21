package com.maifeipin.webmusic.core.player

import com.maifeipin.webmusic.core.network.MediaFileDto

enum class PlaybackMode {
    SEQUENCE, // 顺序/列表循环
    REPEAT_ONE, // 单曲循环
    SHUFFLE // 随机播放
}

class PlaybackQueueManager {
    private val queue = mutableListOf<MediaFileDto>()
    var currentIndex: Int = -1
        private set

    var playbackMode: PlaybackMode = PlaybackMode.SEQUENCE

    fun setQueue(newQueue: List<MediaFileDto>, startIndex: Int = 0) {
        queue.clear()
        queue.addAll(newQueue)
        currentIndex = if (queue.isNotEmpty()) startIndex.coerceIn(0, queue.size - 1) else -1
    }

    fun getCurrentItem(): MediaFileDto? {
        return if (currentIndex in queue.indices) queue[currentIndex] else null
    }

    fun getQueue(): List<MediaFileDto> = queue.toList()

    fun next(): MediaFileDto? {
        if (queue.isEmpty()) return null
        when (playbackMode) {
            PlaybackMode.REPEAT_ONE -> {
                // 仍旧返回当前项
            }
            PlaybackMode.SHUFFLE -> {
                currentIndex = (0 until queue.size).random()
            }
            PlaybackMode.SEQUENCE -> {
                currentIndex = (currentIndex + 1) % queue.size
            }
        }
        return getCurrentItem()
    }

    fun previous(): MediaFileDto? {
        if (queue.isEmpty()) return null
        when (playbackMode) {
            PlaybackMode.REPEAT_ONE -> {
                // 仍旧返回当前项
            }
            PlaybackMode.SHUFFLE -> {
                currentIndex = (0 until queue.size).random()
            }
            PlaybackMode.SEQUENCE -> {
                currentIndex = if (currentIndex - 1 < 0) queue.size - 1 else currentIndex - 1
            }
        }
        return getCurrentItem()
    }

    fun toggleMode(): PlaybackMode {
        playbackMode = when (playbackMode) {
            PlaybackMode.SEQUENCE -> PlaybackMode.REPEAT_ONE
            PlaybackMode.REPEAT_ONE -> PlaybackMode.SHUFFLE
            PlaybackMode.SHUFFLE -> PlaybackMode.SEQUENCE
        }
        return playbackMode
    }
}
