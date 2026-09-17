# Windows「三指拖动」托盘工具 — 可行性评估与实现方案

> 目标：用**无界面 / 仅托盘**的小工具，在 Windows 上复刻 macOS 触控板「三指拖动（Three-Finger Drag）」。
> 输入：对上游项目 `ClementGre/ThreeFingerDragOnWindows`（下称 **TFD**）的源码剖析 + 本机（MacBookPro15,2 / Boot Camp / Windows 11 build 26200）实测。
> 本文件中的「实测」均可在仓库内 `tools/probe` 里用 `tfdprobe.exe caps|accel|watch` 复现；原始采集日志见 §9。

---

## 0. 结论（TL;DR）

| 问题 | 结论 |
|---|---|
| 上游原理可行吗？ | 可行。核心只有两件事：**读 Precision Touchpad(PTP) 的原始 HID 触点**，**合成鼠标左键按下 + 移动光标**。 |
| 本机（Apple 触控板）能跑吗？ | 能。`Apple USB Precision Trackpad` (VID_05AC&PID_027B, MI_02 Col02) 暴露出标准 PTP 顶级集合：usage page `0x0D` / usage `0x05`，5 个触点槽位。 |
| 应该 fork TFD 吗？ | **不应该 fork 应用，应该重写外壳、只继承「引擎知识」。** TFD 是 WinUI3 + Windows App SDK + MSIX + H.NotifyIcon + TaskScheduler，为「有设置窗口的多功能应用」付了 ~100 MB 运行时和一堆依赖；本机甚至连编译都过不了（需 .NET 10 SDK + WinUI 工作负载）。 |
| 有更好的实现方案吗？ | 有，且是**关键差异**：<br>① 用 `TipSwitch`（0x0D/0x42）直接判定手指存在，扔掉 TFD 那套「不完整触点列表重建 + 隔离期 + 位移阈值」启发式；<br>② 解析路径改为「缓存 preparsed data + 单次 `HidP_GetData` + DataIndex 映射表」，把每帧 22+ 次 `GetRawInputDeviceInfo/HidP_GetUsageValue` 降到 1 次；<br>③ 光标注入改为 `SendInput(相对) + SetCursorPos(钉住)` 混合，**绕开系统指针加速曲线**（实测：相对注入被放大 1.94×，`SetCursorPos` 精确 1:1）；<br>④ 释放语义按 macOS 真实行为做「**抬手宽限期 + 可续拖**」；<br>⑤ 托盘用原生 `Shell_NotifyIcon` + 原生弹出菜单，**不要 WinForms/WinUI**。 |
| 能 100% 复刻 macOS 手感吗？ | **不能全等**，差距在 3 处（见 §5）：事件要绕一圈输入队列（多 0–2 ms）、采样率受设备限制（~100 Hz）、提权窗口/独占全屏游戏会拒绝注入。其余（跟手度、加速曲线、抬手可续拖）都能做到 95%+ 相似。 |

---

## 1. TFD 核心原理拆解

### 1.1 数据流

```
PTP 设备 (HID, usage page 0x0D usage 0x05)
        │  WM_INPUT (Raw Input, RIDEV_INPUTSINK)
        ▼
ContactsManager.WindowProcess()            touchpad/ContactsManager.cs
        │  TouchpadHelper.ParseInput()     touchpad/TouchpadHelper.cs   ← 全部魔法在这
        │     · GetRawInputData → RAWHID 原始字节
        │     · GetRawInputDeviceInfo(RIDI_PREPARSEDDATA) 每帧重新取
        │     · HidP_GetCaps / HidP_GetValueCaps / HidP_GetUsageValue
        │     · 按 LinkCollection 拼出 TouchpadContact{Id,X,Y}
        ▼
ContactsManager.ReceiveTouchpadContacts()
        │  用 ContactCount 与真实解析出的触点数量做「拼装/补齐/去重」
        ▼
HandlerWindow.OnTouchpadContact()          touchpad/HandlerWindow.xaml.cs
        │  维护 _oldContacts / 帧间隔 ms
        ▼
ThreeFingerDrag.OnTouchpadContact()        threefingerdrag/ThreeFingerDrag.cs  ← 手势状态机
        ├── DistanceManager.GetLongestDist2D()   每指位移, 取「最大位移的那根手指」
        ├── FingerCounter.CountMovingFingers()   短/长延迟「移动手指计数」+ 阈值判定
        └── MouseOperations.*()                  SendInput 左键按下/抬起/相对移动
        ▼
光标跟手（相对注入，受系统指针加速影响）
```

### 1.2 六个关键机制

1. **Raw Input 订阅 PTP**
   `RegisterRawInputDevices({usUsagePage=0x000D, usUsage=0x0005, dwFlags=0x2100})`
   `0x2100 = RIDEV_DEVNOTIFY(0x2000) | RIDEV_INPUTSINK(0x100)`：窗口在后台也能收到输入，还能收到热插拔通知。
   窗口是一个**隐藏的 WinUI Window**，靠 `SetWindowLongPtr(GWLP_WNDPROC)` 挂自己的窗口过程——因为 WinUI3 没有 `WndProc` 重载，这是必须的 hack（`touchpad/Interop.cs`）。

2. **HID 值解析（`TouchpadHelper.ParseInput`）**
   把 `HIDP_VALUE_CAPS` 按 `LinkCollection` 分组遍历：`LC=0` 是顶级集合（`ScanTime 0x0D/0x56`、`ContactCount 0x0D/0x54`），`LC>=1` 每个就是**一个触点槽位**，取 `ContactId 0x0D/0x51`、`X 0x01/0x30`、`Y 0x01/0x31`。
   注意：这套 P/Invoke 结构体布局是从 `emoacht/RawInput.Touchpad` 抄来的，**是对的**（本机已逐字段校验过，见 §2.2）。

3. **触点列表重建（`ContactsManager.ReceiveTouchpadContacts`）**
   PTP 硬件的报告分两种：`ContactCount == 解析出的触点数`（完整帧），或 `ContactCount == 0` + 只有部分触点（部分帧）。TFD 用 `_lastContacts` 累加 + 去重 + 补齐 + 截断，等凑够 `_targetContactCount` 才交给状态机。
   → **这是它最大的技术债**：纯粹因为解析器不知道「这个槽位当前有没有手指」而做的猜谜。

