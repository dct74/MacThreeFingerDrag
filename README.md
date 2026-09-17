# MacThreeFingerDrag

在 Windows 上复刻 macOS 触控板**三指拖动**的小工具：**没有界面，只有系统托盘**。
单文件原生 exe（约 2.8 MB，零运行时依赖），默认不需要管理员权限。

* 三指按住并移动 = **拖动窗口 / 选中文本**
* 三指点按**不会产生点击**
* 抬手后约 300 ms 内放回手指可**继续同一个拖动**（macOS 的「续拖」）

### 效果与依据

本项目不是"照着感觉调"出来的：所有时序与阈值都基于**本机真实触点数据**（两轮采集共 25 175 帧）标定，
开发过程中把「手感」逐项变成了数字 —— 触控板 123 Hz 上报、42% 的帧是静止帧、
`TipSwitch` 与 `ContactCount` 有 0.5% 帧不一致、相对注入会被系统指针加速放大 1.94× 等等。
完整分析见 **[docs/DESIGN.zh-CN.md](docs/DESIGN.zh-CN.md)**，实测工具见 **[tools/probe](tools/probe)**。

## 快速开始

1. 下载 [Releases](https://github.com/dct74/MacThreeFingerDrag/releases) 里的 `MacThreeFingerDrag.exe`，放到任意目录双击运行（只有托盘图标，不会弹窗）。
2. 托盘菜单 →「**修复 Windows 三指手势设置**」（必须，否则 Windows 会先截获三指手势）。可能需要注销一次。
3. 三根手指放在触控板上移动即可拖动。

## 托盘菜单

```
☑ 启用三指拖动
灵敏度 ±10 / 指针加速 / 拖动按键(左/中/右) / 注入方式(hybrid/relative/absolute) / 抬手宽限期
☑ 随登录启动        修复 Windows 三指手势设置
打开配置文件        打开日志        详细日志(调试)
统计信息…           以管理员身份重启…        退出
```

* 配置：`%APPDATA%\MacThreeFingerDrag\config.json`（改完 1 秒内自动生效）
* 日志：`%APPDATA%\MacThreeFingerDrag\MacThreeFingerDrag.log`（追加写 + 2 MB 轮转）

## 构建

```powershell
# 开发构建
dotnet build

# 单文件原生 exe（NativeAOT）
dotnet publish -c Release -r win-x64
# 产物: bin\Release\net8.0-windows\win-x64\publish\MacThreeFingerDrag.exe
```

需要 .NET 8 SDK（NativeAOT 还需要 MSVC 链接器 / VS 生成工具）。

## 实现要点（相对上游 ThreeFingerDragOnWindows 的改进）

| 项 | 做法 |
|---|---|
| 手指存在判定 | 直接读 `TipSwitch`(0x0D/0x42) + `Confidence`，删掉上游「触点列表重建 + 40ms 隔离期」启发式 |
| 解析 | 缓存 preparsed data 与 DataIndex 映射；每帧 **1 次** `HidP_GetData`；缓冲复用、零托管分配 |
| 消息循环 | 阻塞式 `GetMessage`（轮询 `PeekMessage+Sleep(1)` 会引入 15.6 ms 额外延迟） |
| 主手指 | 活动触点**质心**，不依赖 `ContactId` 连续性；接触集合变化时重新基线化（防 13000 单位的坏点跳变） |
| 光标注入 | `SendInput(相对)` + `SetCursorPos(模型位置)` 混合，绕开系统指针加速曲线（理由见 DESIGN §8） |
| 释放语义 | 靠 `TipSwitch` 全 0 事件 + 300 ms 抬手宽限（可续拖），**不用静止超时**（实测按住不动时仍持续上报） |
| 异常帧 | 接触集合/`ContactId` 变化 → 重新基线化；单帧位移 > 2000 单位 → 丢弃并计数 |
| 安全兜底 | 250 ms 失联看门狗、热插拔、锁屏/注销/休眠、解析异常、进程被强杀后的卡键自恢复（标记文件） |
| 外壳 | 纯 Win32：消息专用窗口 + 隐藏顶层窗口 + 原生 `Shell_NotifyIcon` + 原生弹出菜单；无 WinForms/WinUI/WindowsAppSDK |
| 图标 | 运行时矢量绘制（同一份代码生成托盘 HICON 与 exe .ico） |

## 已知限制

* 拖入**提权窗口**需要本工具也提权（UIPI 限制）；安全桌面 / 锁屏无法注入（系统设计如此）
* **无法拦截** Windows 自带三指手势（Raw Input 只是输入副本）→ 需按上面第 2 步关闭
* 独占全屏游戏可能忽略注入输入
* 手感与 macOS「高度接近」而非逐位相同：Windows 与 macOS 的指针加速曲线不同；
  若要做到更接近，下一步是拟合 Apple 曲线（见 DESIGN §8 末尾）

## 许可

MIT。第三方来源说明见 [LICENSE](LICENSE)。
