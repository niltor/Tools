# EsafenetRecovery

用于恢复 Esafenet 加密容器中的原始文件。

支持两种模式：

- 便捷解密：使用程序内置的 512 字节密钥。
- 已知文件对恢复：根据“原文件 + 对应加密文件”恢复周期性 XOR 密钥。

## 环境与构建

需要 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet build .\EsafenetRecovery\EsafenetRecovery.csproj -c Release
```

## 便捷解密

在 `EsafenetRecovery.exe` 所在目录执行：

```powershell
# 单个文件，输出为 encrypted_decrypted.pdf
.\EsafenetRecovery.exe "C:\Docs\encrypted.pdf"

# 目录，默认只扫描当前目录
.\EsafenetRecovery.exe "C:\Docs\EncryptedFiles"

# 扫描子目录并指定后缀
.\EsafenetRecovery.exe "C:\Docs\EncryptedFiles" --recursive --suffix "_restored"

# 允许覆盖已有输出文件
.\EsafenetRecovery.exe "C:\Docs\encrypted.pdf" --overwrite
```

`decrypt` 命令名可以省略，也可以显式写出：

```powershell
.\EsafenetRecovery.exe decrypt "C:\Docs\EncryptedFiles"
```

参数：

```text
EsafenetRecovery.exe <文件或目录>
  [--recursive|-r]
  [--suffix <后缀>]
  [--overwrite]
```

目录模式会跳过不支持的文件和已存在的输出文件，并在最后打印统计信息。

## 已知文件对恢复

当目标文件使用了不同批次的密钥时，使用一组对应的原文件和加密文件：

```powershell
dotnet run --project .\EsafenetRecovery\EsafenetRecovery.csproj -c Release -- `
  recover <已知原文件> <对应加密文件> `
  <待恢复加密文件> <输出文件> [周期]
```

例如：

```powershell
.\EsafenetRecovery.exe recover `
  .\known\plain.bin .\known\encrypted.bin `
  .\input\target.enc .\output\target.bin
```

周期默认是 `512`。程序会对每个周期位置进行多数表决；输出文件已存在时会拒绝覆盖。

## 发布

### 普通 Windows 单文件

```powershell
dotnet publish .\EsafenetRecovery\EsafenetRecovery.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=None
```

### Windows x64 Native AOT

Native AOT 会在发布时将程序编译为原生代码，不需要目标机器安装 .NET 运行时。AOT 发布与操作系统和 CPU 架构绑定，必须显式指定 RID：

```powershell
dotnet publish .\EsafenetRecovery\EsafenetRecovery.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:TreatWarningsAsErrors=true
```

输出文件：

```text
EsafenetRecovery\bin\Release\net10.0\win-x64\publish\EsafenetRecovery.exe
```

当前项目已验证 `win-x64` Native AOT 发布。发布 AOT 时会启用裁剪；如果以后加入反射、动态程序集加载、运行时序列化或插件机制，需要额外处理兼容性警告。详见 [Microsoft Native AOT 文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。

发布其他架构时替换 RID，例如 `win-arm64`，并确保本机安装了对应的 C++ 链接器工具链。

## 容器与安全注意事项

- 容器头长度通常为 `4096` 字节，负载从偏移 `0x1000` 开始。
- 原始文件长度位于偏移 `0x10`，是小端 `UInt64`。
- 程序不修改输入文件，先写入临时文件，再原子移动到输出路径。
- 内置密钥只适用于相同批次/算法的文件；不同密钥请使用已知文件对恢复模式。
- 密钥硬编码在程序中，Native AOT 不能将其视为安全保密方案。