4. **位移计算（`DistanceManager.GetLongestDist2D`）**
   新旧帧按 `ContactId` 配对，取**位移最大的那根手指**的向量作为「手指移动」。
   配套一个 **quarantine（隔离期）**：新出现的触点 40 ms 内不参与计算，避免「刚落下时的驱动假坐标」被当成移动。

5. **手指计数与阈值（`FingerCounter`）**
   维护两套累加器：`shortDelay` 累到 `StopThreshold=10`、`longDelay` 累到 `StartThreshold=100`（单位是「触摸板逻辑单位 × 速度系数」），分别用于「确认手指数」和「确认开始拖动」。`_originalFingersCount` 只在触点数 ≤1 或抬手超过 40 ms 时刷新，用来记住「用户本来是想三指操作」。

6. **拖动触发与鼠标注入（`ThreeFingerDrag` + `MouseOperations`）**
   * 开始：`点数≥3 && ID 连续 && 长延迟移动手指数==3 && 原始手指数==3 && !dragging` → `SendInput(LEFTDOWN)`
   * 结束：`短延迟移动手指数<2 || 原始手指数变成 2/4` → `LEFTUP`；或 40 ms(可配置 500 ms) 无输入的超时定时器
   * 移动：`SendInput(MOUSEEVENTF_MOVE)` 相对位移，`ApplySpeedAndAcc()` 里自带一个 **sigmoid 加速曲线**（0.7×–1.5×），外加「N 帧平均」的可选平滑。
   * 抬键后 500 ms 内再落三指可继续拖（`ThreeFingerDragAllowReleaseAndRestart`）——这个其实是在抄 macOS 的「抬手宽限期」。

### 1.3 TFD 的问题清单（都有依据）

| # | 问题 | 位置/证据 |
|---|---|---|
| 1 | **每帧都重新取 preparsed data**（2 次 `GetRawInputDeviceInfo` + `Marshal.AllocHGlobal`/`FreeHGlobal` 各 3 次），并撤销所有解析成本 | `TouchpadHelper.ParseInput` 每帧调用 |
| 2 | 每帧要 **22 次 `HidP_GetUsageValue`**（5 槽 × 4 值 + 2）——100 Hz 下 2200 次 P/Invoke/s，全在 UI 线程 | 同上 |
| 3 | **不用 `TipSwitch`**，靠 `ContactCount` 猜哪些槽有效，于是有了 ContactsManager 那套补丁式重建 | `ContactsManager.cs` 全文 |
| 4 | **重复造加速曲线，却没绕开系统加速**：相对注入仍被 Windows「提高指针精确度」曲线放大（实测 1.94×），两段加速叠加 → 手感不可预测、per-device 速度参数难调 | 见 §2.4 实测 |
| 5 | 用「**位移最大的手指**」当主手指 → 抖动大；手指滚动/换指会跳变 | `DistanceManager.GetLongestDist2D` |
| 6 | 手指静止时若设备不发帧，500 ms 定时器会**误判释放**（正在拖的窗口被丢掉 = 掉件） | `ThreeFingerDrag.OnTimerElapsed` |
| 7 | 依赖 `deviceInfo.deviceId`，而 `GetRawInputData` 的 `hDevice` 在内置触控板上**可能是 NULL** → 潜在 NRE | `TouchpadHelper.GetDeivceInfo` 返回 null 时未判空 |
| 8 | 重：WinUI3 + WindowsAppSDK 1.8 + MSIX + H.NotifyIcon + WinUICommunity + TaskScheduler | `ThreeFingerDragOnWindows.csproj` |
| 9 | 本机无法构建（只有 .NET 8 SDK，项目要 net10） | `dotnet build` → `NETSDK1045` |
| 10 | 需要用户手动关掉 Windows 三指手势（README 明说）。**必须的**，且工具完全可以代劳（§4.7） | `README.md` |

---

## 2. 本机实测事实（MacBook Pro 15,2 / Boot Camp / Windows 11 26200）

### 2.1 设备

```
HID-compliant mouse                          HID\VID_05AC&PID_027B&MI_02&COL01   ← 鼠标集合（系统光标）
Apple USB Precision Trackpad                 USB\VID_05AC&PID_027B&MI_02          ← 父设备
HID\VID_05AC&PID_027B&MI_02&Col02   usagePage=0x000D usage=0x0005                ← ★ PTP 顶级集合，我们要的
HID\VID_05AC&PID_027B&MI_02&Col03   → "Microsoft Input Configuration Device"      ← 触控板配置集合（触感/力度）
```
另外机器上有一个第三方虚拟 HID：`ROOT\HIDCLASS\0000 / netease\GVInput`（网易的虚拟输入设备，会创建 HID-compliant mouse）。**与本方案无冲突**，但说明「虚拟鼠标设备」在本机是存在的，设备枚举要按 usage page/usage 过滤，不能按名字猜。

### 2.2 PTP 顶级集合描述符（`tfdprobe caps` 原始输出要点）

```
UsagePage=0x000D Usage=0x0005 (Touch Pad)   InputReportByteLength=50   ReportID=5
NumberLinkCollectionNodes=6  NumberInputValueCaps=22  NumberInputButtonCaps=11

LC=0  ScanTime      0x0D/0x56  bit16
LC=0  ContactCount  0x0D/0x54  bit8    (Logical 0..127)

LC=1..5（5 个触点槽位，每槽 4 个 value + 2 个 button）:
  value  ContactId  0x0D/0x51  Logical 0..65535
  value  X          0x01/0x30  Logical 0..12992   Physical 0..13160  Units=0x11(cm)
  value  Y          0x01/0x31  Logical 0..7855    Physical 0..8016   Units=0x11(cm)
  value  Pressure   0x0D/0x30  Logical 0..2000
  button TipSwitch  0x0D/0x42  DataIndex = 5,11,17,23,29   ← ★ 手指存在位
  button Confidence 0x0D/0x47  DataIndex = 4,10,16,22,28
LC=0  button Button1  0x09/0x01  ← 触控板物理按压
```

