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

- **`SerialPort`** —— `MainWindow`（XAML + 代码隐藏）。窗口为垂直分割布局：上半部分为接收显示区（只读 TextBox，带浮动清除按钮），下半部分为端口配置区（6 个下拉框：端口 / 波特率 / 数据位 / 停止位 / 校验位 / 握手协议）、复选框（十六进制发送 / 追加换行 / 十六进制显示 / 自动滚动）、连接开关按钮（绿色=未连接，红色=已连接）、发送区以及状态栏。它持有唯一的 `SerialPortService` 实例，在构造函数中订阅其事件，并通过私有解析器（`ParseStopBits`、`ParseParity`、`ParseHandshake`、`HexStringToBytes`）将界面字符串转换为串口配置。接收到的数据通过 `txtReceive.AppendText` 增量追加（WPF 中整体重新赋值会很慢），勾选自动滚动时用 `ScrollToEnd` 自动滚动到底部。

- **`SerialPort.Services`** —— `SerialPortService`，一个 `sealed` 类，封装 `System.IO.Ports.SerialPort`（通过 `SerialPortType` 别名引用——根命名空间 `SerialPort` 遮蔽了框架类型）。界面所需的全部内容都在这一个文件里：服务本身、`SerialPortConfig`、`BaudRate` / `DataBits` 枚举以及 `DataReceivedEventArgs`。它实现 `IDisposable`，并在构造函数中捕获 `SynchronizationContext.Current`。

- **`SerialPort.UI`** —— `ThemeManager`（静态类）和 `ThemeMode` 枚举。通过将 `Application.Current.Resources.MergedDictionaries` 的第 0 项替换为 `Themes/LightTheme.xaml` 或 `Themes/DarkTheme.xaml` 来切换浅色 / 深色主题；所有主题颜色都通过 `DynamicResource` 引用，因此替换后会立即重新着色。强调色（`PortOpenBrush` / `PortCloseBrush` / `SendBtnBrush`）是 App.xaml 中的常量，不随主题变化。主题不持久化——应用始终以浅色启动。

### 关键模式：线程编组（thread marshaling）

`SerialPort.DataReceived` 在后台线程上触发。`SerialPortService` 在该线程上读取字节，然后通过 `_syncContext.Post` 在 **UI 线程** 上触发 `DataReceived` / `ConnectionChanged` —— `MainWindow` 中的事件处理器无需 `Dispatcher.BeginInvoke` 即可直接操作控件。这之所以可行，是因为服务在 UI 线程上构造（WPF 会在那里安装 `DispatcherSynchronizationContext`）。任何新增的事件或异步工作都必须遵循此模式；在 UI 线程之外读取界面状态会抛出 `InvalidOperationException`（跨线程访问控件）。

### 发送

`SendText`（字符串 → 按调用方编码转为字节，界面中为 UTF-8）和 `SendBytes`（原始十六进制）都写入 `_port.BaseStream`，端口关闭时抛出 `InvalidOperationException`。`DiscardInBuffer` 目前只刷新流——注意它实际上并没有丢弃已接收的缓冲数据。

## 端口命名冲突说明

根命名空间 `SerialPort` 与 `System.IO.Ports.SerialPort` 冲突。在 `SerialPort.Services` 内部，C# 会将简单名称 `SerialPort` 解析为外层命名空间，因此框架类型在 `SerialPortService.cs` 顶部以别名 `SerialPortType` 引用。不要在本代码库中声明名为 `SerialPort` 的类，也不要在 `namespace SerialPort` 内部用简单名称引用框架类型。
