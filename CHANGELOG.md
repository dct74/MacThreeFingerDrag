# Changelog

## v1.0.1 — 修复仓库缺失 app.manifest + 发版流程自动化（2026-09-17）

### 修复

* **`app.manifest` 此前从未进入版本库**，被全局 `.gitignore_global` 里的 `*.manifest` 规则**静默排除**
  （`git add -A` 一直跳过它）。CI 构建时才暴露为 `CSC error CS1926: Could not find app.manifest`。
  现已恢复该文件（PerMonitorV2 DPI 感知 + asInvoker 非提权），并在仓库 `.gitignore` 里加了
  `!app.manifest` 否定规则（仓库级优先级高于全局 excludesFile）。
  > v1.0 的 tag 树缺少该文件，**无法从该 tag 重建**；v1.0.1 起可完整复现。

### 工程

* 新增 `build.yml`：推送 `v*` tag 即自动完成 —— .NET 8 NativeAOT 构建 → 启动冒烟测试 →
  按模板渲染发布说明 → 发布 Release（附 `exe` 与 `<exe>.sha256`）；main/PR 只做构建校验
* Scoop manifest 同步抽成可复用 workflow `sync-scoop.yml`（发版后调用 + 每日兜底 + 手动触发），
  预发布 tag 不改动 bucket（bucket 始终指向最新正式版）
* 新增 `.github/release-notes.md` 模板：占位符未替换就让 job 失败，杜绝“校验和过期的发布”
* 新增 `bucket/macthreefingerdrag.json` 与 Scoop 安装说明（`scoop install macthreefingerdrag`）

### 行为

* 与 v1.0 功能完全相同，无行为变更

## v1.0.0 — 首个正式版本（2026-09-17）

在 MacBook Pro 2019（Boot Camp / Windows 11 build 26200 / Apple USB Precision Trackpad）上开发并实测通过。

### 功能

* 三指按住并移动 = 拖动窗口 / 选中文本（无界面，仅系统托盘）
* 三指点按**不产生点击**（3 指稳定 25 ms + 位移门限双条件）
* 抬手宽限期（默认 300 ms）：抬手后放回手指可**继续同一个拖动**（对应 macOS 行为）
* 拖动中按住不动不会掉件（**不采用****静止超时**判释放）
* 拖动键可选左键 / 中键 / 右键；灵敏度、指针加速可调；配置热重载
* 一键修复 Windows 三指手势设置；随登录启动（HKCU Run，非提权）；首次运行气泡提示
* 异常兜底：失联看门狗、热插拔、锁屏/注销/休眠、解析异常、进程被强杀后的卡键自恢复

### 关键实现

* 输入：Raw Input 订阅 PTP 顶级集合（usage page `0x0D` / usage `0x05`），手指存在判定直接用 `TipSwitch`(0x0D/0x42)
* 解析：缓存 preparsed data 与 DataIndex 映射，每帧 **1 次** `HidP_GetData`；缓冲复用、零托管分配；阻塞式 `GetMessage`
* 主手指：活动触点**质心**（不依赖 `ContactId` 连续性）
* 光标：`SendInput(相对 MOVE)` + `SetCursorPos(模型位置)` **混合注入**，绕开系统指针加速曲线
* 分布：单文件原生 exe（.NET 8 NativeAOT）约 2.8 MB，零运行时依赖

### 实测依据（详见 docs/DESIGN.zh-CN.md）

* 触控板三指上报 **123 Hz**（帧间隔 mean 8.12 ms，抖动 ±0.5 ms）；无接触时不发帧
* **42% 的帧是坐标不变的静止帧** —— 三指按住不动时仍持续上报，故不可用静止超时判释放
* `TipSwitch`/`Confidence` 可靠；`ContactCount` 与 TipSwitch 计数有 0.5% 帧不一致（用 TipSwitch 免疫）
* `ContactId` 同一次接触内稳定、跨接触会变；槽位（LinkCollection）不漂移
* 逐帧位移 p50=1.0 / p90=24 / p99=129 逻辑单位，异常帧可达 13001（物理不可能）→ 阈值取 2000
* **相对注入会被系统指针曲线放大（30px 步长 → 1.94×）**，`SetCursorPos`/绝对注入精确 1.000×
* 官方 `GetPointerTouchpadInfo` / `RegisterTouchpadCapableWindow` 在后台窗口收不到输入 → 托盘工具只能走 Raw HID

### 已知限制

* 拖入**提权窗口**需要本工具也提权（UIPI）；安全桌面/锁屏无法注入（系统设计如此）
* 无法拦截 Windows 自带的三指手势（Raw Input 只是输入副本）→ 需关闭系统三指手势（工具可一键完成）
* 独占全屏游戏可能忽略注入输入
* macOS 与 Windows 的指针加速曲线不同，手感为"高度接近"而非逐位相同