**两个关键结论：**
* **有 `TipSwitch`** → 可以直接、精确地知道哪根手指在板上，不需要 TFD 的触点重建启发式。
* **5 个槽位 + 每槽有 `ContactId`** → 可以用 ContactId 稳定索引手指（而不是靠列表顺序）。
* 逻辑量程 12992 × 7855，物理量程 13160 × 8016。若按「物理值以 10 µm 为单位」解释，触控板约 **13.16 cm × 8.02 cm**，与 13" MBP 的实测尺寸一致 → 可以据此算出 **≈ 0.0101 mm/逻辑单位**，用于物理 1:1 映射（但见 §4.4 的默认标定策略，更稳的是「整块板宽 = 一屏宽」）。

### 2.3 报告率与「静默」语义（★ 实测，v2 阻塞式消息循环）

> 注意：v1 采集器用 `PeekMessage + Sleep(1)` 轮询，而 Windows 上 `Sleep(1)` 实际约睡 **15.6 ms**，把测得的间隔压成了假的 ~14.85 ms。改成**阻塞式 `GetMessage`** 后实测：

| 场景 | 帧间隔 | 等效速率 |
|---|---|---|
| **3 指接触/移动** | ≈ **7.9–8.5 ms**（非常稳定） | **≈ 125 Hz** |
| 1 指极慢移动 | 多数 8 ms，偶发 **32.1–32.7 ms** 的间隙 | 125 Hz，慢速时偶发丢帧 |
| 无接触 | **完全没有帧**（几十秒 0 帧） | — |
| 手指落下瞬间 | 一次「突发」：连续 5–7 帧在 0.06–0.9 ms 内到达 | 驱动把积压/合并的帧一次性投递 |

关键推论：
* **拖动期间数据源是 125 Hz（8 ms）**，比 TFD 注释里假设的 100 Hz 更好 → 每帧的处理预算只有 8 ms，解析开销必须小（§4.2 的优化更有必要）；
* **落指瞬间会有突发帧**（一次收到多帧真实轨迹点）→ 必须逐帧按顺序消费（不能只取最后一帧，否则丢掉起始轨迹）；
* **不能靠「多久没帧」判释放**：只有 1 指慢速移动才会出现 32 ms 间隙，而 3 指按下时帧流连续；但空闲时确实 0 帧，所以「无帧」既可能是空闲也可能是停顿；
* **必须在阻塞消息循环里处理**（`GetMessage`），用轮询式 `PeekMessage+Sleep` 会引入最多 15.6 ms 的额外延迟（比整帧预算还大）。

### 2.4 三指帧实测结构（真实样本，125 Hz 连续 20 帧）

```
[3F] dT=8.20ms cC=3 down=3
  LC1[tip=1 conf=1 cid=1 x=7813 y=3696 p=1]
  LC2[tip=1 conf=1 cid=4 x=5943 y=3692 p=1]
  LC3[tip=1 conf=1 cid=5 x=4684 y=5208 p=22]
  LC4[tip=-  conf=-   cid=0 x=0    y=0    p=0]      ← 未激活槽位：TipSwitch/Confidence 位根本不出现
  LC5[tip=-  conf=-   cid=0 x=0    y=0    p=0]
```

* `TipSwitch` 与 `Confidence` 在 3 指序列里始终为 1，且**两者的语义一致** → 用 `tip == 1` 做「手指在场上」的唯一判据；未激活槽位 tip 为空（`HidP_GetData` 不返回值为 0 的按钮位）→ 解析时必须把「缺失」和「0」都当作不激活。
* `ContactId` 在同一次接触内**稳定且互不相同**（上例 1/4/5 连续 20 帧不变），但**跨接触会变**（同一次会话里 LC1 出现过 1,2,3,4,6,8,9,10,11）→ 可以做手指身份索引，但**每次新接触必须重新基线化**。
* **槽位（LinkCollection）也是稳定的**：三指期间触点一直在 LC1/LC2/LC3，没有槽位漂移。
* 一次会话中 `ContactCount(0x0D/0x54)` 与「TipSwitch==1 计数」有 **1.5%（64/4300）的帧不一致**（部分帧 `cC=0` 但确实有触点）——这就是 TFD 要写一整套「触点列表重建」的原因，而用 TipSwitch 天然免疫。
* 抬手会被**显式上报**：出现 `down=0` 的帧（TipSwitch 全 0），因此状态机可以靠事件判释放，不需要超时猜测。

### 2.5 光标注入实测（`tfdprobe accel`）★ 这是最重要的实测

本机设置：`MouseSpeed=1 Threshold1=6 Threshold2=10`（= 「提高指针精确度」开启），屏幕 1536×960。

| 注入方式 | 请求位移 | 实际位移 | 比率 |
|---|---|---|---|
| `SendInput` 相对 MOVE，8 px × 20 | +160 px | +159 px | **0.99** |
| `SendInput` 相对 MOVE，30 px × 10 | +300 px | +583 px | **1.94** ← 被系统加速曲线放大 |
| `SendInput` 相对 MOVE，30 px × 10 连发 | +300 px | +595 px | **1.98** |
| `SendInput` 绝对 MOVE（VIRTUALDESK）8 px × 20 | +160 px | +160 px | **1.000** |
| `SetCursorPos` 8 px × 20 | +160 px | +160 px | **1.000**（耗时 1–2 ms/20 次） |

**结论：**
* 相对注入 **会**被用户指针设置缩放（小步长 1:1，大步长 ~2×）。TFD 只用相对注入 + 自己的曲线 ⇒ **双重加速**，这就是它「速度/加速度」参数在不同机器上表现不一致的根因。
* 绝对注入 / `SetCursorPos` **完全不受加速影响**，是确定性的。
* `SetCursorPos` 同步、不入队、几乎零延迟；但它**不产生 `WM_MOUSEMOVE`**，所以目标窗口（窗口拖动、任务栏、OLE 拖放）看不到鼠标移动 —— 必须再补一次相对注入。
* 反之纯绝对注入 + 排队会带来「事件排队把光标拉回旧位置」的问题。

