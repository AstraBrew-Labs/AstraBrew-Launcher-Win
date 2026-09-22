# 更新日志

本文件记录 AstraBrew Launcher Windows 平台 的所有版本更新内容。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 规范。

---

## [Unreleased]

### Fixed
- 修复桌面 WebView 窗口内页面布局错乱（内容被裁切／错位）：`wry` 的 `with_bounds` / `set_bounds` 接收的是**逻辑**像素（DIP），内部会按 `GetDpiForWindow` 取到的缩放比再乘一次交给 WebView2；而代码传入的是窗口客户区的**物理**像素，于是在非 100% 缩放的屏幕上被二次放大（150% 下放大约 1.5 倍），WebView2 控制器远大于客户区。现新增 `logical_bounds()` 把物理尺寸除以缩放比还原为逻辑值，并同时用于初始 `with_bounds` 与 `WM_SIZE` 的尺寸同步；窗口 DPI 在创建后改用 `GetDpiForWindow` 实测（可能因窗口落在不同缩放的显示器而与系统 DPI 不同）。
- 修复桌面 WebView 窗口无法最大化：窗口样式原先只有 `WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX`（最小/关闭按钮可用，最大化按钮灰掉）。现补充 `WS_MAXIMIZEBOX | WS_SIZEBOX`，并把客户区尺寸变化通过 `WM_SIZE` 同步给 WebView。
- 修复桌面模式无法打开酒馆窗口（报「EventLoop can't be recreated」）：winit 用进程级全局静态量限制事件循环在进程内只能创建一次，而 iced 启动时已占用该配额，且该标志在 Windows 上没有复位路径（复位函数被 `#[cfg(web_platform)]` 门控，仅 WASM 可用），因此工作线程里的第二次 `EventLoop::new()` 必然失败。现改为原生 Win32 自建窗口 + 手写 `GetMessageW` 消息循环承载 WebView2（`wry` 在 Windows 上只要求 `HasWindowHandle`，与 winit 无依赖关系），彻底移除桌面模式的 winit 依赖。
- 修复环境模式选「内置」却仍使用系统环境：两处独立缺陷。其一，`command_for` 无条件把内置 `lib/` 目录前置注入子进程 PATH，导致选「系统」时子进程仍优先命中内置的 node/git；现改为仅 `EnvSource::Builtin` 才注入。其二，网络层的 `resolve_command` 硬编码系统路径，9 处 Git 调用全部无视环境模式；现统一收敛为 `git_command(source)`，全链路按 `EnvSource` 解析。
- 修复界面中英文粗细不一致：astra_ui 内置的 HarmonyOS Sans 不含中文字形，中文回退到系统雅黑后笔画偏粗；且该系统字体族缺少 500 档，`medium()`（界面 187 处调用）会被 fontdb 吸附到 400。现改为内置思源黑体三档静态字重（Regular / Medium / Bold），中英文出自同一套设计，字重轴完整覆盖界面所需档位。
- 修复本地实例列表等界面显示的路径带 `\\?\` 前缀：`fs::canonicalize` 在 Windows 上返回扩展长度路径，该前缀是 Win32 API 的转义标记，对用户没有意义。新增 `display_path` 剥掉前缀（含 `\\?\UNC\` 还原为标准 UNC），并应用到实例列表、扫描进度、目录选择器、控制台下载提示与扩展路径校验错误。旧数据无需迁移：载入时会自动规范化并在下次保存时更新。
- 字体选择器新增「该字体不含中文字形」提示，避免用户改选西文字体后重新引入中英文粗细差。

### Changed
- 桌面模式窗口的窗口规范调整：不再套用主界面的「固定尺寸、不可最大化」，改为可缩放、可最大化、可最小化（同浏览器语义）。`AGENTS.md` 中「软件窗口界面，不能最大化」约束的是启动器主界面，桌面 WebView 窗口作为独立的内容浏览窗口不适用该条。
- 内置字体改为 `assets/fonts/SourceHanSansSC-{Regular,Medium,Bold}.otf`，由 `assets/fonts/subset_fonts.py` 按 GB2312 全字集 + 拉丁 + 常用符号裁剪生成，三档合计约 10 MB。
- `.gitignore` 移除 macOS 平台残留项（`.DS_Store`、`*.app.tar.gz`）。
- Windows 依赖调整：移除 `winit`，新增 `raw-window-handle`；`windows-sys` 补充 `Win32_System_Com` 与 `Win32_System_LibraryLoader`（自建窗口与 COM 初始化需要）。

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