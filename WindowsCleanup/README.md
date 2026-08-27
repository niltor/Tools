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

# 查找完全相同的重复文件，仅列表展示，不删除
winclean --duplicates

# 审核长期未变化的 AppData 残留候选，可多选删除
winclean --remnants

# 按软件/厂商更新暂存目录、卸载注册表路径、扩展名和命名语义扫描候选
winclean --packages

# 迁移 C 盘用户目录、开发工具目录和已确认可配置的软件数据
winclean --migration

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
- 抖音、B 站、斗鱼、虎牙、快手、YY、TikTok、腾讯视频、优酷、爱奇艺、芒果 TV、西瓜视频等视频/直播软件缓存；OBS 只审核旧日志和崩溃报告
- Codex 的 `.codex/cache`、`.codex/tmp`，以及 Claude、ChatGPT、Cursor、Windsurf 等工具明确的缓存目录
- Acrobat、Windows Shell 等常见应用缓存
- Steam（含注册表/VDF 发现的自定义库）、Epic、Battle.net、GOG、EA、Riot、Ubisoft、Rockstar 等启动器明确标记的网页/着色器缓存（默认需手动选择）
- VS Code、npm、pip、NuGet、Yarn、pnpm、Gradle、JetBrains、Maven、Cargo、Go、Conda、Scoop/Chocolatey 等开发工具缓存（默认需手动选择）
- 当前用户的最近项目/跳转列表文件
- 当前用户崩溃转储
- 软件/厂商自有更新暂存目录，以及通过卸载注册表关联到的已安装软件更新路径中的安装包候选；按扩展名、安装/升级/驱动命名标记和目录语义筛选，至少 20 MB，逐文件审核。不会进行全盘按文件名猜测

## 安全边界

- 不删除 `Windows\Installer`、`WinSxS`、`Windows\servicing\Packages`、`DriverStore`、Visual Studio `Packages`、驱动、服务配置、页面文件、系统还原点或未知应用数据；这些路径即使被错误配置为清理根目录也会被路径保护拦截。
- 注册表只修改 `HKCU` 中的运行、最近打开/保存、搜索、路径、应用启动、兼容性助手、7-Zip/WinRAR/画图历史和可重建显示名称缓存；同时只读检查 `HKCU/HKLM` 卸载项的失效安装路径，候选不会自动删除。
- 缓存目录本身会保留，只删除其中的文件和空子目录；遇到锁定文件、重解析点或权限不足会跳过。
- `--clean --all` 只选择默认安全项；回收站、Prefetch、WebCache、旧日志和开发工具缓存等需要额外判断的项目会跳过，使用交互式 `--clean` 手动选择。
- 不自动清理 `Windows\Installer`、`WinSxS`、`Windows.old`、驱动包、浏览器历史/登录信息、Docker 数据或游戏文件；系统组件存储应使用 Windows 官方 DISM 流程。用户 Downloads 也不属于默认清理根目录。
- 不清理 `.codex` 中的会话、项目、技能、插件、附件、记忆、配置和密钥，也不清理 HuggingFace/Ollama/LM Studio 等 AI 模型目录。
- 不清理 `Windows\System32\DriverStore`、活动驱动文件、聊天数据库、聊天媒体或附件、视频下载/离线缓存；NVIDIA `Installer2` 仅作为逐项审核的安装包候选。
- 重复文件只在常用用户文件夹中查找完全相同的文件，仅列表展示，不提供删除操作。
- AppData 卸载残留候选来自 Local、Roaming、LocalLow 的一级目录；排除系统/常见软件、已安装软件注册表路径和包含可执行文件的目录。可多选删除，删除前会重新核验，并只处理达到保守时间条件的文件。
- Windows 更新、系统目录通常需要管理员权限；非管理员运行时无法访问的文件会被跳过。

## C 盘数据迁移

迁移菜单（或 `--migration`）先扫描当前仍位于系统盘的目录，再多选项目、选择其他固定盘，展示源路径、占用大小、目标路径和配置更新方案。用户目录的目标路径取源路径相对于当前用户目录的后缀，例如 `C:\Users\alice\Documents` 会迁移到 `E:\Documents`，`C:\Users\alice\AppData\Local\npm-cache` 会迁移到 `E:\AppData\Local\npm-cache`；用户目录之外的已确认路径使用隔离回退目录。复制、校验和配置更新成功后才会清理原目录；目标目录已有内容、空间不足、路径变化或相关进程正在运行时会跳过。终端/应用环境变量的修改对新启动的进程生效。

支持的迁移方案包括：

- Documents、Downloads、Music、Videos：更新 Windows `User Shell Folders`/`Shell Folders`，并调用 `SHSetKnownFolderPath`。
- NuGet：迁移 global packages、HTTP cache、plugins cache，分别设置 `NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH`、`NUGET_PLUGINS_CACHE_PATH`。
- npm：迁移 cache，设置 `npm_config_cache`；Yarn：迁移全局缓存，设置 `YARN_CACHE_FOLDER`，不自动改写项目 `.yarnrc.yml`。
- pnpm：通过 `pnpm store path` 发现目录，迁移后执行 `pnpm config set store-dir <新路径> --global`。
- pip、uv：分别设置 `PIP_CACHE_DIR`、`UV_CACHE_DIR`；uv 按完整缓存目录迁移，不拆分缓存条目。
- Go、Dart/Flutter：迁移 `GOMODCACHE`、`GOCACHE`、`PUB_CACHE` 指向的目录。
- Poetry、Composer、vcpkg：分别迁移并设置 `POETRY_CACHE_DIR`、`COMPOSER_CACHE_DIR`、`VCPKG_DEFAULT_BINARY_CACHE`；不迁移项目虚拟环境、`vendor` 或源码树。
- Cargo、Gradle：仅在用户已显式设置 `CARGO_HOME`/`GRADLE_USER_HOME` 时提供完整用户目录迁移，因为其中可能包含配置、脚本和凭据。
- 微信：仅当检测到微信注册表中的 `FileSavePath` 且路径存在时提供迁移，关闭微信后复制完整数据并更新该设置。
- Codex CLI：仅当用户已显式设置 `CODEX_HOME` 时提供；不会猜测桌面 Codex 应用的内部路径。Hugging Face 使用 `HF_HOME`，Ollama 使用 `OLLAMA_MODELS`。

QQ、Discord、Slack、Teams、ChatGPT/Claude Desktop、Cursor、Windsurf 等软件如果没有确认的用户数据路径配置，不会被自动列为迁移目标；清理功能也不会删除其聊天数据库、媒体、会话、AI 模型或密钥。

## 构建

```powershell
dotnet build WindowsCleanup\WindowsCleanup.csproj --configuration Release
dotnet pack WindowsCleanup\WindowsCleanup.csproj --configuration Release
```

项目目标框架为 `net10.0`，程序运行时仅支持 Windows 10/11。打包后生成的 NuGet 包命令名为 `winclean`。