→ 正解是 **混合**：`SendInput(相对 MOVE)` 提供给目标窗口的鼠标消息 + 自己维护的光标模型 + `SetCursorPos(模型位置)` 钉住位置（见 §4.4）。

### 2.6 Windows 自己的三指手势开关（必须处理）

`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad`（本机现值）：

```
ThreeFingerSlideEnabled : 0      ← 三指左右滑（任务视图/切换桌面），必须 0
ThreeFingerTapEnabled   : 0      ← 三指点按（搜索），必须 0
FourFingerSlideEnabled  : 0
FourFingerTapEnabled    : 0
TapAndDrag              : 1      ← 「双击并拖动以多选」，建议 0（TFD README 也这么要求）
PanEnabled              : 1  ZoomEnabled : 1   ← 双指滚动/缩放，保留
CursorSpeed             : 10     ← 触控板光标速度（我们不使用它）
AAPThreshold            : 2      ← 防误触（掌压抑制），保留
Status\Enabled          : 1
```

Raw Input 只能拿到输入的**副本**，无法拦截/吞掉 Windows 自己的手势 → 这些键必须关（工具可以直接代写 + 提示重启触控板/注销）。本机已经是关的。

### 2.8 汇总统计（20 753 帧实测，含 ~80 s 三指接触）

```
reports=20753  parseFail=0  contactSlots=5
帧间隔（排除长空闲）: mean=8.12ms  p50=8.08  p95=8.56   ->  123 Hz，抖动 ±0.5ms
手指数分布（TipSwitch==1）: 1指=5052  2指=4765  3指=9969  4指=0  5指+=0
ContactCount 分布      : 0->910  1->5068  2->4788  3->9987
不一致帧 (cC != TipSwitch 计数): 108（0.5%）; tip0-但cid>0: 0; tip1-但xy均0: 9
实际坐标范围: X 0..12992  Y 0..7855   ← 恰好用满逻辑量程（说明量程可信，可用于标定）
逐帧槽位位移(逻辑单位): p50=1.0  p90=24.0  p99=129.1  max=13001.5  ← 最大值=整块板，物理不可能，是坏点/槽位复用跳变
静止帧(坐标不变): 18750 (42%)   运动帧: 25736
位移>50 单位: 孤立尖刺=313  连续大位移=1777
各槽位按下的帧数: LC1=19761  LC2=14746  LC3=9982   ← 只用前 3 个槽，无槽位漂移
WM_POINTER（后台窗口 / 线程注册）: 0 条消息
```

由实测直接得到的设计结论：

1. **数据源 123 Hz、抖动 ±0.5 ms** → 每帧预算 8 ms；延迟主要来自设备侧，我们只能不添乱（阻塞消息循环 + 零分配解析）。
2. **42% 的帧是「坐标完全不变」的静止帧**（三指按住不动时仍然 123 Hz 持续上报）→ **绝不能像 TFD 那样用 500 ms 静止超时判释放**（会直接把正在拖的窗口丢掉）。
3. **max=13001.5 单位/帧**（= 13 cm/8 ms ≈ 16 m/s）物理上不可能 → 必须做异常帧处理；同时注意「槽位复用 + cid 变化」也会造成同样的巨跳。
4. 人体手指极限速度约 2 m/s → 8 ms 内最多约 **1600 单位**；因此异常阈值设 **≈2000 单位/帧**既不会吃掉真甩（fast flick 实测约 400–1000 单位/帧），又能拦住坏点。
5. 4/5 指在这轮测试中一帧都没出现 → 4 指场景无法实测，但「3 指稳定 ≥25 ms」的门限仍作为安全措施保留。

### 2.9 官方新 API：`GetPointerTouchpadInfo`（Win11 25H2+）

本机实测（隐藏/离屏窗口、非前台）：
```
GetPointerTouchpadInfo            PRESENT (ordinal 2691)
GetPointerTouchpadInfoHistory     PRESENT (ordinal 2692)
RegisterTouchpadCapableWindow     present，调用返回 True
RegisterTouchpadCapableThread     present，调用返回 False (err=87，需传真实线程句柄)
→ 用户实际操作三指/单指期间：WM_POINTER 消息 = 0 条（DOWN/UPDATE/UP 全为 0）
```
**结论：后台（非前台、离屏）窗口收不到触控板的 `WM_POINTER`，所以官方路径对「托盘常驻工具」不可用。**
（文档明确说明 `TouchpadGesturesController` 只能在前台收到事件；实测表明 `GetPointerTouchpadInfo` 那条路径对后台窗口同样不投递。）
因此本方案采用 **Tier-B：Raw HID**（已实测可用），官方 API 仅作为「前台窗口内应用」的参考。

微软文档（Windows Precision Touchpad Programming Guide + `GetPointerTouchpadInfo`）说明：
* 先 `RegisterTouchpadCapableWindow(hwnd, TRUE)` 注册窗口，然后会收到 **`WM_POINTERDOWN/UPDATE/UP`**；
* `GetPointerTouchpadInfo` 返回 `POINTER_TOUCH_INFO`，其中 **`ptHimetricLocation` = 触点相对设备的位置**（即触控板坐标），`ptPixelLocation` = 手势开始时的鼠标指针位置；
* 系统会做**disambiguation（手势消歧）**、丢弃前几帧并在识别为手势时**合成一帧 `POINTER_FLAG_DOWN`**（文档明确提醒「如果用于内容操作，注意起始不要跳变」）；
* 还可以把帧喂给 `InteractionContext2`（`ProcessPointerFramesInteractionContext2`），用 himetric 输出 + **速度相关的输出缩放**（即系统帮你做加速），并自动避免起始跳变。
* 同族的 `TouchpadGesturesController`（WinRT）**只能在进程处于前台时收到事件**（文档原文），因此不适合托盘常驻工具。

**判断**：这是「官方牌」的实现路径，但 **是否能在后台窗口收到触控板 `WM_POINTER` 尚未验证**（文档没说，需实测）。因此方案把它列为 **Tier-A 可选后端**，Tier-B（Raw HID）为保底。

---

