# WebMusic Native App (Capacitor) 实施手册

本文档为 AI 助手 (Cursor, Copilot, etc.) 提供详细的实施步骤。请**严格**按照此步骤操作。

---

## 🛠️ 前置条件 (Prerequisites)
**必须在具备以下环境的机器上运行：**
*   **iOS**: macOS, Xcode 14+, CocoaPods
*   **Android**: Android Studio, JDK 17+
*   **通用**: Node.js 18+, NPM

---

## 🚀 这里的 AI Prompt (复制给 AI)

如果你是 AI 助手，请读取以下指令开始工作：

> **Role**: 你是一个熟练的 Hybrid App 开发工程师，精通 Capacitor 和 React。
> **Task**: 将当前的 React Web 项目包装为 iOS/Android 原生应用，并实现后台播放保活。
> **Constraint**: 尽量不要手写原生代码 (Swift/Java)，优先使用 Capacitor 官方插件或社区成熟插件。
> **Context**: 项目位于 `v2/frontend`，构建工具是 Vite。

---

## 📝 详细实施步骤 (Step-by-Step)

### Phase 1: 初始化 Capacitor

1.  **进入前端目录**
    ```bash
    cd v2/frontend
    ```

2.  **安装核心依赖**
    ```bash
    npm install @capacitor/core @capacitor/cli @capacitor/ios @capacitor/android
    ```

3.  **初始化配置**
    ```bash
    npx cap init WebMusic com.maifeipin.music --web-dir dist
    ```
    *   `WebMusic`: App 名称
    *   `com.maifeipin.music`: Bundle ID (这个很重要，iOS 也是用这个)
    *   `dist`: Vite build 的输出目录

4.  **第一次构建**
    ```bash
    npm run build
    npx cap add ios
    npx cap add android
    ```
    *   *SOP*: 如果报错 `CocoaPods not installed`，提示用户安装 `sudo gem install cocoapods`。

### Phase 2: 配置后台播放 (关键流程)

这是音乐 App 最重要的一步。

#### 1. 安装后台插件
目前推荐 `capacitor-plugin-background-mode` 或配置原生 Background Modes。
建议先只配置原生 Capability，不引入额外插件，看 WebView 是否能保持。

#### 2. 修改 iOS 配置 (Info.plist)
路径: `ios/App/App/Info.plist`
**操作**: 必须添加 `UIBackgroundModes`。

```xml
<key>UIBackgroundModes</key>
<array>
    <string>audio</string>
    <string>fetch</string>
    <string>processing</string>
</array>
```

#### 3. 修改 Android 配置 (AndroidManifest.xml)
路径: `android/app/src/main/AndroidManifest.xml`
**操作**: 添加权限。

```xml
<uses-permission android:name="android.permission.WAKE_LOCK" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
```

### Phase 3: 解决各种坑 (Troubleshooting)

#### Q1: 图片加载失败 (404/401)
*   **现象**: SMB 图片在 App 里加载不出来。
*   **原因**: App 运行在 `capacitor://localhost` (iOS) 或 `http://localhost` (Android)，而你的 API 是 `http://192.168.x.x`。Cookie/Token 因为跨域 (SameSite) 无法发送。
*   **解决方案**:
    *   安装 `@capacitor-community/http` 插件来发请求（绕过 Cors）。
    *   或者修改后端 CORS 配置，允许 `capacitor://localhost`。

#### Q2: 底部安全区遮挡
*   **现象**: 你的 `MobileTabBar` 被 iPhone 的黑条遮住了。
*   **解决方案**: 确保你的 CSS 里用了 `env(safe-area-inset-bottom)`。
    ```css
    padding-bottom: env(safe-area-inset-bottom);
    ```
    (提示: 我们的 `MobileTabBar.tsx` 已经加了，但要在真机上验证)。

---

## ✅ 验证清单 (Verification)

1.  [ ] **Build**: `npm run build && npx cap sync` 无报错。
2.  [ ] **Run iOS**: Xcode 打开项目，点击 Run，模拟器启动成功。
3.  [ ] **Play**: 点击播放一首歌，按 Home 键切后台，**声音不应该停止**。
4.  [ ] **Lock Screen**: 锁屏，应该能看到音乐控制条。

---

**End of Guide**
