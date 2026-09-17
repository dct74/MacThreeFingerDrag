# TfdProbe —— 触控板/注入机制实测工具

开发本项目时用来把「手感」变成数字的探针。与主程序无关，按需自行编译：

```powershell
cd tools\probe
dotnet build
.\bin\Debug\net8.0-windows\tfdprobe.exe caps              # 导出 PTP 设备 HID 描述符
.\bin\Debug\net8.0-windows\tfdprobe.exe accel             # 实测三种注入方式是否受系统指针加速影响
.\bin\Debug\net8.0-windows\tfdprobe.exe watch 120 watch.log   # 采集真实触点数据（报告率/跳变/抬手语义）
```

* `caps` —— 列出所有 HID Raw Input 设备，并对 usage page `0x0D`/usage `0x05`（Touch Pad）的设备
  逐个打印 HIDP_CAPS、全部 VALUE CAPS（含 LinkCollection、逻辑/物理量程、单位）与 BUTTON CAPS
  （TipSwitch / Confidence）。用于判断某台机器是不是真正的 Precision Touchpad、有几个触点槽位。
* `accel` —— 用 `SendInput` 相对/绝对与 `SetCursorPos` 各走一遍固定位移，测出实际位移比
  （本机结果：相对注入 30px 步长被放大 **1.94×**，绝对注入与 SetCursorPos 精确 **1.000×**）。
* `watch` —— 同时挂 Raw HID 与官方 WM_POINTER 两条后端，输出：报告率分位数、手指数直方图、
  `TipSwitch` 与 `ContactCount` 的一致性、静止帧比例、逐帧位移分位数、跳变统计、
  ContactId 变化、抬手帧语义，以及官方 API 在后台窗口是否收得到输入。

注意：`watch` 会对 `%TEMP%` 之外的指定路径写日志；采集时不要同时运行主程序，以免混淆输入。
