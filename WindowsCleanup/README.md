# WindowsCleanup

面向 Windows 10/11 的安全清理命令行工具，使用 [Spectre.Console](https://spectreconsole.net/) 提供表格、进度和交互式确认，支持中文和英文。

## 使用

安装为全局 .NET Tool：

```powershell
dotnet tool install --global winclean
winclean
```

工具会读取 Windows 当前 UI 语言：中文系统使用中文界面，其它语言默认使用英文界面，不需要额外的语言参数。

本地运行源码：

```powershell
dotnet run --project WindowsCleanup
```

也可以使用命令行模式：

```powershell
# 显示帮助
winclean --help

# 只扫描，不修改任何内容
winclean --scan

# 交互选择清理项目；每次执行前都会确认
winclean --clean

# 选择默认安全项目（需要额外判断的项目会跳过），仍然需要确认
winclean --clean --all

# 预览/清理当前用户的注册表使用记录
winclean --registry

# 查找完全相同的重复文件，并逐组确认是否删除副本
winclean --duplicates

# 仅显示长期未变化的 AppData 残留候选
winclean --remnants

# 扫描旧安装包/升级包候选；删除时逐文件选择并确认
winclean --packages

# 任意支持清理的命令都可以加 --dry-run 只预览
winclean --clean --dry-run
winclean --packages --dry-run
```

## 清理范围

默认目录由程序按当前用户和当前 Windows 安装动态计算，包含：

- 用户和 Windows 临时文件
- Windows Update 下载缓存和 Delivery Optimization 缓存
- C: 回收站
- Windows 错误报告队列，以及超过 30 天的 CBS/DISM 诊断日志
- Windows 网络缓存、缩略图和图形着色器缓存
- NVIDIA/AMD/Intel 图形驱动着色器缓存；驱动安装包只进入人工审核候选
- Chrome、Edge、Brave、Vivaldi、Opera、Firefox、Yandex 缓存及 Chromium GPU/扩展缓存
- Teams、Discord、Slack、Signal、Skype、Zoom、微信、QQ 等聊天/通讯软件缓存；旧日志单独按 30 天规则审核
- 抖音、B 站、斗鱼、虎牙、快手、YY、TikTok 等视频/直播软件缓存；OBS 只审核旧日志和崩溃报告
- Codex 的 `.codex/cache`、`.codex/tmp`，以及 Claude、ChatGPT、Cursor、Windsurf 等工具明确的缓存目录
- Acrobat、Windows Shell 等常见应用缓存
- Steam、Epic、Battle.net、GOG、EA 等启动器明确标记的网页/着色器缓存（默认需手动选择）
- VS Code、npm、pip、NuGet、Yarn、Gradle、JetBrains 等开发工具缓存（默认需手动选择）
- 当前用户的最近项目/跳转列表文件
- 当前用户崩溃转储
- Downloads 和常见 AMD/Intel/NVIDIA 暂存目录中超过 30 天、至少 20 MB 的安装包候选（逐文件审核）

## 安全边界

- 不删除 `Windows\Installer`、`WinSxS`、驱动、服务配置、页面文件、系统还原点或未知应用数据。
- 注册表只修改 `HKCU` 中的运行、最近打开、搜索、路径、应用启动、兼容性助手历史和可重建显示名称缓存；同时只读检查 `HKCU/HKLM` 卸载项的失效安装路径，候选不会自动删除。
- 缓存目录本身会保留，只删除其中的文件和空子目录；遇到锁定文件、重解析点或权限不足会跳过。
- `--clean --all` 只选择默认安全项；回收站、Prefetch、WebCache、旧日志和开发工具缓存等需要额外判断的项目会跳过，使用交互式 `--clean` 手动选择。
- 不自动清理 `Windows\Installer`、`WinSxS`、`Windows.old`、驱动包、浏览器历史/登录信息、Docker 数据或游戏文件；系统组件存储应使用 Windows 官方 DISM 流程。
- 不清理 `.codex` 中的会话、项目、技能、插件、附件、记忆、配置和密钥，也不清理 HuggingFace/Ollama/LM Studio 等 AI 模型目录。
- 不清理 `Windows\System32\DriverStore`、活动驱动文件、聊天数据库、聊天媒体或附件、视频下载/离线缓存；NVIDIA `Installer2` 仅作为逐项审核的安装包候选。
- 重复文件只在常用用户文件夹中查找完全相同的文件，默认保留每组排序后的第一份，并逐组询问。
- AppData 卸载残留无法被 Windows 可靠识别，工具只列出保守候选，不自动删除。
- Windows 更新、系统目录通常需要管理员权限；非管理员运行时无法访问的文件会被跳过。

## 构建

```powershell
dotnet build WindowsCleanup\WindowsCleanup.csproj --configuration Release
dotnet pack WindowsCleanup\WindowsCleanup.csproj --configuration Release
```

项目目标框架为 `net10.0`，程序运行时仅支持 Windows 10/11。打包后生成的 NuGet 包命令名为 `winclean`。
