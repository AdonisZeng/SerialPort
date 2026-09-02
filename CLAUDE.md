# CLAUDE.md

本文件为 Claude Code（claude.ai/code）在此代码库中工作时提供指导。

## 项目概述

SerialPort 是一个 Windows Presentation Foundation（WPF）串口调试助手，由 WinForms 版本 SerialAdonis 移植而来。它可以列出可用的 COM 端口，以可配置的波特率 / 数据位 / 停止位 / 校验位 / 握手协议打开端口，发送文本或十六进制字节，并以文本或十六进制格式显示接收到的数据，支持自动滚动。项目目标框架为 **.NET Framework 4.8.1**，使用旧式（非 SDK）MSBuild 工程格式，**无 NuGet 依赖**。所有代码注释和界面字符串均为 **中文** —— 新增代码或界面文字时请保持一致。

## 构建

Visual Studio 2026 安装在 `D:\Software\Microsoft Visual Studio\18\Community`。构建方法如下：

```bash
# dotnet build 不支持旧式 WPF 工程的标记编译（任务程序集 PresentationBuildTasks 是 .NET Framework 程序集，
# dotnet CLI 的 MSBuild 运行在 .NET 上无法加载）。请用 VS 的 MSBuild.exe 构建：
"/d/Software/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" SerialPort.csproj
# 或直接在 Visual Studio 中 F5 调试（推荐）
```

解决方案文件为 `SerialPort.slnx`（新 XML 解决方案格式，单工程）。没有测试，也没有 NuGet 依赖——只引用框架程序集。

## 重要规则：构建完成后不要运行

**构建完成之后，不要尝试运行程序。** 由开发者（用户）自己运行查看效果，并据此给出反馈。Claude 只负责修改代码与构建；验证运行效果是开发者的事，不要替开发者运行。

## 架构

两层结构，按命名空间划分（继承自 WinForms 原版）：

- **`SerialPort`** —— `MainWindow`（XAML + 代码隐藏）。窗口为垂直分割布局：上半部分为接收显示区（只读 TextBox，带悬浮按钮组：筛选 / 搜索 / 帧解析 / 清空），下半部分为端口配置区（6 个下拉框：端口 / 波特率 / 数据位 / 停止位 / 校验位 / 握手协议）、复选框（编码 / 时间戳 / 十六进制发送 / 十六进制显示 / 追加换行 / 自动滚动 / 暂停显示 / 本地回显 / 文件保存）、连接开关按钮（绿色=未连接，红色=已连接）、发送区（含定时发送间隔与 DTR/RTS 开关、快捷指令入口）以及状态栏（左侧动态信息，右侧统计区与检查更新入口）。它持有唯一的 `SerialPortService` 实例，在构造函数中订阅其事件，并通过私有解析器（`ParseStopBits`、`ParseParity`、`ParseHandshake`、`HexStringToBytes`）将界面字符串转换为串口配置。接收到的数据通过 `txtReceive.AppendText` 增量追加（WPF 中整体重新赋值会很慢），勾选自动滚动时用 `ScrollToEnd` 自动滚动到底部；文本模式用状态保持的 `Decoder` 跨块解码（避免中文被拆块时出现乱码），接收区与筛选缓冲有 **20 万字符**容量上限（超限截头；接收区开了 `TextWrapping`，截头时整段重建并重新排版，上限过大时会周期性冻结 UI）。发送走统一入口 `SendText`（hex / 文本 / 追加换行 / 编码解析，累计发送字节），供发送按钮、定时发送（后台线程循环，`Dispatcher.Invoke` 回 UI 线程取内容）与快捷指令窗口共用。文件保存走后台写队列（写盘不阻塞 UI），更新相关提示由 `UpdateDialog` 负责。

