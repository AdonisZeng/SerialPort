# SerialPort 串口调试工具

WPF 串口调试助手（由 WinForms 版本 SerialAdonis 移植）。可列出可用 COM 端口，以可配置的波特率 / 数据位 / 停止位 / 校验位 / 握手协议打开端口，发送文本或十六进制字节，以文本或十六进制格式显示接收数据，支持自动滚动、接收区搜索 / 筛选、接收数据文件保存，以及通过 GitHub Releases 检查并自动更新。

目标框架：**.NET Framework 4.8.1**（Windows 10 / 11 自带），无第三方依赖，单 exe 绿色运行。

## 功能

- 串口自动检测（1.5 秒轮询，支持热插拔提示）
- 波特率 / 数据位 / 停止位 / 校验位 / 流控可配置
- 文本 / 十六进制发送，追加换行符
- 文本 / 十六进制显示，自动滚动
- 接收区搜索、按行筛选
- 接收数据保存为文件，可自定义保存路径与文件名
- 浅色 / 深色主题切换
- 自动检查更新（GitHub Releases），应用内下载并重启升级

## 构建

需要 Visual Studio（本项目使用 VS 2026）。旧式 WPF 工程的标记编译依赖 VS 安装的 PresentationBuildTasks，`dotnet build` 无法构建，请使用 VS 的 MSBuild.exe：

```bash
"/d/Software/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" SerialPort.csproj /p:Configuration=Release
```

产物位于 `bin\Release\SerialPort.exe`。

## 发布新版本

1. **改版本号**：修改 `SerialPort.csproj` 根 PropertyGroup 中的 `<Version>`（如 `1.0.1`）。程序集版本号由构建自动生成，无需改其他文件。
2. **构建**：`MSBuild.exe SerialPort.csproj /p:Configuration=Release`。
3. **打 tag 并推送**：
   ```bash
   git add -A
   git commit -m "release: v1.0.1"
   git tag v1.0.1
   git push && git push --tags
   ```
4. **创建 GitHub Release**：在仓库 Releases 页面新建 Release，选择 `v1.0.1` tag，填写更新说明（会显示在应用内更新弹窗中），上传以下附件：
   - `bin\Release\SerialPort.exe`
   - `bin\Release\SerialPort.exe.config`
   - `SerialPort.exe.sha256`（用 `certutil -hashfile bin\Release\SerialPort.exe SHA256` 生成，输出内容第一段即 64 位校验值）

> 应用下载后会用 `.sha256` 文件校验 `SerialPort.exe`，校验失败会中止更新，请务必同时上传两个附件。

## 更新机制

- 应用启动时在后台检查一次 GitHub 最新 Release；状态栏右侧"检查更新"按钮可手动检查。
- 发现新版本时弹出更新窗口（显示版本对比与更新说明），确认后自动下载、替换并重启。
- 更新信息来自公开仓库的 `releases/latest` API。仓库与所有者定义在 `Services\UpdateService.cs` 顶部的 `GitHubOwner` / `GitHubRepo` 常量中。

## 许可证

[MIT](LICENSE)
