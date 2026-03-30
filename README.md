# Tools

用于收集一些常用的小工具。

## 结构

- `Tools.slnx`：解决方案文件
- `PortScan/`：端口扫描命令行工具

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
