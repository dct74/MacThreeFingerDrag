**Windows 上复刻 macOS 三指拖动：无界面、仅系统托盘** —— 单文件原生 exe，零运行时依赖。

### 下载

| 文件 | 说明 |
| --- | --- |
| `MacThreeFingerDrag.exe` | {{SIZE}}，x64。拷到任意 Windows 10/11 x64 机器双击即可用，无需安装、无需任何运行时 |
| `MacThreeFingerDrag.exe.sha256` | 校验和（Scoop manifest 的 autoupdate 也读这个文件） |

```
SHA-256  {{SHA256}}
```

> 本附件由 GitHub Actions 在 `windows-latest` 上用 .NET 8 NativeAOT 构建（版本 `{{VERSION}}`，提交 `{{COMMIT}}`，构建日期 {{BUILD_DATE}}）。
> 校验方式：`Get-FileHash .\MacThreeFingerDrag.exe -Algorithm SHA256`

### 安装

```powershell
scoop bucket add MacThreeFingerDrag https://github.com/dct74/MacThreeFingerDrag
scoop install macthreefingerdrag
```

也可以直接下载上面的 exe 双击运行（不会弹窗，只在系统托盘出现图标）。

### 使用前提（重要）

必须先把 Windows 自带的**三指手势关掉**，否则系统会先截获手势：

设置 → 蓝牙和其他设备 → 触控板 → 三指手势（滑动 / 点按）都设为「无」；
或直接在托盘菜单里执行「修复 Windows 三指手势设置」（可能需要注销一次）。

### 功能

* 三指按住并移动 = 拖动窗口 / 选中文本
* 三指点按**不会产生点击**
* 抬手后 300 ms 内放回手指可**继续同一个拖动**（macOS 的「续拖」）
* 拖动中按住不动不会掉件（不用「静止超时」判释放）
* 托盘菜单：灵敏度、指针加速、拖动按键、注入方式、抬手宽限期、随登录启动、运行统计
* 异常兜底：失联看门狗、热插拔、锁屏/注销/休眠、解析异常、进程被强杀后的卡键自恢复

### 已知限制

* 拖入**提权窗口**需要本工具也提权（UIPI）；安全桌面 / 锁屏无法注入
* 无法拦截 Windows 自带三指手势（Raw Input 只是输入副本）→ 见上方「使用前提」
* 独占全屏游戏可能忽略注入输入
* 手感与 macOS 高度接近而非逐位相同（Windows 与 macOS 的指针加速曲线不同）

参数依据与实测数据见仓库 [docs/DESIGN.zh-CN.md](https://github.com/dct74/MacThreeFingerDrag/blob/main/docs/DESIGN.zh-CN.md)。