## 3. 评估：该不该重构？怎么重构？

### 3.1 保留 vs 丢弃

| 组件 | 处理 | 理由 |
|---|---|---|
| HID P/Invoke 结构体与 usage 常量（`TouchpadHelper` 的 Win32 段） | **保留**（已验证正确） | 省掉重复踩坑；只改解析调用方式 |
| `TouchpadContact` / `Point` 等工具类型 | 保留（去掉多余包装） | — |
| `ContactsManager` 触点重建 | **删除** | 有了 `TipSwitch` 就是多余；它也是 bug 温床 |
| `DistanceManager` 隔离期 | **删除**，换「TipSwitch 有效帧 + 孤立尖刺滤波」 | 隔离期 40 ms 是「不知道槽位有效性」的补丁 |
| `DistanceManager.ApplySpeedAndAcc` sigmoid | **保留思路**，参数重标定 + 绕开系统曲线 | 曲线本身合理，问题是叠加了系统加速 |
| `FingerCounter` 双阈值累加器 | **删除**，换显式状态机（稳定 20–25 ms + 位移门限） | 可读性/可调性差，且「移动手指数」语义怪异 |
| `ThreeFingerDrag` 状态机 | 重写（保留「抬手宽限 + 可续拖」语义） | 语义对，实现有误释放 bug |
| `MouseOperations` 注入 | **改**成混合注入 | 见 §2.4 |
| WinUI3 外壳 / 设置窗口 / 单实例重定向 / Elevator / TaskScheduler | **全部丢弃** | 无界面后不需要；权限策略改成「默认非提权」 |
| `H.NotifyIcon` / `WinUICommunity` | 丢弃，换原生 `Shell_NotifyIcon` | 少 5 个依赖、少 ~100 MB 运行时 |

### 3.2 为什么「重写薄壳」优于「fork 精简」

* TFD 的外壳（App/Program/Settings/Elevator/StartupManager/SettingsData + XAML）≈ 60% 的代码量，但和「跟手拖动」这个核心目标无关；删掉它比在它上面改更快。
* 引擎部分真正值得继承的是「**知识**」（HID 描述符怎么读、哪些 usage 要取、状态机时序、系统手势注册表键），而不是它的类结构（`ContactsManager` 的重建逻辑必须删，`FingerCounter` 的阈值语义必须删）。
* 无界面后，配置就是 JSON + 热重载，**不需要任何 XAML/绑定/DispatcherQueue**，也不需要 MSIX 打包（也就没有「必须装 WindowsAppRuntime」这个前置条件）。
* 目标形态：单文件 exe，双击即用，托盘一个图标。C# + NativeAOT 约 2–4 MB；C++/Win32 约 40 KB（参考 `nobu121/win3drag` 的 30–40 KB）；即使不做 AOT，.NET 单文件自包含也就 ~15–40 MB。

**结论：新建工程 `MacThreeFingerDrag`，把 TFD 当参考实现（reference），不做 fork。**

> 本机已实测：NativeAOT 可用 → 「2–3 MB 单文件原生 exe、双击即用、零运行时依赖」是这个工具的正确形态。
---

## 4. 推荐方案（Better Design）

### 4.1 进程与窗口模型
```
单 exe，无窗口界面：
  · 一个 message-only 窗口 (HWND_MESSAGE)         ← 收 WM_INPUT / WM_INPUT_DEVICE_CHANGE
  · 一个 tray 图标 (Shell_NotifyIcon)             ← 左键=菜单，右键=同一菜单（无 Kinetics/动画）
  · config.json 监视 (FileSystemWatcher/ReadDirectoryChangesW)  ← 改文件即生效，无需重启
  · 所有输入处理在单线程消息循环里完成（零锁、零分配、零 GC 压力）
权限：默认 asInvoker（非提权）。Raw Input / SendInput / SetCursorPos / Shell_NotifyIcon 都不需要管理员。
      仅当需要拖拽「提权窗口」时才提供可选的「以管理员身份随登录启动」= Task Scheduler 最高权限任务
      （这一点抄 TFD 的 Elevator 思路，但默认关闭）。
```

### 4.2 输入层（相对 TFD 的 3 个硬改进）

```csharp
// 每设备只做一次（WM_INPUT_DEVICE_CHANGE 时重建）
DeviceCache {
    HANDLE hDevice;
    PHIDP_PREPARSED_DATA prep;                 // ★ 缓存，不再每帧取
    struct Slot { ushort lc; long xMax,yMax; long xPhysMax,yPhysMax; } slots[8]; // ★ 含物理量程
    Dictionary<uint /*DataIndex*/, Entry> map; // ★ DataIndex -> (usage, lc)
    byte[] reportBuf;                          // ★ 复用缓冲，不再每帧 AllocHGlobal
}

// 每帧：一次 P/Invoke 拿到全部值
HidP_GetData(HidP_Input, data, &count, prep, report, len);
foreach (d in data) switch (map[d.DataIndex]) {
   case (0x0D,0x42): slot[lc].tip = d.RawValue;   // TipSwitch  ← 手指存在位
   case (0x0D,0x51): slot[lc].cid = d.RawValue;   // ContactId  ← 稳定索引
   case (0x01,0x30): slot[lc].x   = d.RawValue;
   case (0x01,0x31): slot[lc].y   = d.RawValue;
}
// 另有 (0x0D,0x47) Confidence、(0x0D,0x30) Pressure 备用
```

要点：
* `dwCount > 1` 要循环处理（一帧里多份报告）；**落指瞬间实测会一次到达 5–7 帧（间隙 0.06–0.9 ms）**，必须逐帧按序消费，不能只取最后一帧；
* 消息循环必须用**阻塞式 `GetMessage`**（实测 `PeekMessage+Sleep(1)` 会引入最多 15.6 ms 额外延迟，因为 Windows 的 `Sleep(1)` 实际睡 15.6 ms）；
* 只统计 `tip == 1`（若某设备 `TipSwitch` 恒为 0 → 退化为「该槽有 Digitizer button ON」或 `ContactId>0` 的判定，需要探测一次）；
* 记录 `framesSeen / interval` 用于自动判定设备速率与「设备失联」；
* 屏蔽 `header.hDevice == NULL` 的已知怪癖：按设备路径（VID/PID+path）而不是 hDevice 作为配置键，且单设备时直接回退。

