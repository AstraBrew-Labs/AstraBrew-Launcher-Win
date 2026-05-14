# 星酿启动器 (AstraBrew Launcher)

星酿启动器 (AstraBrew Launcher) 是一款专为 Windows 平台打造的高性能应用程序启动器。它基于 Rust 和 egui 开发，旨在为用户提供快速、轻量、多功能的启动和管理体验。

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

## 🚀 安装依赖与运行项目

### 前置要求

在开始之前，请确保您的系统已经安装了以下工具：
- [Rust & Cargo](https://www.rust-lang.org/tools/install) (建议使用最新的 stable 版本)
- 仅支持 Windows 平台。

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
├── Cargo.toml               # Rust 项目配置和依赖声明
└── README.md                # 项目说明文档
```

## 📝 代码规范与注释规范

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
