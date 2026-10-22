package com.maifeipin.webmusic.core.player

import android.os.SystemClock
import android.util.Log

/**
 * 播放启动的分段计时，只在 logcat 输出，不参与任何业务逻辑。
 *
 * 存在的目的：回答「点播放要等十几秒」到底慢在哪一段——是队列构建（本地路径查询 +
 * N 个 MediaItem 生成），还是首包缓冲（服务端 SMB 冷读超时）。
 * 两段在 logcat 里分属不同 tag，不做锚点就只能靠猜。
 *
 * 用法：`onClick` → `onQueueBuilt` → `onPrepared` → `onTransition` → `onFirstAudio`，
 * 任意一段缺失都不影响其余段的相对耗时报出。
 */
object PlaybackTimeline {
    private const val TAG = "PlaybackTiming"

    private var clickAt = 0L
    private var builtAt = 0L
    private var preparedAt = 0L
    private var transitionAt = 0L
    private var queueSize = 0

    fun onClick(items: Int) {
        clickAt = SystemClock.elapsedRealtime()
        builtAt = 0L
        preparedAt = 0L
        transitionAt = 0L
        queueSize = items
        Log.i(TAG, "click items=$items")
    }

    /** 本地路径查询 + 全部 MediaItem 构建完成 */
    fun onQueueBuilt() {
        if (clickAt == 0L) return
        builtAt = SystemClock.elapsedRealtime()
        Log.i(TAG, "queue-built items=$queueSize cost=${builtAt - clickAt}ms")
    }

    /** setMediaItems / prepare / play 已下发 */
    fun onPrepared() {
        if (clickAt == 0L) return
        preparedAt = SystemClock.elapsedRealtime()
        Log.i(TAG, "prepare-issued cost=${preparedAt - clickAt}ms")
    }

    /** 控制器确认已切到目标曲目 */
    fun onTransition(mediaId: String?) {
        if (clickAt == 0L) return
        transitionAt = SystemClock.elapsedRealtime()
        Log.i(TAG, "transition id=$mediaId cost=${transitionAt - clickAt}ms")
    }

    /** 真正出声（isPlaying 首次为 true） */
    fun onFirstAudio() {
        if (clickAt == 0L) return
        val now = SystemClock.elapsedRealtime()
        fun since(stage: Long): String =
            if (stage == 0L) "-" else "${stage - clickAt}ms"

        Log.i(
            TAG,
            "first-audio total=${now - clickAt}ms queue=${since(builtAt)} " +
                "issue=${since(preparedAt)} transition=${since(transitionAt)}"
        )
        clickAt = 0L
    }
}
