<img src="https://raw.githubusercontent.com/al01cn/sillyTavern-launcher/GUI/src/assets/images/banner.png" style="width: 100%; height: 100%;" />

# 星酿启动器 (AstraBrew Launcher) · Windows版本


<div style="text-align: center;" align="center">

星酿启动器 (AstraBrew Launcher) 原为 [酒馆启动器GUI (SillyTavern Launcher GUI)](https://github.com/al01cn/sillyTavern-launcher)，是一款专为小白打造的简单易用的[酒馆(SillyTavern)](https://github.com/sillyTavern/SillyTavern)启动器。基于 Rust 和 egui 开发，旨在为用户提供易用、快速、轻量、多功能的启动和管理体验。

当前仓库单独管理 Windows版本 的启动器。Windows 用户可以通过星酿启动器轻松配置和管理酒馆实例，享受一键启动、版本管理、环境配置等功能。我们专注于提供流畅的用户界面和稳定的性能，让每位用户都能轻松上手并愉快使用。

[![Releases](https://img.shields.io/github/v/release/AstraBrew-Labs/AstraBrew-Launcher-Win?label=版本)](https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Win/releases)
[![Rust](https://img.shields.io/badge/Rust-latest-CE422B?style=flat-square&logo=rust)](https://www.rust-lang.org/)
[![egui](https://img.shields.io/github/v/release/emilk/egui?label=egui)](https://github.com/emilk/egui)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

[官网](https://launcher.astrabrew.cn) | [Mac版](https://github.com/AstraBrew-Labs/AstraBrew-Launcher-Mac/)

</div>

## 📖 项目介绍

星酿启动器提供了一站式的环境配置、一键启动、版本管理以及扩展和资源管理功能。其特点包括：
- **实时响应的界面**：基于 egui 构建的即时模式 GUI，流畅且资源占用低。
- **国际化支持**：目前支持中文 (zh_CN) 和英文 (en_US)。
- **主题切换**：支持深色和浅色模式的动态切换。
- **多屏自适应**：自动处理 16:9 和 4:3 屏幕比例，适配高分辨率显示器及多屏幕切换。
- **环境隔离**：支持内置和系统级别的 Git、Node.js 环境切换，内置包管理和加速代理配置。

## 🛠 技术栈

- **Rust (2024 Edition)**: 核心开发语言，提供内存安全和极致性能。
- **egui (0.33)**: 简单易用、响应快速的即时模式 (Immediate mode) GUI 框架。
- **eframe (0.33)**: 官方的 egui 原生应用集成框架。
- **egui_phosphor (0.11)**: 提供丰富的界面图标支持。
- **Serde**: 高效的序列化与反序列化库（用于配置文件管理）。
- **serde_json**: 用于 JSON 格式数据的序列化和反序列化。
- **serde_yaml**: 用于 YAML 格式数据的序列化和反序列化。
- **reqwest**: 强大的 HTTP 客户端库，用于网络请求和数据
- **rfd**: 跨平台的文件对话框库，用于文件选择和保存操作。
- **jwalk**: 高性能的并行文件系统遍历库，用于快速扫描和管理文件资源。
- **block2**: 用于在 Rust 中实现阻塞操作的库，适用于需要等待的任务。
- **zip**: 用于处理 ZIP 文件的库，支持压缩和解压缩操作。
- **qrcode**: 用于生成二维码的库，支持多种二维码格式和自定义样式。
- **cargo-packager**: 用于打包和发布 Rust 应用程序的库，支持生成 Windows 可执行文件和安装包。
- **cargo-packager-updater**: 用于自动更新和版本管理的库，支持从 GitHub 仓库获取最新版本并进行更新。

## 运行项目

## 运行

### 普通用户

普通用户请可以直接到[发布页(Releases)](/releases)下载最新版本的exe安装包，按照提示安装完成后，即可打开使用，或者使用免安装的ZIP压缩包，解压后双击程序打开使用。

### 开发者

##  开发环境

本项目使用 Rust 语言进行开发，因此需要安装 Rust 和 Cargo。建议使用最新的 stable 版本，以确保兼容性和性能。

### 前置要求

在开始之前，请确保您的系统已经安装了以下工具：
- [Rust & Cargo](https://www.rust-lang.org/tools/install) (建议使用最新的 stable 版本)
- 因为当前仓库是Windows的版本，所有开发都按照 Windows 的规范进行开发。仅支持 Windows 平台。
- 请勿将其他平台的依赖或配置引入本项目，以避免不必要的兼容性问题。

### 运行项目

1. 克隆或下载本项目到本地。
2. 进入项目根目录：
   ```bash
   cd astrabrew-launcher-win
   ```
3. 使用 Cargo 检查或编译项目：
   ```bash
   cargo check
   ```
4. 运行项目（调试模式）：
   ```bash
   cargo run
   ```
   > **注意**：开发过程中如果只需检查代码规范和编译错误，请优先使用 `cargo check` 以提高效率。

## 📦 构建打包

项目提供了一键构建打包脚本，会同时生成 **NSIS 安装包（.exe）** 和 **免安装版（.zip）**，统一输出到项目根目录的 `dist/` 目录。

### 产物说明

| 产物 | 说明 |
|------|------|
| `AstraBrew Launcher_<version>_x64-setup.exe` | NSIS 安装包，含开始菜单快捷方式、卸载程序、中英文语言选择 |
| `AstraBrew Launcher_<version>_x64_portable.zip` | 免安装版，解压即用（单文件 exe，字体与图标已内嵌） |

### 前置要求

- [Rust & Cargo](https://www.rust-lang.org/tools/install)（stable）
- 首次运行脚本会自动安装 `cargo-packager` CLI（通过 `cargo install`）
- 网络可访问 crates.io 与 NSIS 官方源（首次打包时 cargo-packager 会自动下载 NSIS 工具）

### beta / release 构建模式

| 模式 | 版本号示例 | 文件名标识 | UI 角标 |
|------|-----------|-----------|---------|
| `-release`（默认） | `0.0.1` | 无特殊标识 | 无角标（正式版） |
| `-beta` | `0.0.1-beta` | 文件名含 `-beta` | 侧边栏右上角橙红色"测试版/BETA"角标 |

> `-beta` 模式会临时修改 `Cargo.toml` 的 `version` 字段并设置 `ASTRABREW_BUILD_TYPE=beta` 环境变量，`build.rs` 读取后设置 `cfg(beta)` 编译标识，UI 中的 `#[cfg(beta)]` 代码块渲染 BETA 角标。打包完成后自动恢复 `Cargo.toml`（即使中途报错也能恢复）。

### 一键打包

项目提供三种构建方式，功能完全等价，按习惯选择：

**方式一：Bash 脚本（需要 Git Bash 或 WSL，支持 beta/release 参数）**

```bash
chmod +x build
./build -release          # 构建正式版
./build -beta             # 构建测试版（UI 显示 BETA 角标）
./build -beta -clean      # 清空 dist 后构建测试版
./build -release -skipbuild  # 仅生成 zip 免安装版
./build -help             # 查看帮助
```

**方式二：PowerShell 脚本（直接可用，无需额外环境）**

```powershell
.\build.ps1                # 完整构建打包（正式版）
.\build.ps1 -Beta          # 构建测试版（UI 显示 BETA 角标）
.\build.ps1 -Beta -Clean   # 清空 dist 后构建测试版
.\build.ps1 -SkipBuild     # 跳过 NSIS，仅生成 zip 免安装版
```

**方式三：双击 build.bat（最简单，无需命令行）**

在资源管理器中双击项目根目录下的 `build.bat`，打包完成后窗口不会自动关闭，方便查看输出。

> 以上三种方式的脚本均在项目根目录。`scripts/` 目录下的 `build.ps1` / `build.bat` 为旧版保留，功能相同，按需使用。

### 打包配置

打包配置位于 `Cargo.toml` 的 `[package.metadata.packager]` 段，关键字段：

- `productName` / `version` / `identifier`：产品元信息
- `outDir = "dist"`：产物输出目录
- `binariesDir = "target/release"`：cargo 构建产物目录
- `formats = ["nsis"]`：仅生成 NSIS 安装包（zip 由构建脚本另行生成）
- `beforePackagingCommand`：打包前自动执行 `cargo build --release`
- `[package.metadata.packager.nsis]`：NSIS 安装包选项（安装模式、语言、压缩、应用数据路径）

### 重新生成安装包图标

安装包图标 `icons/icon.ico` 由 `scripts/gen_icon_ico.py` 从 `icons/icon.png` 生成（多尺寸 16/24/32/48/64/128/256）。修改 PNG 后重新生成：

```bash
python scripts/gen_icon_ico.py
```

## 📂 项目结构

```text
astrabrew-launcher-win/
├── assets/                  # 静态资源文件
│   └── fonts/               # 字体文件（如 MiSans-Regular.ttf）
├── data/                    # 本地数据及配置存储
│   ├── settings.json        # 应用程序配置文件（实时保存）
│   └── ...                  # 库、日志和核心运行文件目录
├── src/                     # 源代码目录
│   ├── core/                # 核心逻辑模块（环境配置等）
│   ├── lang/                # 国际化语言模块（en.rs, zh.rs, lang.rs）
│   ├── pages/               # 界面视图模块（如 settings.rs 等）
│   ├── ui/                  # 自定义 UI 组件（如分段控制器）
│   ├── utils.rs             # 通用工具函数
│   └── main.rs              # 应用程序主入口
├── build.rs                 # 构建脚本（读取环境变量设置 cfg(beta)）
├── build                    # Bash 构建脚本（支持 -beta/-release 参数，需要 Git Bash）
├── build.ps1                # PowerShell 构建脚本（双击或命令行运行，无需额外环境）
├── build.bat                # 批处理入口（双击即可打包，无需 Git Bash）
├── Cargo.toml               # Rust 项目配置和依赖声明
└── README.md                # 项目说明文档
```

## 📂 软件目录结构

```text
%AppData%/AstraBrew Launcher/    ← 根目录 (root)
├── data/                       ← 软件数据目录
│   ├── default/                ← 默认数据子目录
│   │   └── sillytavern/        ← 酒馆数据子目录
│   │       ├── config.yaml     ← 全局统一酒馆配置文件
│   │       └── settings.json   ← 全局统一酒馆WebUI配置文件
│   ├── sillytavern/            ← 酒馆数据子目录
│   │   └── data/               ← 默认全局酒馆数据目录
│   │       ├── config.yaml     ← 全局统一酒馆配置文件
│   │       └── default-user/
│   │           └── settings.json ← 全局模式酒馆WebUI设置
│   └── local_instances.json
├── logs/                    ← 软件日志目录
├── sillytavern/             ← 酒馆核心文件目录 (ST installation) (在线下载实例)
├── lib/                     ← 内置环境目录 (内置 NodeJS、MinGit 等)
│   ├── nodejs/              ← 内置 NodeJS 目录
│   ├── git/                 ← 内置 MinGit 目录
│   ├── pm2/                 ← 内置 PM2 目录
│   └── caddy/               ← 内置 Caddy 目录
└── config.json              ← 启动器配置文件

%Temp%/astrabrew-launcher/         ← 根目录·临时目录 (temp)
%Temp%/astrabrew-launcher/caches   ← 缓存目录，存放API数据缓存等 (caches)
```

## 📝 代码规范与注释规范

### AI编程
- 可使用仓库里的`MEMORY.md`喂给AI，辅助开发。

### 代码规范
- **命名规范**：
  - 变量和函数使用 `snake_case`。
  - 结构体、枚举和特征使用 `PascalCase`。
  - 常量和静态变量使用 `SCREAMING_SNAKE_CASE`。
- **模块化**：页面和功能模块需分文件编写，禁止所有逻辑堆砌在主函数或单个文件内。
- **错误处理**：尽量使用 `Result` 和 `Option` 进行错误处理，避免直接使用 `unwrap()` 或 `panic!()` 导致程序崩溃。

### 代码注释规范
- **强制使用中文**进行代码注释。
- **函数和结构体说明**：在复杂的函数和结构体定义前使用 `///` 进行文档注释，解释其用途和参数含义。
- **逻辑块注释**：在复杂的业务逻辑块上方使用 `//` 进行单行注释，说明该段代码的意图。
- 避免冗余和废话注释（如 `// 定义变量` 等无意义的说明）。

## 🤝 贡献指南

我们欢迎并感谢任何形式的贡献！
1. Fork 本仓库。
2. 创建您的特性分支 (`git checkout -b feature/AmazingFeature`)。
3. 提交您的更改 (`git commit -m 'Add some AmazingFeature'`)。
4. 推送到分支 (`git push origin feature/AmazingFeature`)。
5. 开启一个 Pull Request。

在提交代码前，请务必运行 `cargo check` 和 `cargo fmt` 以确保代码符合规范且无编译错误。

## 📄 代码许可

本项目采用 [MIT License](LICENSE) 协议进行开源，允许自由使用、修改和分发，但请保留原作者的版权声明。字体等第三方资源版权归其原作者所有。