**预期收益**：每帧解析成本从「22 次 P/Invoke + 4 次堆分配 + 每帧取 preparsed」降到「1 次 P/Invoke」，100 Hz 下把 CPU 从「可观」压到「可忽略」，同时消掉 GC/分配带来的抖动。

### 4.3 手势状态机（按 macOS 真实语义设计）

macOS 真实行为（含关键细节）：
1. 三指落下并**开始移动**即开始拖动；
2. 拖动中光标按**同一套指针加速曲线**跟手；
3. **抬起手指后有一个短暂的「宽限期」**：在此期间把手指放回触控板可以**继续同一个拖动**（很实用：可以把手指挪回触控板另一侧继续拖）；
4. 三指**点按（无位移）不是拖动**，不能点出一次左键按下。

推荐状态机（时间单位 ms，参数已按 §2.8 实测标定）：

```
IDLE
  n_active == 3 连续 ≥ 25ms                        → ARMED      (实测 2 指→3 指的过渡在 8ms 帧率下只需 1–3 帧)
ARMED
  累计手指位移 ≥ armDistance(≈30 单位 ≈ 0.3mm)      → SendInput(LEFTDOWN) → DRAGGING
  n_active != 3                                    → IDLE       (纯三指点按 → 不产生点击)
DRAGGING
  n_active == 3 :
      主手指 = 活动触点质心（见下）
      位移 = 质心位移（异常帧过滤，见下）
      SendInput(相对 MOVE)  +  SetCursorPos(模型位置)
  n_active 变化（加/减指）或 某槽 cid 变化:
      重新基线化 + 重选质心，本帧不移动                        ← 避免巨跳（实测 max=13001 单位就是这种跳变）
  n_active != 3 :
      启动 releaseTimer = 300ms（可配 150–600）           → DRAG_GRACE
DRAG_GRACE
  300ms 内重新 3 指落下                                → DRAGGING（不重按左键 = 真·续拖）
  收到 TipSwitch 全 0 的帧（实测抬手会显式上报）且 >40ms 无新帧 → SendInput(LEFTUP) → IDLE
兜底看门狗（任何状态）
  连续 ≥ 250ms 收不到该设备任何帧 且 正处于 DRAGGING      → 强制 LEFTUP
  WM_INPUT_DEVICE_CHANGE / 会话锁定 / WM_ENDSESSION    → 强制 LEFTUP
```

**异常帧过滤规则（替换 win3drag 的「50 单位孤立尖刺」规则）**：
```
if (contactSetChanged || cidChanged)      -> 重新基线化，本帧位移 = 0
else if (|Δ| > 2000 单位/帧)              -> 丢弃该帧位移，重新基线化（并计数告警，便于观测硬件）
else                                      -> 正常处理
```
理由：实测 p99 = 129 单位/帧，而 13001 单位/帧（≈16 m/s）物理不可能；同时「按 (槽位,cid) 匹配 + 集合变化就重新基线」能从根上消除绝大部分假跳变（实测 313 次「孤立尖刺」里相当一部分来自槽位复用）。

**若需更保守的平滑（可选）**：对质心位移做一阶低通 `Δ_out = α·Δ + (1-α)·Δ_prev`（α ≈ 0.6–0.8），或在结尾做 2–3 帧的轻插值以把 123 Hz 提到 165 Hz 观感；不建议默认开启（会增加延迟）。


**几个刻意的设计选择：**

| 选择 | 理由 |
|---|---|
| 开始门限 = 「稳定 25 ms」+「位移≥armDistance」 | 25 ms 挡 4 指手势；位移门限让「三指点按」不产生点击（win3drag 用纯 20 ms，会把三指点按变成点击；TFD 用位移阈值 100 单位，偏大且和速度参数耦合） |
| 质心（centroid）而不是 TFD 的「位移最大手指」/ win3drag 的「ContactId 最小手指」 | 质心对单指抖动/换指天然鲁棒，最接近「三根手指一起搬东西」的直觉；接触集合变化时重新基线化即可无跳变 |
| 释放只在「触点数变化」时判定，绝不用「静止超时」 | **实测 42% 的帧是坐标完全不变的静止帧，三指按住时仍以 123 Hz 持续上报**；用超时判释放会把正在拖的窗口丢掉（TFD 的 bug #6，本机必然复现） |
| 宽限期 300 ms 内**不重按**左键 | 这正是 macOS 的「抬手→放回继续拖」；TFD 用 500 ms 定时器近似，但它的定时器会被「静止」误触发 |
| 孤立尖刺滤波（当前 |d| > 50 且上一帧 |d| < 25 → 丢该轴） | Apple 触控板驱动存在偶发跳坐标；连续大位移（真甩动）必须放行 |

### 4.4 光标映射与注入（本方案和 TFD 差异最大的一环）

```
scale（像素/逻辑单位）：
   默认 = SCREEN_W / logicalXMax（整块板宽 ≈ 一屏宽），即 sensitivity = 100
          实测 X 恰好用满 0..12992、Y 用满 0..7855 → 该标定可靠
   可调 sensitivity 50..500（对应 0.5×..5×），也可以选「按物理尺寸 1:1」模式：
       物理 1:1 → mmPerUnit = xPhysMax / logicalXMax × 0.01  (本机 ≈ 0.0101 mm/单位)
                  像素 = mm × (1 / 该显示器 mm-per-pixel)，跨屏/DPI 用 GetDpiForWindow 换算
accel（可选，默认开，模拟 macOS 指针加速）：
   v = |Δ| / Δt（单位/ms，归一化到 每秒多少个「板宽」）
   mult = 1 + A · smoothstep(v / vRef)     默认 A ≈ 1.6, vRef ≈ 12 板宽/s
注入（每帧一次）：
   model += screenDelta                       // 自己维护的光标模型（不 GetCursorPos 轮询）
   SendInput({MOUSEEVENTF_MOVE, dx=stepX, dy=stepY})   // ① 让目标窗口收到 WM_MOUSEMOVE（窗口拖动/OLE 拖放/任务栏都靠它）
   SetCursorPos(model)                                  // ② 钉住位置：绕开系统指针加速曲线（实测 1.000 vs 1.94）
子像素：把 scale 的小数部分累加到下一帧（TFD 里的 _decimalX/Y，win3drag 里的 g_subX/Y，都保留了，是对的）
边界：模型值 clamp 到虚拟桌面范围；跨屏/负坐标用 SM_XVIRTUALSCREEN 等处理
```

