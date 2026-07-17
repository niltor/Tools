# Esafenet 文件恢复工具

本工具根据一组“原文件 + 加密文件”恢复重复异或流，再用它还原同一批次/同一密钥生成的 Esafenet 容器。当前样本的周期为 512 字节（十六进制 `0x200`）。

## 样本分析

三个加密文件都有相同的 4096 字节容器头，文件数据从偏移 4096（`0x1000`）开始。

| 十进制偏移 | 十六进制偏移 | 长度 | 十六进制内容 | 十进制/含义 |
|---:|---:|---:|---|---|
| 0 | `0x0000` | 8 | `E0 A8 91 E7 D8 F2 05 AC` | 魔数；十进制为 `224 168 145 231 216 242 5 172` |
| 8 | `0x0008` | 4 | `00 02 00 00` | 版本或标志字段 |
| 12 | `0x000C` | 4 | `00 10 00 00` | 小端整数 `4096`，即容器头长度 |
| 16 | `0x0010` | 8 | 见下表 | 小端 UInt64，原文件长度 |
| 88 | `0x0058` | 8 | `45 73 61 66 65 6E 65 74` | ASCII `Esafenet`；十进制为 `69 115 97 102 101 110 101 116` |
| 4096 | `0x1000` | 可变 | 加密负载 | 与原文件等长 |

原文件长度字段及总长度：

| 文件 | 长度字段（小端十六进制） | 原长度（十进制） | 容器总长度 |
|---|---|---:|---:|
| `origin_encrypt.cs` | `31 00 00 00 00 00 00 00` | 49 | 4,145 |
| `large_encrypt.cs` | `31 38 02 00 00 00 00 00` | 145,457 | 149,553 |
| `test.pdf` | `B7 0B 31 00 00 00 00 00` | 3,214,263 | 3,218,359 |

所有容器均满足：`总长度 = 4096 + 原长度`。

## 恢复原理

已知明文与对应密文负载满足：

```text
key[i] = plain[i] XOR encrypted[4096 + i]
plain[i] = encrypted[4096 + i] XOR key[i mod 512]
```

`origin` 样本的 49 个字节与 `large` 样本导出的流完全一致。`large.cs` 比容器头记录的原长度少 8 字节，并且已知对中约四分之一的位置不一致，因此程序不是直接取单个块，而是对每个周期位置的候选字节进行多数表决。当前 512 个位置的最低置信度为 71.83%，平均置信度为 75.10%。

恢复出的前 49 个密钥字节为：

```text
7C 95 E6 43 86 7F 75 6F 33 35 0C 66 ED 0C B5 A0
8A 31 B0 78 52 29 51 5B D8 28 B8 30 DF DD BD 24
7F 3C B4 F0 DD 88 E2 CA A5 39 96 5E B6 BD 2B 5D 7B
```

## 编译和使用

需要 .NET 10 SDK：

```powershell
dotnet build .\decrypt\EsafenetRecovery\EsafenetRecovery.csproj -c Release
```

## 便捷解密模式

程序已经内置从当前已知样本恢复出的 512 字节密钥。对于使用同一密钥生成的 Esafenet 容器，只需要传入一个文件路径或目录路径。

### 单个文件

```powershell
EsafenetRecovery.exe "C:\Docs\encrypted.pdf"
```

输出到输入文件所在目录，默认在扩展名前增加 `_decrypted`：

```text
C:\Docs\encrypted_decrypted.pdf
```

### 整个目录

```powershell
EsafenetRecovery.exe "C:\Docs\EncryptedFiles"
```

程序扫描该目录中的所有普通文件，只解密具有 Esafenet 容器头的文件，普通文件会被跳过。默认不扫描子目录。

扫描所有子目录：

```powershell
EsafenetRecovery.exe "C:\Docs\EncryptedFiles" --recursive
```

自定义输出后缀：

```powershell
EsafenetRecovery.exe "C:\Docs\EncryptedFiles" --suffix "_restored"
```

明确覆盖已存在的输出文件：

```powershell
EsafenetRecovery.exe "C:\Docs\EncryptedFiles" --overwrite
```

也可以显式写出 `decrypt` 命令，两种写法等价：

```powershell
EsafenetRecovery.exe decrypt "C:\Docs\EncryptedFiles"
```

### 安全行为

- 不修改或删除输入文件。
- 默认拒绝覆盖单文件模式的现有输出。
- 目录模式默认跳过已有输出，并在结尾打印统计。
- 先写入同目录临时文件，完整写入磁盘后再原子重命名；失败时删除临时文件。
- 使用流式解密，不需要把整个待解密文件载入内存。
- 内置密钥只适用于与当前样本使用相同密钥/算法的 Esafenet 文件；其他批次若密钥不同，需要使用下面的已知文件对恢复模式。

完整便捷模式参数：

```text
EsafenetRecovery <文件或目录> [--suffix _decrypted] [--recursive] [--overwrite]
EsafenetRecovery decrypt <文件或目录> [--suffix _decrypted] [--recursive] [--overwrite]
```

## 已知文件对恢复模式

用 `large` 已知对恢复测试 PDF：

```powershell
dotnet run --project .\decrypt\EsafenetRecovery\EsafenetRecovery.csproj -c Release -- `
  recover .\decrypt\large.cs .\decrypt\large_encrypt.cs `
  .\decrypt\test.pdf .\output\pdf\test_restored.pdf
```

完整参数：

```text
EsafenetRecovery recover <已知原文件> <对应加密文件> <待恢复加密文件> <输出文件> [周期]
```

周期省略时默认为 512。为避免误操作，输出路径已存在时程序会拒绝覆盖。

## 发布 Windows 单文件程序

```powershell
dotnet publish .\decrypt\EsafenetRecovery\EsafenetRecovery.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None
```

发布结果位于：

```text
decrypt\EsafenetRecovery\bin\Release\net10.0\win-x64\publish\EsafenetRecovery.exe
```

本项目当前已经把验证后的发布文件复制为更便于使用的：

```text
decrypt\EsafenetRecovery.exe
```

## 本次验证

- 输出长度：3,214,263 字节，与容器头记录一致。
- PDF 版本：1.7；页数：45；未加 PDF 密码。
- `pypdf` 可提取首页正文。
- Poppler 可解析文件，并成功渲染第 1、23、45 页。
- SHA-256：`09CEC188892289CF507CCA7DDA6B6FD5D290A303248C109050E5D34BB9ACF5FD`
