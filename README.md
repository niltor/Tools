# Tools

用于收集一些常用的小工具。

## 结构

- `Tools.slnx`：解决方案文件
- `PortScan/`：端口扫描命令行工具
- `WindowsCleanup/`：Windows 10/11 安全清理命令行工具

## PortScan

扫描目标主机的 TCP 端口，并在端口可连接时进一步检测是否为 Web 服务。

### 特性

- 按 CPU 核心数进行并发扫描
- 默认 20 个线程，可通过参数调整为 10~100
- 单个端口默认超时 0.3 秒
- 可通过第 4 个参数覆盖超时时间（秒）
- 对可连接端口做 HTTP / HTTPS 探测
- 输出开放端口及 Web 相关信息

### 使用

```bash
dotnet run --project PortScan -- <host> [startPort] [endPort] [timeoutSeconds] [threads]
```

示例：

```bash
dotnet run --project PortScan -- 192.168.1.10
dotnet run --project PortScan -- 192.168.1.10 1 1024
dotnet run --project PortScan -- 192.168.1.10 1 1024 0.5
dotnet run --project PortScan -- 192.168.1.10 1 1024 0.5 50
```

### 实现选择

这里选择直接用 `.NET` 的 `TcpClient` / `HttpClient` 自己实现，而不是调用 `telnet`：

- 启动外部进程的开销更小，整体通常更快
- 更容易做并发控制和超时管理
- 更容易直接拿到结构化结果，便于后续输出、记录或扩展
- 不依赖系统是否安装 `telnet`

如果只是人工临时连一下，`telnet` 当然也能用；但做成工具的话，自己实现更稳，也更像个正经工具，少一点“看天吃饭”的味道。

## WindowsCleanup

使用 Spectre.Console 提供交互式清理界面，支持中文和英文：

```bash
dotnet pack WindowsCleanup/WindowsCleanup.csproj --configuration Release
dotnet tool install --global winclean
winclean
```

工具会根据 Windows 当前 UI 语言自动选择中文或英文界面。

命令行模式：

```bash
winclean --scan
winclean --clean
winclean --clean --all
winclean --registry
winclean --duplicates
winclean --remnants
winclean --packages
winclean --migration
```

所有删除操作都需要交互确认；`--scan` 和 `--dry-run` 只预览，不删除文件。`--clean --all` 只选择默认安全项目，交互式 `--clean` 可手动选择需确认项目。工具只清理可重新生成的临时文件/缓存、C: 回收站和当前用户的使用记录；安装包与卸载残留均采用人工审核，重复文件只列表展示。`--migration` 会在单独确认后把选中的用户目录、开发工具目录或已确认可配置的软件数据迁移到其他固定盘，并更新对应配置。详见 [`WindowsCleanup/README.md`](WindowsCleanup/README.md)。