* 为什么不用「纯绝对注入」：绝对事件会**排队**，队列里过期的事件会把光标**拉回旧位置**（win3drag 的作者注释里也明确踩过这个坑）。
* 为什么不用「纯 `SetCursorPos`」：它**不产生鼠标消息**，窗口拖动/任务栏/OLE 拖放拿不到 `WM_MOUSEMOVE`。
* 混合方案是唯一同时满足「光标精确 1:1」「目标窗口看到鼠标移动」的做法；代价是消息流里的坐标可能被系统加速放大（≤1 帧、几像素，不影响窗口最终位置）。

### 4.5 托盘与配置（无界面）

托盘菜单（全部是开关，不需要任何窗口）：
```
☑ 启用三指拖动
   灵敏度             −  120  +        （每档 ±10）
   ☑ 指针加速
   拖动按键           左键 / 中键 / 右键
   抬手宽限期         150 / 300 / 500 ms
   ☐ 以管理员身份随登录启动
────────────────
   修复 Windows 触控板设置（写入 §2.5 的注册表键）
   ☑ 随登录启动（HKCU Run）
   打开配置文件 / 打开日志
   关于 / 退出
```
配置：`%APPDATA%\MacThreeFingerDrag\config.json`，`ReadDirectoryChangesW` 热重载；
日志：滚动文件 + `--debug` 打开逐帧日志（默认关闭，避免 IO 抖动）；
单实例：`CreateMutex(Local\...)`，第二个实例把参数通过 `WM_COPYDATA` 交给已有实例后退出。

### 4.6 分层与代码规模预估

```
src/
  Program.cs            ~120 行   入口、单实例、消息循环、托盘初始化
  Tray/TrayIcon.cs      ~200 行   Shell_NotifyIcon + CreatePopupMenu/TrackPopupMenuEx
  Tray/TrayMenu.cs      ~150 行   菜单项状态映射
  Hid/PtpDevice.cs      ~260 行   枚举/注册/缓存/解析（HidP_GetData 路径）
  Hid/HidInterop.cs     ~220 行   结构体与 P/Invoke（可直接移植 TFD 的部分）
  Gesture/DragEngine.cs ~300 行   状态机 + 质心 + 尖刺滤波 + 看门狗
  Pointer/Injector.cs   ~140 行   混合注入 + 子像素 + 边界
  Config/Config.cs      ~120 行   JSON 读写 + 热重载
  Win/Settings.cs        ~90 行   注册表检查/写入（三指手势、自启）
  Win/NativeMethods.cs  ~150 行
  合计 ≈ 1700 行 C#（或 ≈ 900 行 C++）
```

### 4.7 与 Windows 自带手势的关系

* **无法拦截**（Raw Input 是副本）：所以首次运行要检测 §2.5 的注册表键，若非期望值则托盘提示「一键修复」；
* 写入后用 `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, ...)` + 可选重启 `TabletInputService`/注销使其生效（Windows 设置面板改这些键通常即时生效，工具写完后应引导用户实测，必要时提示注销）；
* 4 指手势默认留给系统（我们不碰），因此状态机用「严格 == 3」。

---

## 5. 与 macOS 的保真度差异（诚实清单）

| 维度 | macOS | 本方案 | 差距可否消除 |
|---|---|---|---|
| 事件路径 | 驱动内直接进入指针管线 | Raw Input → 用户态 → SendInput/SetCursorPos 回到指针管线 | 不能；多 0–2 ms（实测 `SetCursorPos` 20 次仅 1–2 ms，`SendInput` 有队列效应）。人手感知阈值 ~10 ms，实际不可感知 |
| 采样/刷新 | 新一代硬件 120–160 Hz | 设备报告 ~100 Hz（本机待实测） | 不能超过设备速率；可插值但不能降延迟 |
| 加速曲线 | Apple 私有曲线 | 自研 smoothstep（形状近似） | 形状可近似，绝对手感需按个人喜好调参 |
| 抬手续拖 | 有（宽限期） | 有（300 ms 宽限 + 不重按） | **可完全复刻** |
| 触控板点按/压力 | 驱动级，与拖动正交 | 我们只读触点，物理按压仍由系统/驱动处理 | 无冲突 |
| 提权/受保护窗口 | N/A | UIPI 会拒绝非提权进程注入 | 可选提权缓解；安全桌面/锁屏无解（macOS 同样不能） |
| 独占全屏游戏 | N/A | 可能忽略注入或走 raw input 不响应 | 无法保证 |
| 系统三指手势冲突 | 无 | 需关掉 Windows 三指手势 | 工具可自动代劳 |

---

## 6. 待验证清单（建议按序做 spike）

1. **【必做】`watch` 采集真实手指数据**：报告率与抖动、`TipSwitch` 是否真的随手指翻转、静止时是否不发帧、抬手是否发末帧、跳坐标的频率。
   `tfdprobe.exe watch 120 watch.log` 然后用 1 指/3 指各划几次、按住不动 2 s、快速甩、单指抬/放。
2. **【可做】Tier-A 后端**：已确认 `RegisterTouchpadCapableWindow` 调用成功、导出存在；还需确认**后台（非前台）窗口能否收到触控板 `WM_POINTER`**。若能 → 可省掉全部 HID 解析，直接用 `ptHimetricLocation`（设备坐标）+ `InteractionContext2`（himetric 输出自带速度缩放）。若不能 → 只保留 Tier-B。
3. **【可做】多设备/热插拔**：外接 Precision Touchpad 插拔、`hDevice == NULL` 场景。
4. **【已验证】NativeAOT 体积/兼容**：本机 `dotnet publish -c Release -r win-x64 /p:PublishAot=true` 可用，hello world 单文件 **1.24 MB** 原生 exe，无 .NET 运行时依赖。托盘工具预估 2–3 MB。

