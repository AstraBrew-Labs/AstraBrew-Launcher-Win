# 更新日志

本文件记录 AstraBrew Launcher Windows 平台 的所有版本更新内容。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 规范。

---

## [Unreleased]

### Fixed
- 修复界面中英文粗细不一致：astra_ui 内置的 HarmonyOS Sans 不含中文字形，中文回退到系统雅黑后笔画偏粗；且该系统字体族缺少 500 档，`medium()`（界面 187 处调用）会被 fontdb 吸附到 400。现改为内置思源黑体三档静态字重（Regular / Medium / Bold），中英文出自同一套设计，字重轴完整覆盖界面所需档位。
- 修复本地实例列表等界面显示的路径带 `\\?\` 前缀：`fs::canonicalize` 在 Windows 上返回扩展长度路径，该前缀是 Win32 API 的转义标记，对用户没有意义。新增 `display_path` 剥掉前缀（含 `\\?\UNC\` 还原为标准 UNC），并应用到实例列表、扫描进度、目录选择器、控制台下载提示与扩展路径校验错误。旧数据无需迁移：载入时会自动规范化并在下次保存时更新。
- 字体选择器新增「该字体不含中文字形」提示，避免用户改选西文字体后重新引入中英文粗细差。

### Changed
- 内置字体改为 `assets/fonts/SourceHanSansSC-{Regular,Medium,Bold}.otf`，由 `assets/fonts/subset_fonts.py` 按 GB2312 全字集 + 拉丁 + 常用符号裁剪生成，三档合计约 10 MB。
- `.gitignore` 移除 macOS 平台残留项（`.DS_Store`、`*.app.tar.gz`）。

## [0.2.1] - 2026-09-19

### Added
- 启动器自动更新：从 GitCode 镜像（优先）与 GitHub 直连（兜底）检测并安装新版本；minisign 验签、下载地址按发布资产自动纠偏、多源回退、失败原因精确归因。
- 设置页「软件与更新」区块：展示当前版本，提供「检查更新」入口与更新确认弹窗（含发行说明）。
- 构建发布流水线：`build.ps1` / `build.bat`（本地）与 `.github/workflows/release.yml`（CI），产出 NSIS 安装包、免安装压缩包、SHA-256 校验清单与 `latest.json`。
- 测试版标记：构建渠道为 beta 时界面左上角显示「测试版」，由 Cargo.toml 的 `[package.metadata.astrabrew] beta` 开关控制。
- 版本管理、扩展管理、资源管理、本地实例管理、环境依赖检测、动态字体加载与界面缩放等核心模块。

### Changed
- 设置页版本号不再硬编码，改为读取编译期版本号。

### Fixed
- 修正更新清单平台键与更新器自检键不匹配导致无法检测的问题；平台键改为按目标三元组与 `windows-<arch>` 匹配。

## [0.2.0] - 2026-08-18

### Changed
- 完成 iced + astra_ui 界面架构迁移，补齐核心页面与模块。
- 目标平台由 macOS 切换为 Windows：路径、命令、注册表、WebView2 与打包链路全面替换。

## [0.1.0] - 2026-08-17

### Changed
- 从 egui 迁移到 iced + astra_ui，重建导航、主题与字体系统。

## [0.0.1] - 2026-06-23

- 初始版本
- 添加了 README.md 文件，并完善了项目结构。
- 第一个测试版，包含基础的设置页面、控制台、主页。
- 反向代理还处于初始阶段，但功能尚未完善。