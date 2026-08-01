# 更新日志

本文件记录 AstraBrew Launcher Windows 平台的所有版本更新内容。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 规范。

---
## [0.0.5-beta] - 2026-08-02

- 修复 Windows 路径直接传入 Node.js `--import` 导致的 `ERR_UNSUPPORTED_ESM_URL_SCHEME`，直接启动与 PM2 模式均改用标准 `file://` URL。
- 修复语言和主题的“跟随系统”功能，语言改为读取 Windows 用户界面语言，主题可正确响应系统明暗模式切换。
- 修复自动更新未在启动时执行的问题，并统一从 Windows 版仓库 `AstraBrew-Labs/AstraBrew-Launcher-Win` 检查和下载更新。
- 更新检查和安装包下载优先使用 `gh-proxy.org` 加速，失败时自动回退 GitHub 原地址，并增加 Windows 安装包格式校验。

## [0.0.4] - 2026-07-28

- 新增首次启动的一键自动化流程，可按环境模式安装缺失的 Git、Node.js 和酒馆实例后自动启动。
- 修复已安装环境仍重复进入安装流程，以及系统环境与内置环境判断不一致的问题。
- 新增酒馆依赖缺失检测，可识别 `ERR_MODULE_NOT_FOUND` 并引导自动安装缺失的 npm 依赖。
- 完善日志系统，分别保存启动器日志和酒馆日志，并支持从控制台导出日志压缩包。
- 修复桌面模式下部分扩展无法导出 Blob、Data URL 和 Shadow DOM 文件的问题。
- 新增 GitHub Releases 自动构建、版本发布和 Windows 客户端更新支持。

## [0.0.1] - 2026-06-23

- 初始版本
- 添加了 README.md 文件，并完善了项目结构。
- 第一个测试版，包含基础的设置页面、控制台、主页。
- 反向代理还处于初始阶段，但功能尚未完善。