---

## 7. 工作量估计

| 阶段 | 内容 | 估计 |
|---|---|---|
| P0 | spike：watch 数据 + 注入策略验证（本轮已完成大半） | 0.5 天 |
| P1 | HID 层（缓存 + `HidP_GetData` + 设备表 + 热插拔） | 0.5 天 |
| P2 | 状态机 + 质心 + 滤波 + 宽限期 + 看门狗 | 1 天 |
| P3 | 混合注入 + 标定 + 子像素 + 多屏/DPI | 0.5 天 |
| P4 | 原生托盘 + 菜单 + 配置热重载 + 注册表修复 + 自启 | 0.5–1 天 |
| P5 | 打磨：日志、异常兜底（绝不残留左键按下）、长跑稳定性 | 0.5 天 |
| 合计 | 可用版本 | **约 3–4 天**（P0 大部分已完成） |

**验收标准**：1) 三指点按不产生点击；2) 三指落下移动即开始拖动，跟手无可感延迟；3) 拖动中按住不动 5 s 不掉件；4) 抬手后 300 ms 内放回可续拖；5) 无论何种异常（拔设备、锁屏、进程被杀前）都不残留「左键按下」；6) 空闲 CPU < 0.1%，常驻内存 < 30 MB。

---

## 8. 注入方式的理论分析（为什么默认 hybrid）

三种候选注入在 Windows 机制层面的本质差异：

| 注入 | 光标轨迹 | 事件流（WM_MOUSEMOVE） | 排队鲁棒性 |
|---|---|---|---|
| **relative**（`SendInput` 相对 MOVE） | 被系统指针曲线二次整形（实测 8px 步长 1.00×、30px 步长 **1.94×**），强度依赖每帧步长与帧率，且随用户「提高指针精确度」设置而变 | 原生鼠标消息流 | 加法误差 → 最坏差一帧位移，下一帧自愈 |
| **absolute**（`MOUSEEVENTF_ABSOLUTE`） | **不过曲线**，精确 1.000× | 原生鼠标消息流，点==注入位置 | **绝对位置误差** → 队列积压时会把光标拽回几帧前的旧位置（「橡皮筋」） |
| **hybrid**（相对 + `SetCursorPos`） | **不过曲线**（`SetCursorPos` 同步生效、不入队），精确 1.000× | 原生鼠标消息流 | 加法误差有界一帧，且每帧被 `SetCursorPos` 重新锚定（123 次/秒） |

补充机制事实：Windows 的 `WM_MOUSEMOVE` **不是**把每个位移都塞进队列，而是应用取消息时用**当前光标位置**按需合成 → hybrid 下「应用看到的点」天然收敛到「我们锚定的光标位置」，两种表示每帧重新对齐。

**为什么实测三种模式手感接近**：本机逐帧位移分位数 × 标定系数（1536/12992 = 0.118 px/单位）：

| 分位 | 逻辑单位/帧 | 屏幕像素/帧 | 是否越过 Windows 加速阈值（6 px/事件） |
|---|---|---|---|
| p50 | 1.0 | 0.12 px | 否 |
| p90 | 24 | 2.8 px | 否 |
| p99 | 129 | 15.3 px | 是 |

即 **99% 的帧在阈值以下 → 三种模式数学上等价**；只有快速甩（约 1% 帧，~47–118 px/帧）才显出差异，而那正是 relative 会长出 ~1.94× 的地方。

**结论**：
1. 需要「不过曲线 + 无排队误差」的同步锚定 → 只有 `SetCursorPos` 同时满足；
2. 需要「原生鼠标消息」→ 相对注入（`SetCursorPos` 不产生鼠标消息，单独用无法拖动窗口）；
3. 两者的误差特性互补（加法有界 vs 绝对位置无界），每帧重新同步 → **hybrid 理论最优**，且在系统高负载下退化最优雅。

**理论上还能更进一步（P6 可选）**：把注入点下沉到「触控板设备层」——`CreateSyntheticPointerDevice(PT_TOUCHPAD)` + `InjectSyntheticPointerInput` 注入 synthetic 触点，让 Windows 自己的 PTP 栈产生光标运动（曲线只被系统应用一次，与真实手指同源）。前提：Win11 25H2+（本机 build 26200 满足）；需验证与真实触控板共存、以及是否被我们自己的 Raw Input 监听到（反馈回路）。

**正交的待改进点**：macOS 的三指拖动复用普通指针的加速曲线，所以「最像 macOS」要求曲线形状像 **Apple** 的，而不是像 Windows 的、也不是像我们当前的 smoothstep 近似（`1 + 1.6·smoothstep(v/12板宽每秒)`）。这是目前唯一仍有理论提升空间的地方，且与注入方式无关。

---

## 9. v1.0 验收记录（2026-09-17）

验收环境：MacBook Pro 2019（Boot Camp）／Windows 11 build 26200／Apple USB Precision Trackpad (05AC:027B)。

| 验收项 | 结果 |
|---|---|
| 托盘图标与菜单各功能项 | 通过（含自启、灵敏度、加速、按键、注入方式、手势修复、统计信息） |
| 三指拖动窗口 / 选中文本 | 通过 |
| 三指点按不产生点击 | 通过 |
| 三指按住不动不掉件 | 通过 |
| 抬手后 300 ms 内放回可续拖 | 通过 |
| 快速甩不跳变/不漂移 | 通过 |
| 三种注入方式手感对比 | 均「与 macOS 很接近」，差异不可辨（与 §8 预测一致） |
| 残留按下键 / 其他异常 | 未出现 |
| 随登录启动 | 通过（注销重新登录后由 HKCU Run 自动拉起） |

采集数据：`watch` 两轮合计 25 175 帧（v1: 4 422 帧；v2: 20 753 帧，含约 80 s 三指接触）。

v1.0.0 交付：单文件原生 exe（.NET 8 NativeAOT）2.79 MB（含图标资源），零运行时依赖，默认非提权。
