# 构建与测试

从仓库根目录执行。Windows 使用 .NET Framework 4.5 目标和项目锁定的 Roslyn 编译器；运行系统需有兼容 .NET Framework。Android 使用 `portable/toolchain.lock.json` 中的 SDK/JDK，以及 csproj 中固定的依赖版本。首次安装需要网络下载依赖。

## Windows

```powershell
.\setup-build.ps1
.\build.ps1 -Test
```

该命令构建解释器并运行真实 WinForms/解释器的回归测试。测试使用中性演示和受控回复，不调用个人 API。测试会创建 `tests/game`、`tests/bin`、`logs` 等本地目录，均已忽略。

## Android

先准备可执行的 `node`、`npm`，再运行 `setup-android.ps1` 和 `build-android.ps1`。工具链位于 `.tools`，前端使用 `package-lock.json` 安装依赖，打包包含类型检查。默认生成 ARM64 APK。

开发签名在 `.tools/android-user` 内，不能提交。不同签名的 APK 不能覆盖安装已有应用；保留自己的签名用于更新。

## 共用逻辑与手机宿主测试

```powershell
. .\portable\env.ps1
dotnet restore portable/Tests/EmueraAI.Portable.Tests.csproj --configfile portable/NuGet.Config
dotnet build portable/Tests/EmueraAI.Portable.Tests.csproj --no-restore
$projectRoot = (Get-Location).Path
dotnet portable/Tests/bin/Debug/net10.0/EmueraAI.Portable.Tests.dll $projectRoot --function-document
dotnet portable/Tests/bin/Debug/net10.0/EmueraAI.Portable.Tests.dll $projectRoot --story
```


手机页面测试在 `portable/era-core/EraCore.Web` 运行 `npm ci` 和 `npx vitest run`。对应浏览器回放脚本位于 `scripts`，需要先生成相关中性宿主记录，并安装 Playwright 的浏览器依赖。本项目的浏览器回放不等于 Android 真机测试。

## 口上触发回归与静态扫描

构建共用测试后，可用同一 DLL 加项目根目录及 `--prefixed-hooks`、`--empty-dialogue` 分别测试带前缀口上与空白按键等待。这些测试使用中性文本和受控 API；不会执行原游戏或调用个人 API。

`--scan-hooks` 会只读扫描项目父目录中的 ERB 目录，兼容游戏文本编码，输出 `logs/hook-scan` 元数据。它不运行游戏、不读取存档或导出口上正文；目录包含副本和版本，结果不等于去重游戏数或已确认故障数。完成后 `--verify-hooks` 使用两端共用规则验证新增命名条目。
