# 清理规则调研与取舍

本次参考 [BleachBit](https://github.com/bleachbit/bleachbit) 的应用规则与预览方式，以及 [Winapp2](https://github.com/MoscaDotTo/Winapp2) 的显式路径规则库。独立实现目录识别，不直接导入其规则文件或执行外部清理程序。

## 目录优先

开发清理按用途分开：`--developer` 只包含项目构建中间产物、最终输出与 Go 编译缓存；`--developer-packages` 单独提供下载包/依赖清理，二者不混合统计。npm、pip、NuGet、Cargo/Go 下载、Conda 以及混有依赖的全局 Gradle caches 均不进入日常项目清理。IDE 缓存归普通应用清理。Node 项目按 package.json 定位 dist/build/.next 等约定输出，Python 包项目按 pyproject.toml/setup.py 定位 build/dist；依赖本体与锁文件保留。

1. 应用缓存由当前用户的 LocalAppData、Roaming、用户目录及受约束的环境变量定位。
2. 已知应用目录只探测明确的缓存子目录，不把所有叫 Cache 的目录泛化为可删除数据。
3. 项目构建产物由项目文件与约定子目录共同识别；发现阶段不读取文件内容，不下降到依赖目录和已识别的输出目录。
4. 普通缓存、项目产物和包缓存只统计命中目录内符合规则的文件，预览按大小降序并排除没有匹配文件的候选。删除复用路径保护与锁定文件处理。普通删除使用流式枚举；NuGet 为保证元数据删除顺序单独排序。回收站通过系统接口查询。

## 来源与决策

| 来源 | 采用的事实与边界 |
| --- | --- |
| [BleachBit Discord 规则](https://github.com/bleachbit/bleachbit/blob/master/cleaners/discord.xml) | Stable/PTB/Canary/Development 的 Cache、Code Cache、GPUCache 目录；不采用其 cookies、history、session 清理。缓存清理也可能影响收藏 GIF，应手动选择。 |
| [Winapp2 目录规则](https://github.com/MoscaDotTo/Winapp2/blob/master/Winapp2.ini) | 补充 Notion 明确的渲染缓存、VLC art/videoThumbs 和 Notion/Dropbox/Spotify 旧诊断日志。没有采用删除整个 Spotify LocalCache、Dropbox 同步临时目录或 Telegram 媒体缓存的宽泛规则。 |
| [BleachBit 文件定位说明](https://docs.bleachbit.org/cml/finding-files-to-delete/index.html) | 按应用配置目录定位，先预览；区分缓存与用户数据。 |
| [NuGet 缓存管理](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders) | HTTP 缓存与全局包分别列项；全局包删除须先去除 .nupkg.metadata，后续需 restore。 |
| [Go modules](https://go.dev/ref/mod) | 下载缓存与源码、工具二进制区分；只处理下载归档及构建缓存。 |
| [Cargo home](https://doc.rust-lang.org/cargo/guide/cargo-home.html) | registry/cache 为下载归档；保留凭据、配置与工具，避免清理用户可能改动过的源码 checkout。 |
| [npm cache](https://docs.npmjs.com/cli/v8/commands/npm-cache/) | npm 缓存可恢复；不把整个 npm prefix 当缓存。 |
| [pip caching](https://pip.pypa.io/en/stable/topics/caching/) | pip 下载/构建缓存与实际虚拟环境分开。 |
| [Gradle 目录布局](https://docs.gradle.org/current/userguide/directory_layout.html) | 用户 caches、项目 build/.gradle 与配置、工具安装分开。 |

浏览器的 Service Worker CacheStorage 和 Application Cache 可能保存离线网页，已从默认安全浏览器项拆成手动选择项；浏览器根目录下的共享着色器缓存单独识别。

用户个人目录不是整体清理对象：保留文档、桌面、下载、聊天附件/数据库、密码、登录状态、SSH 密钥、版本库、AI 会话/模型、软件配置。开发工作区中的约定输出仍可能包含手工文件，必须先检查显示的路径再选择。Maven 本地仓库可能包含不能重新下载的本地发布包，不再按文件年龄批量删除。自定义构建输出、Docker/WSL 虚拟磁盘和系统组件存储不在此次目录规则中。

这里的“可再生成”不等于无成本：可能增加首次启动时间、重新编译和联网下载，并影响离线使用。
