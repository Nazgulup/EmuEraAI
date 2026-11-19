# 来源与第三方说明

- Windows 引擎基于 Emuera 1.824 / v11+webp+test+fix 的修改版，保留 MinorShift 等原作者的通知。见 `LICENSE.Emuera.txt` 和原文件注释。
- Android 内核基于 [LaoBro/era-core](https://github.com/LaoBro/era-core) 的固定提交 `6e14de0fa0cd83b0bb83df96fc29422e107f7af4`，保留所需源码及原始许可。具体来源见 `portable/UPSTREAM.txt` 和 `portable/era-core/Readme/License`。
- WebP 原生库保留 `native/LICENSE.libwebp.txt`；包装器许可保留在 `LICENSE.Emuera.txt`。
- 字体许可保留在 `portable/era-core/EraCore.Web/src/assets/fonts`，包含 IPA 和 DejaVu 通知。
- 前端与 .NET 依赖由各自的包声明与锁文件管理，遵循其原有许可证。

本项目增加了加载器、AI 文字生成、预设、日志、剧情保护、模板匹配和函数清单等修改，不冒称为上游原版。游戏需由用户自行提供；仓库不附第三方游戏、存档或正文语料。
