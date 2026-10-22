# Android 应用内一键更新（方案 A）落地说明

> 结论：不做"进程壳 / 动态 DexClassLoader"。升级 = 下载完整 APK + 系统安装器覆盖安装。
> 登录 Token、离线音乐文件、本地 SQLite 数据库在覆盖安装后全部保留，用户的手机从此可以独立升级，不再需要数据线和电脑。

## 1. 全链路

```
┌──────────────────────────── Android 客户端 ────────────────────────────┐
│ 启动 / 设置页「检查更新」                                               │
│   └─ GET /api/app/version?currentCode=2&channel=debug&packageName=…     │
│        ├─ hasUpdate=false → 静默结束（手动时提示"已是最新"）             │
│        └─ hasUpdate=true  → 弹窗展示版本号 / 大小 / 更新说明             │
│              └─ GET /api/app/download/{channel}  （Range 断点续传）      │
│                    └─ SHA-256 校验 → 通过                              │
│                          └─ FileProvider → Intent(ACTION_VIEW)          │
│                                └─ 系统安装器覆盖安装（数据全保留）        │
└────────────────────────────────────────────────────────────────────────┘
```

服务端零第三方依赖：一份 `manifest.json` + 一个目录里的 APK 文件，清单被替换后自动重载，**发布更新不需要重启容器**。

## 2. 服务端

### 2.1 目录结构

```
data/app-releases/                  # 容器内 /app/data/app-releases（compose 已挂载 ./data）
├── manifest.json
├── debug/webmusic-debug-2.apk
└── release/webmusic-release-3.apk
```

可通过 `AppRelease:RootDir` 覆盖根目录（绝对值，或相对 ContentRoot 的相对值）；未配置时按 `{ContentRoot}/data/app-releases` → `{ContentRoot}/../data/app-releases` 依次探测。

### 2.2 manifest.json

```json
{
  "channels": {
    "debug": {
      "versionCode": 2,
      "versionName": "1.1.0",
      "fileName": "webmusic-debug-2.apk",
      "packageName": "com.maifeipin.webmusic.debug",
      "changelog": "- 新增应用内一键检查更新与安装\n- 全屏播放器支持收藏 / 加入歌单",
      "sha256": "…64 位小写十六进制…",
      "forceUpdate": false,
      "minSupportedVersionCode": 1,
      "publishedAt": "2026-10-23T10:00:00+08:00"
    }
  }
}
```

| 字段 | 语义 |
| --- | --- |
| `versionCode` | 与 APK 内 `versionCode` **必须一致**，是唯一的版本比较依据 |
| `fileName` | 必须落在 `data/app-releases/{channel}/` 内，仅 `.apk`；带路径分隔符会被拒绝 |
| `packageName` | 客户端安装前比对，包名不符直接拒绝（避免装错渠道包） |
| `sha256` | 客户端下载完成后校验；不匹配会丢弃重下，防止半截包/中间层篡改 |
| `forceUpdate` | 强制更新，弹窗不可关闭、不可跳过 |
| `minSupportedVersionCode` | 低于该版本的客户端强制更新（用于废弃老协议） |

### 2.3 接口

两个端点均为匿名可访问（版本检查要在登录前就能工作，且不含任何用户私有数据）。

`GET /api/app/version`

```json
{
  "hasUpdate": true,
  "forceUpdate": false,
  "channel": "debug",
  "versionCode": 2,
  "versionName": "1.1.0",
  "packageName": "com.maifeipin.webmusic.debug",
  "minSupportedVersionCode": 1,
  "sizeBytes": 20272495,
  "sha256": "…",
  "changelog": "- 新增应用内一键更新",
  "publishedAt": "2026-10-23T10:00:00+08:00",
  "downloadUrl": "/api/app/download/debug"
}
```

- `downloadUrl` 故意返回**相对路径**：反向代理下的 scheme/host 不可靠，客户端按自己配置的 `serverUrl` 拼接。
- 清单声明了新版本但 APK 文件缺失时，返回 `hasUpdate=false`（宁可不升级，也不给用户一个必然失败的下载）。

`GET /api/app/download/{channel}`

- `Content-Type: application/vnd.android.package-archive`
- 支持 `Range` 请求（断点续传）、`ETag` / `Last-Modified`
- 响应头附带 `X-App-Version-Code` / `X-App-Sha256`，便于用 curl 排查

## 3. 客户端

| 位置 | 文件 |
| --- | --- |
| 版本检查 / 下载 / 校验 / 安装 | `core/update/AppUpdateManager.kt` |
| 状态机（检查→下载→安装） | `core/update/UpdateController.kt` |
| 弹窗 UI | `ui/components/UpdateDialog.kt` |
| 权限与 FileProvider | `AndroidManifest.xml`、`res/xml/file_paths.xml` |

行为要点：

1. **启动静默检查**：`autoCheckOnce()` 每个进程一次，失败完全静默；设置页提供手动「检查更新」。
2. **跳过此版本**：记录 `skipped_version_code`，静默检查不再打扰；手动检查与强制更新不受限制。
3. **断点续传**：分片写 `webmusic-{versionCode}.apk.part`，带 `Range: bytes=N-`；若服务端返回 200（忽略 Range）自动清空重下；下载中断保留分片，下次续传。
4. **完整性校验**：`sizeBytes` 长度比对 + `SHA-256` 比对，任一不符即丢弃，绝不给系统装一个坏包。
5. **安装授权闭环**：Android 8.0+ 必须用户手动授予"安装未知应用"。点击安装时若未授权 → 跳转授权页 → 返回 App 时 `onResumed()` 检测到已授权会**自动续接安装**，用户只需点一次系统"安装"。
6. **FileProvider 最小暴露**：只授权 `updates/` 目录，不暴露其余任何文件。