端口枚举（`SerialPortService.GetAvailablePorts()`）**不在 UI 线程执行**：构造函数启动一个后台扫描线程（`StartPortScan` / `PortScanLoop`，`IsBackground`，**0.2 秒一轮**——与 0.5 秒的 `_timerPortCheck` 相位独立，端口变化到 UI 反映最坏约"扫描间隔 + 0.5 秒"，故间隔取 0.2 秒把最坏延迟压回约 0.7 秒）把结果写入 `_lastPorts` 快照，UI 通过 `GetLastPorts()` 只读副本；`TimerPortCheck_Tick` 的重连逻辑与下拉刷新都消费这份快照。设备描述（WMI）查询完成后经 `ApplyDescriptionsInPlace` **就地替换**发生变化的单个 Item（不清空重建），因此对"下拉处于展开状态"是安全的；`cboPortName_DropDown` 会在集合未变但描述仍缺失时补一次回填，**并以 `_lastBackfillPorts` 记录上次补查时的集合去重**——端口在 WMI 中确实没有描述时 `HasMissingDescription()` 恒为 true，不去重会导致每次展开下拉都重跑一次 WMI 查询且结果全部被丢弃。子窗口均为单实例（`_frameWindow` / `_quickWindow`）——快捷指令窗口多开时各自的命令列表副本会互相覆盖保存，故必须单实例。

- **`SerialPort.Services`** —— `SerialPortService`，一个 `sealed` 类，封装 `System.IO.Ports.SerialPort`（通过 `SerialPortType` 别名引用——根命名空间 `SerialPort` 遮蔽了框架类型），以及 `SerialPortConfig`、`DataBits` 枚举、`DataReceivedEventArgs`、`SerialPortItem` / `PortDeviceInfo`（WMI 端口描述查询）、`FrameParser`（通用协议帧解析：帧头 / 帧尾定界、CRC16-Modbus / Sum8 校验，**单帧收集上限 64 KB**，帧头定界模式用 `_partial` 维护一个"恒为帧头前缀"的**候选窗口**：新字节入队后只要 `IsHeaderPrefix()` 判定窗口不再是前缀，就把队首字节出队并计入帧体（帧尚未开始时丢弃），再对剩余窗口继续判定——既避免帧体中与帧头首字节相同的字节被吞掉，也能识别帧头自重叠时窗口中间的帧头（帧头 `AA AA 55`、帧体出现 `AA AA AA 55`）；窗口凑满 `_header.Length` 即完整帧头。`_headerMatch` 仅帧尾定界模式使用）、`UpdateService`（自动更新）。`SerialPortService` 实现 `IDisposable`，并在构造函数中捕获 `SynchronizationContext.Current`。服务区分**逻辑连接**（`IsConnected`，由保存的 `_config` 决定，设备拔出等待重插期间仍为 true，UI 按钮以它为据）与**物理连接**（`IsOpen`，底层端口是否真正打开）；设备拔出时**不会自动断开**，UI 定时器检测到端口重现后调用 `TryReconnect()` 按原配置重建端口实例自动重连（无 `PortGone` 事件）。端口实例在每次打开 / 重连时重建（设备拔出后旧实例内部流已损坏，不可复用）。`_port` 字段跨线程访问（后台接收回调 vs UI 线程重建）由 `_portLock` 保护：**所有读写 `_port` 的方法都必须经 `TryGetOpenPort()` 在锁内取一次快照**，不得直接多次读 `_port`（否则 `DisposePort` 置 null 后第二次读会拿到 null 而抛 NRE）；真正的读写在锁外进行，避免持锁阻塞 `DisposePort`，端口若已被释放只会抛 `IOException` / `InvalidOperationException`，正好由 `HandleDeviceError` 兜住。RTS / DTR 引脚通过 `SetRts` / `SetDtr` 控制（仅物理打开时生效，重连后由 UI 的 `ApplyPinStates` 按勾选重写）。设备异常（拔出 / IO 错误 / 读写超时）经 `DeviceError` 事件上报 UI（**按时间全局节流**：2 秒内最多上报一次、**不区分错误文本**——只比对"上一条文本"会被交替出现的 `IOException`（写失败）与 `InvalidOperationException`（端口重建竞态）逐条绕过，节流形同虚设），只写状态栏不弹窗，不再完全静默。

