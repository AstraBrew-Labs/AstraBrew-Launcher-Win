# MEMORY.md - 项目长期记忆

## 规范
不要使用cargo run启动项目，也不要用于测试
只能使用cargo check检查项目
代码要严格模式，warning要修复，error要修复。
仅Windows 10以上的平台，不用考虑其他平台。

## 项目基础信息
- **项目名**：AstraBrew Launcher（星酿启动器）
- **技术栈**：Rust + egui/eframe 0.33，egui-phosphor 图标库
- **平台**：Windows
- **窗口规格**：默认 1280x720（16:9），最小 800x600，禁用最大化

## 关键目录/文件
- `src/main.rs`：主程序，MyApp 状态管理，eframe::App::update
- `src/pages/settings.rs`：设置页面 UI + SettingsState 数据结构
- `src/core/settings/`：各子系统核心逻辑（git/nodejs/pm2/github_proxy）
- `src/lang/zh.rs` + `src/lang/en.rs`：双语翻译文件
- `data/settings.json`：运行时持久化配置
- `%Temp%/astrabrew-launcher/github_proxy_cache.json`：GitHub 节点列表缓存（TTL 3天）

## 目录规范
`utils.ts` ← 路径辅助函数，以下路径统一用这个文件里函数，不要重复或写死路径在其他rs文件里。
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

## 开发规范
- UI 设计：无滚动条完整显示（特殊场景用 max_height 限制的 ScrollArea）
- 工作流：先分析根因、参考现有模式、再编码
- 配置持久化：`SettingsState::save()` / `load()`（JSON）
- 异步通信：`std::sync::mpsc::channel` + `ctx.request_repaint()`

## 用户偏好
- 风格：干脆直接，不废话
- UI：明确数值约束，无滚动条（或 max_height 限制）
- 语言：中文沟通

## 环境模式（2025-07-05）
- `SettingsState.env_mode: EnvSource` — 统一环境模式：系统 / 内置
- 内置模式：优先使用 `%AppData%/AstraBrew Launcher/lib/` 下的 Node.js、MinGit、Caddy、PM2
- 系统模式：使用系统 PATH 中的全局环境

## 酒馆配置页面（2026-06-03）
- 从 `.docs/Tavern.vue` 转换为 Rust egui 页面
- 9 个折叠分区：网络与访问、安全与白名单、SSL、CORS、代理与备份、缩略图、性能、日志、会话安全
- 数据结构：`src/core/settings/tavern.rs`（TavernConfig，含 20+ 子结构体）
- UI 页面：`src/pages/tavern_config.rs`（collapsible_section 自定义折叠卡片）
- 配置持久化：`data/tavern_config.json`，手动保存模式
- 状态管理：`TavernConfigUI` 存储于 `MyApp.tavern_config_ui`
- 翻译：中英双语 60+ 个 key（`tc_*` 前缀）
- **serde_yaml 0.9 不支持 `!tag:yaml.org,2002:null`** — 写 YAML null 用 `Value::Null`，不能用 tagged value，否则回读时解析失败 → 全默认值 → 保存覆盖原配置（数据丢失）
- YAML 写入后要确保 serde_yaml 能回读

## 控制台页面（2026-06-03）
- `src/pages/console.rs`：ConsoleState（status + logs）+ render 函数
- 状态栏（左右布局）+ 日志区（ScrollArea + stick_to_bottom）
- 按钮组：启动/重启/停止/强行停止，按状态联动启用/禁用
- 导航位于底部区域，在设置按钮上方（Page::Console）
- 翻译键 `console*` 前缀，中英双语 18 个 key

## 一键启动主页（2026-06-04）
- `src/pages/home.rs`：主页渲染函数 `render(ui, current_page, console_state, lang, version_info, command)`
- 英雄按钮：停用态"一键启动"（绿）→ 发送 ConsoleCommand::Start + 跳转控制台，运行态"立即停止"（红）→ 发送 ConsoleCommand::Stop + 跳转控制台
- 底部三列信息卡片：当前版本 / 启动模式 / 服务端口
- 翻译键 `home_*` 前缀，中英双语 18 个 key

## 进程管理（2026-06-04）
- `src/core/process.rs`：核心进程管理模块
  - `ConsoleCommand` 枚举：`Start` / `Stop` / `ForceStop`
  - `ProcessMsg` 枚举：`Log(String)` / `StateChange(ConsoleStatus)`
  - `start_tavern(tx, settings, child_handle)`：后台线程中执行完整启动流程
    1. 检查 Node.js / Git 环境（从 settings 获取 builtin/system 路径）
    2. 端口检查：读取 `config.yaml` port 字段 → `netstat` 检测 → `taskkill /F` 释放占用
    3. 代理配置：非关闭时设置 HTTP_PROXY/HTTPS_PROXY 环境变量
    4. 内置 Git → 追加到 PATH
    5. `allow_tavern_background` → PM2 模式（`pm2 start --name astrabrew-tavern`）
    6. `!allow_tavern_background` → 直接模式（`node server.js`，child 句柄存 Arc<Mutex>）
    7. `TavernDataMode::Global` → 追加 `--configPath <APPDATA>/.../config.yaml`
  - `stop_tavern(force, child_handle)`：同步停止，PM2 (`stop/kill` + `flush`) 或直接 kill
  - 每次停止必清空 PM2 日志（`pm2 flush`），防止日志鬼畜
- `console.rs`：按钮点击改为发 `ConsoleCommand`；`pending_restart` 字段支持重启；**支持 ANSI 颜色 escape code 渲染**
- `home.rs`：英雄按钮改为发 `ConsoleCommand`
- `main.rs`：
  - `MyApp` 新增 `tavern_child: Arc<Mutex<Option<Child>>>` + `process_receiver`
  - `handle_console_command()` → 分发命令
  - `start_tavern_process()` → 克隆 settings + 创建 channel + spawn 线程
  - `stop_tavern_process(force)` → **异步**：spawn 线程执行停止，完成后通过 channel 回传结果
  - `update()` 中轮询 ProcessMsg（Log → add_log, StateChange → 更新状态）
  - Disconnected 时检查 pending_restart → 自动重启

## Git 节点选择安装功能（2026-07-07）
- 内置 Git 未安装时点击"安装"→ 弹出节点选择弹窗（8 个镜像源）
- 背景线程 HEAD 测速（5s 超时）→ 按延迟排序 → 3 秒倒计时自动选最快
- 手动选择或自动选择 → `InstallTaskState::start_git_install(url)` → 进度弹窗
- `GitNodeSelectState` 存于 `MyApp.git_node_select`，接收 `GitNodeSelectMsg`（TestingProgress / LatencyResults）
- `GitMirrorNode` 存储于 `src/core/settings/git.rs`
- `download_and_install_git_from_url(url, tx)` 安装到 `{APPDATA}/AstraBrew Launcher/lib/git/`
- 翻译 key：`git_node_select_title/desc`、`git_node_auto_select`、`git_install_progress_title/desc`
