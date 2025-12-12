# WebMusic v2 重构测试用例

## 测试环境准备
```bash
cd /Users/lilee/Projects/dev/WebMusic/v2
./dev.sh
```

---

## 1️⃣ GlobalExceptionFilter（API 统一错误处理）

### 测试目标
验证当 API 发生异常时，返回统一的 JSON 错误格式。

### 测试用例

| ID | 测试场景 | 预期结果 |
|----|----------|----------|
| E1 | 访问不存在的媒体文件 `/api/media/stream/999999` | 返回 404 JSON `{ success: false, statusCode: 404, message: "..." }` |
| E2 | 使用无效 Token 访问受保护 API | 返回 401 错误 |
| E3 | GetGroups 传入无效参数 `/api/media/groups?groupBy=invalid` | 返回 400 BadRequest |

---

## 2️⃣ PathResolver（路径解析服务）

### 影响的 API 端点
- `GET /api/media` (GetFiles)
- `GET /api/media/directory` (GetDirectory)

### 测试用例

| ID | 测试场景 | 操作步骤 | 预期结果 |
|----|----------|----------|----------|
| P1 | 目录根级别浏览 | 访问 Library > Directory 视图，点击根节点 | 显示所有 ScanSource 作为根文件夹 |
| P2 | 子目录浏览 | 点击任意文件夹进入子目录 | 正确显示该目录下的子文件夹和歌曲 |
| P3 | 嵌套目录浏览 | 继续点击进入更深层目录 | 路径解析正确，显示正确内容 |
| P4 | 目录播放 (非递归) | 在某个目录点击 "Play" 按钮 | 只播放该目录下的歌曲（不含子目录） |
| P5 | 目录播放 (递归) | Library 中使用 Play Folder 功能 | 播放该目录及所有子目录的歌曲 |
| P6 | 路径过滤 (Path 列点击) | 在 Library List 视图，点击某首歌的 Path 列 | 过滤显示该目录下所有歌曲 |

---

## 3️⃣ Library 页面新功能

### 测试用例

| ID | 测试场景 | 操作步骤 | 预期结果 |
|----|----------|----------|----------|
| L1 | 列头排序 - Title | 点击 Title 列头 | 歌曲按标题排序，再次点击切换升序/降序 |
| L2 | 列头排序 - Artist | 点击 Artist 列头 | 歌曲按艺术家排序 |
| L3 | 列头排序 - Album | 点击 Album 列头 | 歌曲按专辑排序 |
| L4 | 列头排序 - Genre | 点击 Genre 列头 | 歌曲按流派排序 |
| L5 | 列头排序 - Path | 点击 Path 列头 | 歌曲按路径排序 |
| L6 | 列头排序 - Time | 点击 Time 列头 | 歌曲按时长排序 |
| L7 | 多选 - 单选 | 点击某首歌的复选框 | 该行高亮，显示 "Play (1)" 和 "Add to Playlist" 按钮 |
| L8 | 多选 - 多选 | 点击多首歌的复选框 | 显示 "Play (N)" 按钮，N 为选中数量 |
| L9 | 多选 - 全选 | 点击表头的全选复选框 | 当前页所有歌曲被选中 |
| L10 | Play Selected | 选中多首歌后点击 "Play" 按钮 | 播放所有选中的歌曲 |
| L11 | Add to Playlist - 已有歌单 | 选中歌曲 > Add to Playlist > 选择已有歌单 | 歌曲添加到该歌单，提示成功 |
| L12 | Add to Playlist - 新建歌单 | 选中歌曲 > Add to Playlist > Create New > 输入名称 | 创建新歌单并添加歌曲 |
| L13 | 可点击过滤 - Artist | 点击某首歌的 Artist 单元格 | 过滤显示该艺术家的所有歌曲 |
| L14 | 可点击过滤 - Album | 点击某首歌的 Album 单元格 | 过滤显示该专辑的所有歌曲 |
| L15 | 可点击过滤 - Genre | 点击某首歌的 Genre 单元格 | 过滤显示该流派的所有歌曲 |
| L16 | 可点击过滤 - Path | 点击某首歌的 Path 单元格 | 过滤显示该目录下的所有歌曲（不含文件名）|
| L17 | 清除过滤 | 应用过滤后，点击过滤标签的 X 按钮 | 清除过滤，显示所有歌曲 |

---

## 4️⃣ 回归测试（确保没有破坏现有功能）

| ID | 测试场景 | 操作步骤 | 预期结果 |
|----|----------|----------|----------|
| R1 | 基本搜索 | 在 Library 搜索框输入关键词 | 搜索结果正确显示 |
| R2 | 分组视图 - Artist | 切换到 Group 视图，选择 "Group by Artist" | 显示艺术家分组列表 |
| R3 | 分组视图 - Album | 选择 "Group by Album" | 显示专辑分组列表 |
| R4 | 分组视图 - Genre | 选择 "Group by Genre" | 显示流派分组列表 |
| R5 | 分组展开 | 点击某个分组 | 展开显示该分组内的歌曲 |
| R6 | 分组播放 | 点击分组的 Play 按钮 | 播放该分组内所有歌曲 |
| R7 | 播放器基本功能 | 双击歌曲播放 | 歌曲正常播放 |
| R8 | 播放队列 | 播放后检查队列 | 队列正确包含同目录/分组歌曲 |
| R9 | 收藏功能 | 收藏/取消收藏歌曲 | 功能正常 |
| R10 | 歌单页面 | 访问 Playlists 页面 | 正常显示所有歌单 |

---

## 5️⃣ 边界情况

| ID | 测试场景 | 预期结果 |
|----|----------|----------|
| B1 | 空搜索结果 | 搜索不存在的关键词 | 显示空列表，无错误 |
| B2 | 空目录 | 浏览没有歌曲的目录 | 正常显示空列表 |
| B3 | 特殊字符路径 | 包含中文/空格的目录路径 | 正常解析和显示 |
| B4 | 翻页 | 点击 Next/Prev 按钮 | 正确翻页 |

---

## 快速冒烟测试清单 ✅

运行 dev.sh 后，依次验证：

1. [ ] 打开 http://localhost:8090，登录成功
2. [ ] 进入 Library，能看到歌曲列表
3. [ ] 点击 Title 列头，能排序
4. [ ] 点击某首歌的 Artist 单元格，能过滤
5. [ ] 点击某首歌的 Path 单元格，能按目录过滤
6. [ ] 勾选多首歌，点击 "Play" 按钮，能播放
7. [ ] 勾选多首歌，点击 "Add to Playlist"，能添加
8. [ ] 切换到 Directory 视图，能浏览目录树
9. [ ] 点击目录的 Play 按钮，能播放该目录歌曲
10. [ ] 双击歌曲，能正常播放

---

## 测试完成后
如果所有测试通过，执行：
```bash
git add . && git commit -m "refactor: add GlobalExceptionFilter and PathResolver service" && git push
./deploy.sh
```
