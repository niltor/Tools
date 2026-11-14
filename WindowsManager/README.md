# WindowsManager

提供 C 盘清理和硬件检测的命令行工具，使用 [Spectre.Console](https://spectreconsole.net/) 展示信息和交互菜单，支持中文和英文。硬件检测按 Windows、Linux 或 macOS 调用本机命令；清理功能支持 Windows 10/11。

## 使用

安装为全局 .NET Tool：

```powershell
dotnet tool install --global Ater.Windows.Tools
windowsmanager
```

工具会读取 Windows 当前 UI 语言：中文系统使用中文界面，其它语言默认使用英文界面，不需要额外的语言参数。

一级菜单为“C盘清理”和“硬件检测”。硬件检测也可直接运行 `windowsmanager --hardware`；包含 CPU、内存、GPU/显卡、主板型号，以及系统能提供的使用率、容量、状态和驱动等指标。Linux/macOS 上缺少系统命令或权限不足时，对应项目会显示不可用。

本地运行源码：

```powershell
dotnet run --project WindowsManager
```

也可以使用命令行模式：

```powershell
# 显示帮助
windowsmanager --help

# 硬件检测
windowsmanager --hardware

# 只扫描，不修改任何内容
windowsmanager --scan

# 交互选择清理项目；每次执行前都会确认
windowsmanager --clean

# 选择默认安全项目（需要额外判断的项目会跳过），仍然需要确认
windowsmanager --clean --all

# 预览/清理当前用户的注册表使用记录
windowsmanager --registry

# 查找完全相同的重复文件，仅列表展示，不删除
windowsmanager --duplicates

# 审核长期未变化的 AppData 残留候选，可多选删除
windowsmanager --remnants

# 按软件/厂商更新暂存目录、卸载注册表路径、扩展名和命名语义扫描候选
windowsmanager --packages

# 迁移 C 盘用户目录、开发工具目录和已确认可配置的软件数据
windowsmanager --migration

# 分析 C 盘非系统目录中的大文件（超过 100 MB，最多显示 50 条，可多选永久删除）
windowsmanager --large-files

# 任意支持清理的命令都可以加 --dry-run 只预览
windowsmanager --clean --dry-run
windowsmanager --packages --dry-run
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
- Notion 渲染缓存、VLC 封面/缩略图，以及 Notion/Dropbox/Spotify 超过 30 天的诊断日志（手动选择）；保留离线页面、音乐、同步数据和播放列表
- Steam（含注册表/VDF 发现的自定义库）、Epic、Battle.net、GOG、EA、Riot、Ubisoft、Rockstar 等启动器明确标记的网页/着色器缓存（默认需手动选择）
- VS Code、npm、pip/uv、NuGet、Yarn、pnpm、Gradle、JetBrains、Cargo 下载归档、Go、Conda、Scoop/Chocolatey 等开发工具缓存，按工具分别选择（默认需手动选择）
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
- C 盘大文件分析扫描系统盘中非 Windows、Program Files、ProgramData、回收站及其他系统目录的文件，只列出超过 100 MB 的文件，按大小从大到小最多显示 50 条。可多选永久删除；删除前会再次检查路径和大小，并显示明确的 y/n 风险确认。结果可能因权限不足或文件变化而不完整。
- Windows 更新、系统目录通常需要管理员权限；非管理员运行时无法访问的文件会被跳过。

## 开发缓存清理

主菜单将“项目构建产物清理”和“包下载/依赖缓存清理”分开。日常使用前者，只清构建的中间产物和最终输出；包缓存独立识别、列项和统计，不会混入项目清理。IDE 缓存仍在普通应用清理中。

```powershell
# 只预览项目产物及 Go 编译缓存，保留下载包
windowsmanager --developer --dry-run

# 只有需要释放包缓存空间时，单独预览；可能需要重新下载
windowsmanager --developer-packages --dry-run

# 在指定目录发现项目；可以重复传入多个开发根目录
windowsmanager --developer --project-root C:\codes --project-root C:\work --dry-run

# 显示预览后多选，并确认清理
windowsmanager --developer --project-root C:\codes
```

未指定根目录时，扫描系统盘的 `codes`（例如 `C:\codes`）、当前工作目录及当前用户的 `source\repos`、`repos`、`projects`、`go\src`。扫描最多向下 8 层，跳过 `.git`、依赖、虚拟环境和已识别输出目录。自定义工作区可用 `--project-root`。

先按项目标识和目录结构定位候选，再只统计已命中的目录。项目产物、包缓存和普通缓存均在选择前统计符合规则的文件数和大小；空目录和没有符合条件文件的位置不计入。含空间数据的扫描结果和选择列表按大小从大到小排列。

项目产物优先按聚合入口合并统计和选择。支持 .NET `.sln` / `.slnx`、npm/Yarn 和 pnpm 工作区、Cargo 工作区、Maven 多模块、Gradle 静态模块声明、`go.work` 和 uv 工作区。根据清单中声明的成员归组，未归组的项目单独显示。成员出现在多个清单时优先归入覆盖候选项目更多的清单，避免重复统计。同一组只需选择一次。

Git 工作区中，构建目录含已跟踪文件时跳过整个目录；artifacts、TestResults、coverage 等附加目录须被 Git 忽略才会纳入。清理前重新校验。超过 5 项的多选列表可直接按 A 切换全选和取消全选，再逐项调整。交互式清理直接显示选择列表；只读预览用表格展示。

清理后的文件大小合计和磁盘可用空间变化分别显示。磁盘读数反映操作前后的净变化，也可能受其他进程同时读写影响。

| 类型 | 可选清理目录 | 保留内容 |
| --- | --- | --- |
| Go | `GOCACHE` / `LocalAppData\go-build`，模块下载归档 | `GOPATH\bin`、源码、已解压模块 |
| .NET | NuGet HTTP/插件缓存、全局包；有项目文件的 `bin` / `obj` | SDK、配置、源码、自定义发布目录 |
| Rust | Cargo `registry\cache`；有 `Cargo.toml` 和 `CACHEDIR.TAG` 的 `target` | 工具链、凭据、Cargo bin、registry src、git checkout |
| Node.js | 项目入口：有 `package.json` 的 `dist`、`build`、`.next`、`.nuxt`、`.output`、`.svelte-kit`、`node_modules\.cache`、`.parcel-cache`、`.angular\cache`；包入口：npm/Yarn/pnpm/node-gyp | node_modules 依赖本体、锁文件 |
| Python | 项目入口：有 Python 标识的 `__pycache__`、pytest/mypy/ruff 缓存；有 pyproject.toml/setup.py 的 build/dist；包入口：pip/uv | venv、源码、已安装解释器 |
| Java/Kotlin | Gradle caches；有 Gradle 标识的 build/.gradle、有 pom.xml 的 target | `.m2\repository` 中可能仅本机存在的发布包、配置 |

`--developer` 包含项目输出与 Go 构建缓存；NuGet、npm/pip、Cargo 归档、Go 模块下载和 Conda 等仅在 `--developer-packages` 中出现。全局 Gradle caches 混合依赖与构建缓存，归入包缓存入口。项目最终输出可能用于直接运行或部署，应在确认可以重新构建时清理。

## 容器镜像和卷

多选后、确认前会显示预计释放合计：每张镜像只计一次独占空间，再加所选卷的占用；镜像共享层不计入保守估算，无法测量的项目会注明。清理后用容器引擎报告的前后占用差显示实际净变化。该数值受统计精度和并发活动影响，容器虚拟磁盘内部释放空间也不一定立即增加 Windows 的可用空间。

“C盘清理”子菜单包含容器镜像和卷入口。默认只列出引擎识别为 dangling、且未被运行中或已停止容器引用的无标签镜像；进入菜单时可选择同时审核有标签镜像和卷。命令行可用 `windowsmanager --containers --dry-run` 预览默认范围，或用 `windowsmanager --containers-review --dry-run` 预览扩展范围。未引用只表示当前没有关联容器，不代表数据没用或镜像不会再次使用。

支持本机 Docker 命名管道和 Podman 本地机器的回环 SSH 连接。选择前展示未引用卷的占用合计、镜像总大小和按独占层计算的预计释放量，并降序排列。只删除手动选择并确认的资源，不执行强制删除或批量 prune。

扩展审核把本次扫描到的全部未引用本地卷合成一项，显示数量和占用合计。选中后统一确认和清理，删除前重新检查引用。镜像仍可逐项选择。

支持常见缓存环境变量覆盖；不会执行项目脚本，也不读取各工具配置文件来推断自定义输出路径。所有开发项均需手动选择，`--all` 不会清理。删除前应关闭 IDE、构建进程和开发服务器；下次使用可能需要联网恢复依赖、重新编译。项目输出目录中手工存入的文件也会删除。NuGet 全局包先删除 `.nupkg.metadata`，该文件被占用时保留相应包内容，避免留下被误认为完整的包。

规则调研、来源和排除依据见 [清理规则说明](CLEANUP_RULES.md)。回归验证：`dotnet run --project WindowsManager.Tests`，仅操作测试自行创建的临时目录。

## C 盘数据迁移

迁移菜单（或 `--migration`）先扫描当前仍位于系统盘的目录，再多选项目、选择其他固定盘，展示源路径、占用大小、目标路径和配置更新方案。用户目录的目标路径取源路径相对于当前用户目录的后缀，例如 `C:\Users\alice\Documents` 会迁移到 `E:\Documents`，`C:\Users\alice\AppData\Local\npm-cache` 会迁移到 `E:\AppData\Local\npm-cache`；用户目录之外的已确认路径使用隔离回退目录。复制、校验和配置更新成功后才会清理原目录；目标目录已有内容、空间不足、路径变化或相关进程正在运行时会跳过。终端/应用环境变量的修改对新启动的进程生效。

迁移扫描目录大小时会显示当前路径和动态状态；扫描中或选择界面都可以直接按 ESC 返回上级菜单，选择列表不再显示单独的“取消”项。

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
dotnet build WindowsManager\WindowsManager.csproj --configuration Release
dotnet pack WindowsManager\WindowsManager.csproj --configuration Release
```

包 ID 为 `Ater.Windows.Tools`，全局或本地工具命令为 `windowsmanager`。NuGet 包包含此 README、MIT 许可声明、作者和 GitHub 仓库元数据。常规 `dotnet pack` 会生成可安装的框架依赖工具包；发布后可用上面的 `dotnet tool install` 命令安装。发布到 NuGet 时，使用 `dotnet nuget push <包路径> --api-key <NuGet API Key> --source https://api.nuget.org/v3/index.json`。

项目目标框架为 `net10.0`。硬件检测支持 Windows、Linux、macOS；清理功能仅支持 Windows 10/11。项目启用了 AOT 兼容性分析，并可通过 `-p:PublishAot=true` 生成 Native AOT 工具包。AOT 包需要按 RID 分别在对应操作系统构建：

```powershell
# 在 Windows 构建机上
dotnet pack WindowsManager\WindowsManager.csproj --configuration Release -r win-x64 -p:PublishAot=true
dotnet pack WindowsManager\WindowsManager.csproj --configuration Release -r win-arm64 -p:PublishAot=true

# 在 Linux 构建机上
dotnet pack WindowsManager/WindowsManager.csproj --configuration Release -r linux-x64 -p:PublishAot=true
dotnet pack WindowsManager/WindowsManager.csproj --configuration Release -r linux-arm64 -p:PublishAot=true

# 在 macOS 构建机上
dotnet pack WindowsManager/WindowsManager.csproj --configuration Release -r osx-x64 -p:PublishAot=true
dotnet pack WindowsManager/WindowsManager.csproj --configuration Release -r osx-arm64 -p:PublishAot=true

# 无专用 AOT 包的平台使用 .NET 运行时回退包
dotnet pack WindowsManager\WindowsManager.csproj --configuration Release -r any -p:PublishAot=false

# 所有运行时包先发布，最后发布顶层入口包
dotnet pack WindowsManager\WindowsManager.csproj --configuration Release -p:PublishAot=true
```

AOT 目标必须在匹配操作系统的构建环境中生成；Windows 构建机不能直接交叉编译 Linux/macOS AOT。首次发布前应安装对应平台的 Native AOT 编译依赖，并确认每个 RID 包与顶层包版本一致。先将所有 RID 包推送到 NuGet，再推送顶层包。上述多平台 AOT 命令是发布流程说明；本次没有执行 AOT 编译或发布。
