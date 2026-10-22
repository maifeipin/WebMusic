package com.maifeipin.webmusic.core.update

import android.content.Context
import com.maifeipin.webmusic.core.network.TokenManager
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.File

/**
 * 应用内更细的状态机：把「检查 → 下载 → 安装」收敛成一个可观察的状态流，
 * UI 只负责渲染状态与转发用户动作，不持有任何下载逻辑。
 */
class UpdateController(
    private val context: Context,
    private val tokenManager: TokenManager,
    private val scope: CoroutineScope,
    private val beforeInstall: () -> Unit = {}
) {

    sealed interface State {
        /** 无事发生（含静默检查未发现新版本）。 */
        data object Idle : State

        /** 用户手动检查中（会显示 loading）。 */
        data object Checking : State

        /** 手动检查结果：已是最新。 */
        data class UpToDate(val versionName: String) : State

        /** 发现新版本，等待用户确认下载。 */
        data class Available(val info: AppVersionDto) : State

        data class Downloading(val info: AppVersionDto, val progress: UpdateDownloadProgress) : State

        /** 下载并校验完成，等待安装；needsPermission 表示尚未授予"安装未知应用"。 */
        data class ReadyToInstall(
            val info: AppVersionDto,
            val apk: File,
            val needsPermission: Boolean
        ) : State

        data class Failed(val info: AppVersionDto?, val message: String) : State
    }

    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    private var job: Job? = null
    private var autoChecked = false
    private var installLaunched = false

    val currentVersionName: String get() = AppUpdateManager.currentVersionName

    val currentVersionCode: Int get() = AppUpdateManager.currentVersionCode

    val channel: String get() = AppUpdateManager.channel

    /** 启动时自动检查一次（每个进程只触发一次，静默失败不打扰用户）。 */
    fun autoCheckOnce() {
        if (autoChecked) return
        autoChecked = true
        check(silent = true)
    }

    fun checkManually() {
        when (_state.value) {
            // 下载中 / 待安装时不打断当前流程
            is State.Downloading, is State.ReadyToInstall -> return
            else -> Unit
        }
        job?.cancel()
        job = null
        check(silent = false)
    }

    private fun check(silent: Boolean) {
        if (job?.isActive == true) return
        if (!silent) _state.value = State.Checking

        job = scope.launch {
            try {
                val info = AppUpdateManager.checkForUpdate(
                    context = context,
                    serverUrl = tokenManager.serverUrl,
                    token = tokenManager.token
                )
                val hasNewer = info.hasUpdate &&
                    info.versionCode > AppUpdateManager.currentVersionCode

                val skippedCode = prefs.getInt(KEY_SKIPPED_CODE, 0)
                _state.value = when {
                    !hasNewer -> if (silent) State.Idle else State.UpToDate(AppUpdateManager.currentVersionName)
                    // 静默检查时尊重"跳过此版本"；强制更新与手动检查不受限制
                    silent && !info.forceUpdate && info.versionCode <= skippedCode -> State.Idle
                    else -> State.Available(info)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.value = if (silent) {
                    State.Idle
                } else {
                    State.Failed(null, e.message ?: "无法连接服务器检查更新")
                }
            }
        }
    }

    /** 开始（或续传）下载。允许从 Available / Failed 状态重试。 */
    fun download() {
        val info = when (val current = _state.value) {
            is State.Available -> current.info
            is State.Failed -> current.info
            else -> null
        } ?: return

        if (job?.isActive == true) return
        installLaunched = false
        _state.value = State.Downloading(info, UpdateDownloadProgress(0L, info.sizeBytes))

        job = scope.launch {
            try {
                val apk = AppUpdateManager.downloadApk(
                    context = context,
                    serverUrl = tokenManager.serverUrl,
                    info = info,
                    token = tokenManager.token
                ) { progress ->
                    // 回调来自 IO 线程；StateFlow 自身线程安全
                    _state.value = State.Downloading(info, progress)
                }
                _state.value = State.ReadyToInstall(
                    info = info,
                    apk = apk,
                    needsPermission = !AppUpdateManager.canInstallPackages(context)
                )
            } catch (e: CancellationException) {
                _state.value = State.Available(info)
                throw e
            } catch (e: Exception) {
                _state.value = State.Failed(info, e.message ?: "下载失败")
            }
        }
    }

    fun cancelDownload() {
        job?.cancel()
        job = null
        val info = (_state.value as? State.Downloading)?.info
        _state.value = if (info != null) State.Available(info) else State.Idle
    }

    /** 调起系统安装；未授权时先跳转授权页，授权返回后 onResumed() 会自动续接。 */
    fun install() {
        val ready = _state.value as? State.ReadyToInstall ?: return

        if (!AppUpdateManager.canInstallPackages(context)) {
            installLaunched = false
            _state.value = ready.copy(needsPermission = true)
            AppUpdateManager.openInstallPermissionSettings(context)
            return
        }

        try {
            AppUpdateManager.validateApk(context, ready.apk, ready.info)
            beforeInstall()
            installLaunched = AppUpdateManager.installApk(context, ready.apk)
        } catch (e: Exception) {
            _state.value = State.Failed(ready.info, e.message ?: "安装前校验失败")
            return
        }
        if (!installLaunched) {
            _state.value = State.Failed(ready.info, "无法调起安装界面，请检查系统是否拦截了未知来源安装")
        }
    }

    /** Activity onResume 时调用：若刚从"安装未知应用"授权页返回，自动继续安装。 */
    fun onResumed() {
        val ready = _state.value as? State.ReadyToInstall ?: return
        if (installLaunched) return
        if (!ready.needsPermission) return
        if (!AppUpdateManager.canInstallPackages(context)) return
        // 已授权：直接续接安装，用户无感知
        install()
    }

    fun dismiss() {
        val current = _state.value
        // 强制更新不允许关闭
        if (current is State.Available && current.info.forceUpdate) return
        if (current is State.Downloading) return
        job?.cancel()
        job = null
        _state.value = State.Idle
    }

    fun skipThisVersion() {
        val info = (_state.value as? State.Available)?.info ?: return
        if (info.forceUpdate) return
        prefs.edit().putInt(KEY_SKIPPED_CODE, info.versionCode).apply()
        _state.value = State.Idle
    }

    fun resetUi() {
        job?.cancel()
        job = null
        _state.value = State.Idle
    }

    private companion object {
        const val PREFS_NAME = "webmusic_update_prefs"
        const val KEY_SKIPPED_CODE = "skipped_version_code"
    }
}
