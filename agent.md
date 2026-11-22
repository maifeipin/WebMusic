# Agent 工作规范

## Android 开发与调试环境约定

1. **禁止在本机安装模拟器和调试环境**：
   - 不要在本机安装 Android Studio、Android SDK、Android 模拟器（AVD）或额外的本地 Android 调试/构建环境。
2. **真机调试**：
   - 需要真机运行、抓包、查看 `logcat` 或安装测试时，由用户提供真机 ADB 连接，不自行搭建本地模拟器。
   - 本机已有 `adb.exe` 路径：`C:\Projects\WebMusic\.Archive\APP\tools\platform-tools\adb.exe`。
   - 不要修改或扩大手机 `logcat` 内存缓冲区（如 `adb logcat -G`）。
3. **Android 构建与验证**：
   - Android APK 的编译与打包通过 GitHub Actions CI（`.github/workflows/android-build.yml`）完成。