## 4. 发布流程

```bash
# 1) 构建（CI 或本地）
cd android && ./gradlew assembleDebug

# 2) 发布到后端分发目录（脚本自动算 SHA-256、复制、更新 manifest.json）
./scripts/publish_apk_release.py \
    --apk android/app/build/outputs/apk/debug/app-debug.apk \
    --channel debug \
    --version-code 2 \
    --version-name 1.1.0 \
    --package-name com.maifeipin.webmusic.debug \
    --changelog "- 新增应用内一键检查更新与安装"

# 3) 验证
curl -s 'http://localhost:5080/api/app/version?currentCode=1&channel=debug'
curl -sI 'http://localhost:5080/api/app/download/debug'
```

脚本默认会清理同渠道的旧 APK（`--keep-old` 保留），并拒绝发布比清单更旧的 `versionCode`（回滚需显式 `--allow-downgrade`）。

## 5. 必须守住的约束（踩了就是安装失败）

1. **签名一致**：覆盖安装要求新旧 APK 同一签名证书。
   - `debug` 构建使用仓库内固定的 `android/app/debug.keystore`，所以 debug 渠道可以一路自升级。
   - 一旦换成自己的 release 签名，**必须长期保管同一个 keystore**；换签名只能卸载重装（登录态/离线文件会丢）。
2. **versionCode 递增**：Android 拒绝安装 versionCode 更低的包（`INSTALL_FAILED_VERSION_DOWNGRADE`）。
3. **包名一致**：debug 构建带 `.debug` 后缀，与 release 包互不覆盖，故渠道必须分开（`AppUpdateManager.channel` 依 `BuildConfig.DEBUG` 自动选择）。
4. **targetSdk 与厂商策略**：部分国产 ROM 对"安装未知应用"有额外二次确认，属于预期行为，客户端已做跳转引导。

## 6. 排障速查

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 一直提示"已是最新" | 服务端 `versionCode` ≤ 客户端，或 APK 文件缺失 | 查 `manifest.json` 与文件名是否一致 |
| 下载后安装失败 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | 签名/包名不一致 | 用同签名同包名重新出包 |
| 下载后安装失败 `INSTALL_FAILED_VERSION_DOWNGRADE` | versionCode 未递增 | 提升 versionCode 重新发布 |
| 弹窗一闪而过 / 不弹 | 该版本此前被"跳过" | 手动「检查更新」，或清理应用数据里的 `webmusic_update_prefs` |
| 下载卡在 0% | 反向代理剥掉了 Range 或拦了大文件 | 检查 nginx `client_max_body_size` 与 `proxy_request_buffering` |

## 7. 为什么不是"进程壳 + 动态 Dex"

- Android 14+ 对动态代码加载（DCL）有强制只读/UID 校验，ColorOS/OriginOS 的沙盒引擎会把动态 Dex 判为注入行为；
- Compose 的 IR 与 SlotTable 与 ClassLoader 强绑定，跨 ClassLoader 加载 Composable 极易 `ClassNotFoundException` / `TypeCastException`；
- 音乐 App 的核心是 `PlaybackService`，四大组件必须静态声明在 `AndroidManifest.xml`，外挂 Dex 无法新增 Service、通知通道或权限；
- 结论：业务迭代走应用内 APK 覆盖安装；只有"线上几个空指针"才考虑 Tinker/Robust 级别的字节码热修复，且不承担加组件、改清单的能力。

## 本轮加固与云端发布

- 云端 Debug versionCode = 100000 + GITHUB_RUN_NUMBER，版本名称为 1.1.<code>；本地默认 3。保持此工作流的 run number 连续递增，不可删除重建后沿用更低编号。
- main 构建成功后发布永久版本 Release：android-debug-<code>。固定检查入口为 https://github.com/maifeipin/WebMusic/releases/download/android-debug-latest/update.json；版本 APK 地址固定到独立 Release，不用短期 Artifact 分发。Debug 客户端优先读取该入口，失败时再读取配置的后端，不依赖后端部署进度。私有仓库必须使用后端公开分发，不能嵌入 GitHub Token。
- 强制大小、SHA-256、包名、版本、渠道、最低 SDK 和当前安装签名完全一致校验；当前实现拒绝签名轮换，正式签名迁移另行设计。公开 debug 密钥仅用于内部测试。
- 安装按钮点击后再次校验 APK，保存服务器/账号绑定的队列、位置、循环与随机模式。服务重建后恢复暂停状态，不自动播放；安装取消时不影响当前播放。
- 安装需系统未知来源授权与用户确认。使用完整 APK，不做差分更新。
- 验收须包含：篡改 APK、错误签名/包名/版本、授权拒绝、安装取消、升级后重新连接服务恢复队列、账号切换后拒绝恢复旧快照。

## 签名连续性与传输重试

- **现有 Debug 渠道的 `android/app/debug.keystore` 不可替换或重新生成。** 同包名升级必须延续签名；更换该密钥会导致旧设备无法覆盖安装。CI 必须使用此固定密钥，禁止回退到 Runner 自动生成的密钥。正式渠道使用独立妥善保存的发布密钥，禁止复用公开 Debug 密钥；签名轮换需要专门设计迁移方案。
- 下载首次失败后最多自动重试 3 次（总计最多 4 次请求），退避 1、2、4 秒。每次根据 `.part` 实际长度重建 Range；取消、无效范围、非暂时 HTTP 错误和最终完整性/签名失败不进入重试。耗尽后显示中文网络提示，保留原始异常作为诊断原因。
- `versionCode` 用于严格递增；面向用户的 `versionName` 后续可改为语义版本加构建标识。Release 清理后续独立设计，至少保护当前清单指向版本及必要回退版本；本轮不删除历史产物。
