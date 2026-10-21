# WebMusic Android 客户端系统架构设计方案与实施计划 (正式版)

> **版本**：v1.0 (正式复核版)  
> **状态**：待用户复核 (Gate 评审)  
> **适用终端**：Android 8.0+ (API 26+) 物理手机，针对 Android 13+ 现代多媒体及 Android 15 16KB 页对齐做专项优化。

---

## 目录
1. [项目背景与定位](#一-项目背景与定位)
2. [功能边界划分与需求矩阵](#二-功能边界划分与需求矩阵)
3. [系统整体架构与技术选型](#三-系统整体架构与技术选型)
4. [开源解码与格式兼容矩阵](#四-开源解码与格式兼容矩阵)
5. [播放服务与系统级集成设计](#五-播放服务与系统级集成设计)
6. [缓存与离线下载双轨设计](#六-缓存与离线下载双轨设计)
7. [与 WebMusic 现有后端对接工程细节](#七-与-webmusic-现有后端对接工程细节)
8. [轻量化开发与 CI/CD 调试流水线](#八-轻量化开发与-cicd-调试流水线)
9. [分阶段实施计划与里程碑排期](#九-分阶段实施计划与里程碑排期)
10. [复核确认清单 (Gates Checklist)](#十-复核确认清单-gates-checklist)

---

## 一、 项目背景与定位

### 1.1 背景
WebMusic 现已具备较为完善的 Web 桌面端与基础移动端 PWA 体验。但在 Android 移动终端日常使用中存在明显痛点：
- 浏览器在切后台、熄屏或系统内存紧张时会限制或杀掉 Web 进程，无法保证**长时间稳定后台音乐播放**。
- 无法与手机系统级锁屏大卡片、状态栏媒体控件、**耳机硬件线控**（单双击切歌）及**车载蓝牙 (AVRCP)** 深度融合。
- 缺少流媒体**边听边缓存**以及**完整离线下载保存**能力，弱网或离线状态无法消费媒体。

### 1.2 客户端定位
- **定位**：专注于移动场景的**高保真个人流媒体消费端**。
- **职责边界**：以极致的播放稳定性、音质解码还原、后台保活、锁屏交互和离线消费为主，**坚决剥离高风险服务端运维操作与重型桌面管理功能**。

---

## 二、 功能边界划分与需求矩阵

对照当前 WebMusic 系统功能模块，划分三类处置方式：

| 功能模块 | 状态 | 交付阶段 | 规划与技术边界 |
| :--- | :---: | :---: | :--- |
| **原文件音频流播放** | **首版包含** | Phase 1 (MVP) | 原文件 Range 流式拉取播放，支持 Seek 与播放队列。 |
| **转码流播放降级** | **首版包含** | Phase 1 (MVP) | 格式无法硬解时的 192k MP3 软解兜底（无 Range，禁用 Seek）。 |
| **后台持久播放** | **首版包含** | Phase 1 (MVP) | 基于 `MediaSessionService` 保持前台通知服务，杜绝断播。 |
| **锁屏/通知栏控制** | **首版包含** | Phase 1 (MVP) | 系统级 `MediaStyle` 卡片联动（封面、标题、进度、播放控制）。 |
| **物理硬件联动** | **首版包含** | Phase 1 (MVP) | 耳机拔出强制暂停 (`BECOMING_NOISY`)、线控单双击、来电焦点闪避。 |
| **媒体库多维浏览** | **首版包含** | Phase 1 (MVP) | 全部歌曲、专辑分类、艺术家、流派、目录树形浏览及全库关键词检索。 |
| **歌单与收藏同步** | **首版包含** | Phase 1 (MVP) | 登录态下读取/新增/删除收藏夹与自建歌单。 |
| **行级同步歌词** | **首版包含** | Phase 1 (MVP) | 对齐当前 `LyricsPanel`，实现平滑行级滚动与高亮定位。 |
| **临时流式缓存** | **首版包含** | Phase 1 (MVP) | 基于 ExoPlayer `SimpleCache` 实现 LRU 边播边存，重复收听免流。 |
| **主动离线下载器** | 演进交付 | Phase 2 | 独立 `DownloadManager`，完整文件校验，断点续传，用户显式管理。 |
| **离线媒体数据库** | 演进交付 | Phase 2 | 使用 Room 本地保存歌曲元数据、本地封面、LRC 文件，断网无感浏览。 |
| **音频均衡器 (EQ)** | 演进交付 | Phase 2 | 基于 Android `AudioEffect` 接入 5 段均衡器、重低音增强 (BassBoost)。 |
| **无损/特殊格式专项** | 演进交付 | Phase 2 | APE 容器解封装与 DSD/DSF 软解转 PCM 兼容性实测。 |
| **视频播放与画中画** | 演进交付 | Phase 3 | MV/车载视频播放，横竖屏旋转，手势亮度/音量控制，系统画中画 (PiP)。 |
| **逐字动效与双语翻译**| 演进交付 | Phase 3 | 待服务端扩展字级时间戳协议和翻译接口后再行对齐。 |
| **单曲 Tag 轻量编辑** | 演进交付 | Phase 3 | 提供单曲标题、艺术家轻量修改入口（强校验服务端管理员权限）。 |
| **触发库增量扫描** | 演进交付 | Phase 3 | 提供触发扫描按钮与状态进度查询（强校验服务端管理员权限）。 |
| **多引擎运行时切换** | 预留接口 | Phase 3 | 预留 LibVLC 播放适配器，首版不承诺双引擎上线。 |
| **去重与文件物理清理**| **严禁搬迁** | 永久排除 | `MediaDuplicateCleaner` 5 阶段去重、物理文件删除及回滚，留于 Web 端。 |
| **SMB/存储底层配置** | **严禁搬迁** | 永久排除 | 服务端物理挂载运维，移动端不直接触碰。 |
| **数据库备份与还原** | **严禁搬迁** | 永久排除 | 系统级运维功能，移动端不触碰。 |
| **本地 OCR 识别服务** | **服务端承载**| 永久排除 | 移动端仅消费识别完成后的 LRC，不部署本地重型 OCR 模型。 |

---

## 三、 系统整体架构与技术选型

### 3.1 技术栈选型
- **开发语言**：Kotlin 1.9+ (协程 Coroutines + Flow 数据流响应式编程)。
- **UI 框架**：Jetpack Compose (纯原生声明式 UI，深浅色沉浸式主题，Material 3 规范)。
- **多媒体内核**：AndroidX Media3 (ExoPlayer 1.3+) + 预编译开源 FFmpeg 解码扩展。
- **本地存储**：Room Database (离线元数据) + DataStore (用户首选项) + OkHttp Cache / SimpleCache。
- **网络通信**：Retrofit 2 + OkHttp 4 (含 Token 拦截器与流式 Range 请求封装)。

### 3.2 分层架构设计

```
┌─────────────────────────────────────────────────────────────┐
│                 UI 表现层 (Jetpack Compose)                 │
│   HomeScreen / LibraryView / PlayerBottomBar / LyricsScreen │
│   - 通过 MediaController 发起播放意图与监听状态             │
│   - 完全通过 StateFlow 响应状态重组，不直接持有播放器实例    │
└──────────────────────────────▲──────────────────────────────┘
                               │ (IPC / MediaSession 通信)
┌──────────────────────────────▼──────────────────────────────┐
│           播放核心服务层 (PlaybackService : MediaSessionService) │
│   - 维护 Foreground Service 前台通知常驻                    │
│   - 维护 PlayQueue 播放队列、播放模式 (列表/单曲/随机)       │
│   - 处理系统级中断：AudioManager 音频焦点管理、线控广播监听 │
│   - 统一对外暴露标准 MediaSession                           │
└──────────────────────────────▲──────────────────────────────┘
                               │ (调用抽象接口)
┌──────────────────────────────▼──────────────────────────────┐
│             播放引擎抽象适配层 (IMediaPlayerAdapter)        │
│    (实现 Media3 Player 标准规范：play/pause/seek/tracks)     │
├──────────────────────────────┬──────────────────────────────┤
│  【首版唯一主力引擎】        │  【预留扩展引擎】            │
│  Media3 (ExoPlayer)          │  LibVLC Player Adapter       │
│  - MediaCodec 硬件加速 (MP3/AAC/FLAC)                        │
│  - FFmpeg Audio Extension (无损软解补充)                     │
└──────────────────────────────┴──────────────────────────────┘
                               │ (读取数据)
┌──────────────────────────────▼──────────────────────────────┐
│                  数据与缓存层 (Repository Layer)             │
│   ┌───────────────────────────┐ ┌─────────────────────────┐ │
│   │ 流式缓存: SimpleCache     │ │ 离线固化: DownloadManager │ │
│   │ (LRU 淘汰，边播边存)      │ │ (完整性校验，本地 Room 库) │ │
│   └───────────────────────────┘ └─────────────────────────┘ │
│                  统一隔离 Key 生成器 (Server+User+Media+Version)  │
└──────────────────────────────▲──────────────────────────────┘
                               │ (HTTP / Range)
┌──────────────────────────────▼──────────────────────────────┐
│          远程服务端通信 (WebMusic ASP.NET Core API)          │
│       /stream/{id} (Range) │ /api/auth │ /api/playlists     │
└─────────────────────────────────────────────────────────────┘
```

---

## 四、 开源解码与格式兼容矩阵

### 4.1 兼容性基准与样本验证表
以客观样本测试为准绳，首版不夸大格式范围：

| 格式 | 容器 | 编码 | 位深/采样率 | 解码途径 | Seek 表现 | 首版承诺状态 |
| :--- | :--- | :--- | :--- | :--- | :---: | :--- |
| **MP3** | MP3 / ID3 | MPEG Audio | 16-bit, 44.1k | MediaCodec 硬件解码 | ✅ 精确 | **首版直接支持** |
| **AAC** | M4A / MP4 | AAC-LC | 16-bit, 44.1~48k | MediaCodec 硬件解码 | ✅ 精确 | **首版直接支持** |
| **FLAC (标清)**| FLAC | FLAC | 16-bit, 44.1~48k | 系统硬解 / FFmpeg 软解 | ✅ 精确 | **首版直接支持** |
| **FLAC (高清)**| FLAC | FLAC | 24-bit, 96~192k | FFmpeg Audio 软解 | ✅ 精确 | **首版直接支持** (注: Android AudioTrack 默认混音重采样) |
| **OGG / OPUS**| OGG | Vorbis / Opus | 16-bit, 48k | MediaCodec / FFmpeg 扩展 | ✅ 精确 | **首版直接支持** |
| **WAV** | WAV | PCM | 16/24-bit, 44.1~96k | Media3 原生 Extractor | ✅ 精确 | **首版直接支持** |
| **APE** | APE | Monkey's Audio | 16/24-bit | 缺乏官方 Extractor | ⚠️ 需定制 | **Phase 2 专项验证** |
| **DSD (DSF)**| DSF | DSD (DSD64/128)| 1-bit, 2.8224MHz | 需自研 DSD2PCM 解码器 | ⚠️ 需实测 | **Phase 2 专项验证** |
| **常规视频** | MP4 / MKV | H.264 / AAC | 1080P | MediaCodec 硬件解码 | ✅ 关键帧 | **Phase 3 规划** |

### 4.2 解码库升级与 16 KB 页对齐规范
1. **组件化发布原则**：
   - 禁止承诺“运行时任意热替换单 `libffmpeg.so`”。
   - 底层 NDK 库、JNI 胶水层（`ffmpeg_jni.cc`）与上层 `media3-decoder-ffmpeg` 强绑定，必须按 App 版本统一编译、打包、测试与发布。
2. **Android 15 强制 16 KB 内存页对齐**：
   - 在编译原生动态库时，必须向链接器注入对齐参数：
     ```cmake
     target_link_options(${CMAKE_PROJECT_NAME} PRIVATE "-Wl,-z,max-page-size=16384")
     ```
   - 打包后必须执行官方对齐校验脚本：
     ```bash
     python3 check_elf_alignment.py --apk app-debug.apk
     ```

---

## 五、 播放服务与系统级集成设计

### 5.1 `MediaSessionService` 统领后台
- 采用 AndroidX 官方推荐的 `MediaSessionService`。
- 在 `onStartCommand` 中触发 `startForeground`，拉起常驻通知栏，绑定系统 `MediaStyle`。
- **锁屏卡片表现边界**：
  - 依赖 System UI 绘制；保证歌曲标题、艺术家、专辑封面图、上一曲、播放/暂停、下一曲、可拖动进度条。
  - **不承诺**锁屏端自绘音质角标、流派或声波频谱。

### 5.2 系统交互与中断恢复
- **音频焦点 (Audio Focus)**：
  - 来电 / 强焦点丢失：触发 `AUDIOFOCUS_LOSS`，立即暂停并释放前台通知锁定。
  - 短暂被打断 (导航语音)：触发 `AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK`，音量压低至 20%，结束后恢复。
- **防意外外放 (Noisy Broadcast)**：
  - 注册 `AudioManager.ACTION_AUDIO_BECOMING_NOISY` 广播，检测到有线耳机拔出或蓝牙断开，**立即强制暂停**。
- **进程被杀后状态重建**：
  - 实时将当前歌曲 ID、播放进度毫秒值、播放队列索引持久化至 DataStore。
  - 进程重启后静默重建 Player 状态，展示上一曲封面与进度，等待用户点击播放。

---

## 六、 缓存与离线下载双轨设计

将两套存储逻辑彻底解耦：

```
                    客户端点播或发起下载请求
                               │
               ┌───────────────┴───────────────┐
               ▼                               ▼
    【轨 1: 临时流式缓存】           【轨 2: 主动离线完整下载】
  - 核心类: ExoPlayer SimpleCache   - 核心类: Media3 DownloadManager
  - 存储位置: app/cache/media/      - 存储位置: app/files/downloads/
  - 策略: LRU 自动淘汰 (上限 2GB)  - 策略: 用户显式管理与删除
  - 特点: 边播边缓存，断网不保完整  - 特点: 校验 Content-Length，支持断点续传
                                    - 联动: 同步固化 元数据/封面/LRC 到本地 Room 库
               └───────────────┬───────────────┘
                               ▼
        统一隔离 Key: Hash(ServerUrl_UserId_MediaId_FileVersion)
        (杜绝多账号串用、服务端更新文件后的脏缓存问题)
```

---

## 七、 与 WebMusic 现有后端对接工程细节

### 7.1 原文件流与 Range 穿透验证 (`MediaController.cs:539`)
- 客户端使用 `OkHttpDataSource.Factory` 发起请求，统一路由为 `/api/media/stream/{id}`，自动携带 `Range: bytes=start-end`。
- **部署检查点**：
  - 验证 SMB 远程网络共享盘在持续并发 Range 请求下的 I/O 稳定性。
  - 验证 Nginx 反向代理配置了 `proxy_buffering off` 并暴露了 `Content-Range, Accept-Ranges`。

### 7.2 转码流只读兜底策略 (`MediaController.cs:566`)
- 后端转码流输出实时 192k MP3，无 `Content-Length`，关闭了 Range。
- **客户端规范**：
  - 仅作为异常格式软解失败时的最终播放兜底。
  - 转码播放状态下**禁用进度条拖动 (Seek)**，**禁止加入离线下载任务**。

### 7.3 JWT 鉴权过期处理
- 后端 Token 有效期为 7 天，当前后端**未实现 refreshToken 接口**。
- 客户端规范：当长期后台播放或网络请求遇到 HTTP 401 Unauthorized 时，清空本地无效凭据，平滑提示用户重新登录，不预设虚假的无感续期逻辑。

### 7.4 行级歌词对齐 (`LyricsPanel.tsx:38`)
- 服务端提供标准 LRC 文本。
- 客户端首版实现行级时间戳解析（`[mm:ss.xx]`），基于 Compose 提供高帧率弹性滚动与当前行高亮动效。
- 逐字动效与双语翻译契约列入 Phase 3，等待服务端扩展字段支持。

---

## 八、 轻量化开发与 CI/CD 调试流水线

针对**“本机硬件资源紧张”**，执行**“云端承担重度构建 + 本地纯 ADB 极轻量调试”**分工：

```
  【开发者】 (修改代码，本机资源消耗 < 200MB)
      │
      ├─► git push
      │      │
      │      ▼
      │   【GitHub Actions】 (云端 4核/16GB 免费 Runner)
      │      - 检出代码、准备 JDK 17 与 Gradle Wrapper
      │      - 执行 ./gradlew assembleDebug 编译构建
      │      - 输出 app-debug.apk 产物 (耗时约 3~5 分钟)
      │      │
      │      ▼
      ├─► 下载产物 app-debug.apk 到本地
      │
      ├─► adb install -r app-debug.apk (秒级推送到真实手机 OnePlus 9)
      │
      └─► adb logcat -v time | grep -E "WebMusic|ExoPlayer|MediaSession"
          (终端毫秒级查看网络请求、解码与报错日志，几乎 0 内存开销)
```

> **注**：当前 CI 先保障 Media3 官方基础库的完整自动化构建。FFmpeg 源码交叉编译和 16 KB 页对齐校验将在 Phase 2 样本攻坚阶段作为独立 NDK 步骤接入。

---

## 九、 阶段 0 (Spike) 验收标准与演进排期

### 阶段 0: 核心播放链路 Spike (已全部验证通过 ✅)
- [x] 搭建 Android 工程骨架与 GitHub Actions 构建流水线 (产出 app-debug.apk)。
- [x] 补齐 Gradle Wrapper Jar 与权限配置，规避 CI 启动失败。
- [x] 对齐真实后端接口：登录 `/api/auth/login`、分页列表 `GET /api/media`、原文件流 `/api/media/stream/{id}`。
- [x] 安全脱敏：移除 URL Query 中的 Token，统一在 OkHttp Header 中承载 Bearer Token，并对 Logcat 日志脱敏。
- [x] 配置音频焦点管理 (`AudioAttributes.Builder().handleAudioFocus(true)`) 与耳机拔出防外放广播。
- [x] 将歌曲元数据 (`MediaMetadata`) 完整注入 `MediaItem`，支持系统锁屏与通知栏正确展示。
- [x] **真机验证通过记录**：
  1. OnePlus 9 物理机成功无线 ADB 连通；
  2. 登录 `https://music.maifeipin.com` 获取并持久化 Token；
  3. 成功拉取全库 80,124 首歌曲列表；
  4. 原文件点播发声正常，Logcat 确认 `AudioTrack` 启动并收到流数据；
  5. 验证拖动 Seek、切后台系统锁屏大卡片联动及音频焦点接管。

### 阶段 1: 核心消费端 MVP 交付 (开发中 🚀)
- [x] **流式 LRU 缓存**：接入 ExoPlayer `SimpleCache` (2GB 上限) + `CacheDataSource`，实现透明分片缓存；仍在本地缓存中的分片可离线复用免流。
- [x] **多账号与版本缓存隔离**：`CustomCacheKey` 整合 ServerUrl SHA-256 + Username + MediaId + FileHash (服务端新增投影)，彻底杜绝同大小内容更新与多账号脏读。
- [x] **歌词防竞态引擎**：接入 `/api/lyrics/{mediaId}`，支持平滑行级逐行滚动与高亮渲染 (`LyricsView`)，带请求取消与当前曲目 ID 校验防乱序。
- [x] **原生播放队列与生命周期解耦**：
  - 队列交付底层 ExoPlayer 内核驱动，解耦 Activity 生命周期，支持系统锁屏切歌与自动连播；
  - 底层默认显式应用 `REPEAT_MODE_ALL` 列表循环，消除首次播放末尾停止问题；
  - Activity 旋转或重建后，自动同步恢复底层播放模式并主动拉取当前歌曲歌词。
- [x] **防抢占搜索与分页版本控制**：
  - 严格分离输入防抖态与已确认查询，防抖期间冻结底部分页触发；
  - 引入递增 `queryVersion` 校验，彻底阻断慢网络响应与旧关键词的分页乱序混杂；
  - 底部常驻 `MiniPlayerBar`（黑胶唱片小动效、进度条、切歌）；
  - 沉浸全屏播放页 `FullPlayerSheet`（黑胶大唱盘与平滑歌词双向切换）。

### 阶段 2: 完整离线与音效强化 (5~7 天)
- [ ] 接入 Media3 `DownloadManager`，实现歌曲后台完整离线下载与断点续传。
- [ ] 搭建 Room 本地数据库，离线保存歌曲元数据、本地封面路径与 LRC 歌词。
- [ ] 接入 Android 5 段音频均衡器 (EQ) 调节面板。
- [ ] APE / DSD 音频样本专项测试与兼容性摸底。
- [ ] CI 接入 NDK 交叉编译 FFmpeg 动态库与 16 KB 页大小验证。

### 阶段 3: 体验扩展与生态对接 (按需排期)
- [ ] 视频播放器 (MV/车载视频) 与画中画 (PiP) 支持。
- [ ] 逐字卡拉 OK 动效与双语翻译（对齐服务端新协议）。
- [ ] 媒体库触发增量扫描与单曲 Tag 轻量编辑（管理员权限控制）。
- [ ] 预研 LibVLC 播放适配器。

