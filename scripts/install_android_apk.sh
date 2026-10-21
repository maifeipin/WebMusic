#!/usr/bin/env bash
# WebMusic Android 物理机一键安装与调试日志辅助脚本
set -e

APK_PATH="${1:-android/app/build/outputs/apk/debug/app-debug.apk}"

if [ ! -f "$APK_PATH" ]; then
    echo "⚠️ 未在默认路径找到 APK: $APK_PATH"
    echo "提示: 可以把从 GitHub Actions 下载的 app-debug.apk 路径作为参数传入:"
    echo "  ./scripts/install_android_apk.sh /path/to/app-debug.apk"
    exit 1
fi

echo "📱 检查已连接的 Android 设备..."
adb devices

echo "🚀 正在安装 APK 到设备: $APK_PATH ..."
adb install -r "$APK_PATH"

echo "✅ 安装成功！已就绪。"
echo "💡 提示：运行以下命令实时查看流媒体与播放器日志 (0 内存开销):"
echo "adb logcat -v time | grep -E 'WebMusic|ExoPlayer|MediaSession'"
