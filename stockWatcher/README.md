# stockWatcher

`stockWatcher` 是一个基于 .NET 10 Native AOT 的命令行自选股工具，支持 A 股（上海、深圳、北京）和美股主要交易所股票。

## 命令

```bash
# 搜索全部支持的市场，输入结果序号加入自选；相同代码更新名称等信息
stockWatcher search 600519
stockWatcher search Apple
stockWatcher search AAPL

# 打开配置菜单：行情刷新间隔（1～10 秒）、默认灰色背景
stockWatcher config

# 拉取一次行情；按配置间隔持续刷新，按 Ctrl+C 静默结束
stockWatcher list
stockWatcher show
stockWatcher show --interval 5
# 关闭默认灰色背景
stockWatcher show --no-mask

# 从自选删除 Yahoo Finance 代码
stockWatcher remove AAPL
```

搜索结果选择后会写入当前用户的数据目录：

- Windows：`%LOCALAPPDATA%\stockWatcher\watchlist.json`
- macOS：`~/Library/Application Support/stockWatcher/watchlist.json`
- Linux：`~/.config/stockWatcher/watchlist.json`

`search` 统一搜索全部支持的市场，选择结果后按股票代码匹配自选（忽略代码大小写）；已有代码会更新名称和市场信息，不重复添加。

`config` 打开配置菜单，修改后立即保存到与自选文件同目录的 `settings.json`，下次启动 `show` 时生效。行情刷新间隔范围为 1～10 秒，初始为 3 秒；也可设置是否默认开启灰色背景。`show --interval 5` 只覆盖本次运行的刷新间隔，`--no-mask` 只覆盖本次运行的背景设置，不修改保存的配置。

`list` 显示一次行情快照；`show` 按配置间隔刷新 `code/name/price/limit/start`，分别代表股票代码、名称、当前价、相对前收的涨跌幅、当日开盘价。列表按涨跌幅从高到低排序，缺失值放最后，相同涨跌幅保持原顺序。各列默认使用深灰色；仅按住 Alt 时，`limit` 上涨显示红色、下跌显示绿色，平盘或缺失仍为深灰色。按住 Alt 期间仍持续刷新行情；界面不显示 Alt 操作提示，结束时不显示停止提示。输出重定向到文件或管道时使用纯文本。

Windows 下 `show` 默认给所有行使用灰色背景，按住 Alt 去除灰色背景、恢复终端原来的背景并显示涨跌颜色，松开后恢复灰色背景及灰色文字。切换会立即重绘已缓存的行情，不需要等待下一次网络刷新；退出时恢复原背景。此效果直接使用终端字符背景色，无需修改窗口透明度或标题。`--no-mask` 只关闭背景遮罩，仍可按住 Alt 显示涨跌颜色。重定向输出保持纯文本；非 Windows 平台不启用背景遮罩或涨跌颜色，因为无法可靠检测 Alt 松开。

## Native AOT 发布

在具备对应平台 Native AOT 编译依赖的环境中发布：

```bash
dotnet publish stockWatcher/stockWatcher.csproj -c Release -r win-x64
dotnet publish stockWatcher/stockWatcher.csproj -c Release -r linux-x64
dotnet publish stockWatcher/stockWatcher.csproj -c Release -r osx-arm64
```

程序使用微软维护的 `System.CommandLine` 解析命令行参数，并为行情和自选 JSON 使用 source generation，避免依赖运行时反射序列化。

英文名称与美股代码搜索优先使用 Yahoo Finance 搜索接口；A 股代码及中文名称优先使用东方财富建议接口，以显示中文股票名称；A 股代码的中文名称查询失败时回退到 Yahoo 搜索。上海、深圳 A 股和美股行情使用 Yahoo Finance 批量接口；北交所行情回退到东方财富。重新选择已存在的股票会更新其名称和市场信息，不会重复添加。`show` 会按每批最多 20 只股票请求行情，按配置间隔刷新。

Yahoo Finance 和东方财富的这些 JSON 接口均未公开承诺作为稳定的公共 API；接口变更或限流时，搜索/行情可能暂时不可用。实时行情也可能存在延迟。
