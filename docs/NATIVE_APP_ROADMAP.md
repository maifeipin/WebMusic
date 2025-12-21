# WebMusic Native App 演进路线图 (Roadmap)

## 1. 背景与目标
目前 WebMusic 已经具备了完善的 PWA 能力 (v2.6.1+)，在移动端体验良好。但在以下场景存在瓶颈：
*   **后台播放持久性**：iOS Safari 在后台暂停超过一定时间或内存紧张时，会杀掉 Web 进程，导致音乐中断。
*   **硬件交互**：无法使用线控 (耳机按键) 完美控制上一曲/下一曲 (受限于浏览器实现)。
*   **推送通知**：无法稳定接收即时消息推送。

**目标**：构建一个轻量级的原生壳 (Native Shell)，在这个壳中运行现有的 Web 前端，同时接管音频播放和系统交互，实现 "Native 级" 的音乐播放体验。

## 2. 技术选型：Capacitor
经过评估，我们放弃重写 Native App (Swift/Kotlin) 或 Flutter/RN 方案，选择 **Capacitor**。

| 维度 | Capacitor | Native/Flutter | 结论 |
| :--- | :--- | :--- | :--- |
| **代码复用率** | 99% (直接复用 React 前端) | < 20% (需重写 UI) | Capacitor 胜 |
| **开发成本** | 低 (仅需配置 Native 插件) | 高 (需维护双端代码) | Capacitor 胜 |
| **性能** | 足够 (UI 也是 Web 渲染) | 极致 | 音乐 App 对帧率不敏感，Capacitor 足矣 |
| **后台能力** | 强 (通过 Native 插件) | 强 | 持平 |

**核心策略**：
*   **UI 层**：完全保留现在的 React/Vite 前端。
*   **逻辑层**：通过 Capacitor Bridge 调用 Native 能力。
*   **音频层**：使用 `capacitor-plugin-background-mode` 或 `webview audio` 配合 iOS `AVAudioSession` 配置。

## 3. 实施阶段 (Phases)

### Phase 1: 基础集成 (预计耗时: 30min)
*   集成 Capacitor 到现有 Frontend 项目。
*   生成 iOS 和 Android 工程目录。
*   实现 Web 资源到 Native 工程的同步构建流。

### Phase 2: 后台保活与播放 (Core) (预计耗时: 2h)
*   **核心难点**：WebView 在后台时 JS 会暂停。
*   **解决方案**：
    *   **iOS**: 开启 `Audio` Background Mode，配置 `AVAudioSession`。
    *   **Android**: 启动前台服务 (Foreground Service) 显示通知栏。
*   即使 JS 被暂停，原生音频线程不能停。

### Phase 3: 锁屏控制与原生特性 (预计耗时: 1h)
*   集成 Media Session API 到原生锁屏控制 (MPNowPlayingInfoCenter / MediaStyle Notification)。
*   处理耳机线控事件。

## 4. 给 AI 协作的建议
如果未来由 AI 辅助实施，请按以下顺序提示：
1.  先让 AI 检查环境 (`xcodebuild -version`, `java -version`)。
2.  提供 `CAPACITOR_IMPLEMENTATION_GUIDE.md` 作为 Context。
3.  不要让 AI 试图去写 Swift/Kotlin 业务逻辑，**强制它只配置插件**。Native 代码越少越稳。