- **`SerialPort.UI`** —— `ThemeManager`（静态类）和 `ThemeMode` 枚举。通过将 `Application.Current.Resources.MergedDictionaries` 的第 0 项替换为 `Themes/LightTheme.xaml` 或 `Themes/DarkTheme.xaml` 来切换浅色 / 深色主题；所有主题颜色都通过 `DynamicResource` 引用，因此替换后会立即重新着色。强调色（`PortOpenBrush` / `PortCloseBrush` / `SendBtnBrush`）是 App.xaml 中的常量，不随主题变化。主题**持久化**到 `Settings.Theme`：切换时立即 `Save()`，`App.OnStartup` 在启动时恢复；由于 XAML 中主题按钮文案恒为"深色模式"，`MainWindow` 构造末尾必须调用 `UpdateThemeButtonText()` 校准（否则深色主题下重启按钮文案与当前主题不符）。

### 关键模式：线程编组（thread marshaling）

`SerialPort.DataReceived` 在后台线程上触发。`SerialPortService` 在该线程上读取字节并**攒批**（合并成较大块后一次 `Post`，降低高频数据下的 UI 积压），然后通过 `_syncContext.Post` 在 **UI 线程** 上触发 `DataReceived` —— `MainWindow` 中的事件处理器无需 `Dispatcher.BeginInvoke` 即可直接操作控件。这之所以可行，是因为服务在 UI 线程上构造（WPF 会在那里安装 `DispatcherSynchronizationContext`）。`DeviceError` 事件同样走 `Post`（并按文本节流 2 秒）。**例外**：`ConnectionChanged` 是在调用线程（`Open` / `Close` 的调用方，即 UI 线程）**同步**触发的，不经过 `Post`。任何新增的事件或异步工作都必须遵循此模式；在 UI 线程之外读取界面状态会抛出 `InvalidOperationException`（跨线程访问控件）。

### 发送

`SendBytes`（字节数组写入 `_port.BaseStream`）是服务层唯一发送入口，**返回 bool 表示是否真正写出**；文本由 UI 层 `MainWindow.SendText`（hex / 文本 / 追加换行 / 编码解析）先按当前编码转为字节后调用，且**仅在返回 true 时**累计发送字节（否则设备断开期间的丢弃会让统计虚高）。逻辑未连接时抛出 `InvalidOperationException`；逻辑已连接但物理断开（设备拔出、等待重插）时丢弃并返回 false（**并非静默**：会经 `DeviceError` 上报一次"设备未打开"提示，受全局节流保护），不抛异常（`SendText` 的返回值语义不变：false = 未发出 / 解析失败，丢弃仍返回 true，否则设备拔出期间定时发送会被误停）。`DiscardInBuffer` 调用框架 `SerialPort.DiscardInBuffer()` 真正丢弃输入缓冲区中尚未读取的数据（UI 暂未使用）。

### 自动更新

`UpdateService` 通过 GitHub Releases 检查新版本：网页端 `/releases/latest` 302 跳转解析 tag（不消耗 API 配额），确认有新版本才调 API 补全发布说明与附件地址；结果缓存 12 小时，`_checking` 用 `Interlocked` 管理并发；检查类请求 15 秒超时，下载类 10 分钟超时（HttpClient 默认 100 秒对大文件偏短）。`UpdateDialog` 确认后下载 → SHA256 校验（**校验文件缺失即拒绝更新**）→ 替换 exe → 启动新版本退出。下载任务接受 `CancellationToken`（`GetAsync` 也要传，否则响应头阶段无法取消）：**更新窗口关闭即取消**，替换程序前做最后一次取消检查。三个附件（exe 0-90% / sha256 90-95% / config 95-100%）共用一条进度条，按区间映射保证进度单调上升。取消与超时以 `token.IsCancellationRequested` 区分（`HttpClient` 超时抛的 `TaskCanceledException` 也派生自 `OperationCanceledException`）；`Process.Start` 失败单独提示"新版本已安装，但启动失败，请手动启动程序"，因为此时 exe 已被替换。检查区分手动/自动：手动检查失败才弹窗，启动时自动检查仅在状态栏提示。

## 端口命名冲突说明

根命名空间 `SerialPort` 与 `System.IO.Ports.SerialPort` 冲突。在 `SerialPort.Services` 内部，C# 会将简单名称 `SerialPort` 解析为外层命名空间，因此框架类型在 `SerialPortService.cs` 顶部以别名 `SerialPortType` 引用。不要在本代码库中声明名为 `SerialPort` 的类，也不要在 `namespace SerialPort` 内部用简单名称引用框架类型。
