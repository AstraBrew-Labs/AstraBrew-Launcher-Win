## 规范
这是一个 rust + iced + astra_ui 项目
astra_ui文档：https://docs.rs/iced-astraui/0.0.1
不要使用cargo run启动项目，也不要用于测试
只能使用cargo check检查项目
代码要严格模式，warning要修复，error要修复。
仅Windows10以上的平台，不用考虑其他平台。

要确保程序check通过

需要加上代码注释，注释要中文

软件窗口界面，不能最大化
在16：9比例下，默认要1280x720，固定大小，不能手动调节
在4:3比例下，默认1280x800，固定大小，不能手动调节
要适配高清屏，高分辨率屏，保持界面清晰度
要保证每个缩放都能正常显示
要适配多屏切换
要适配如果软件界面在副屏，但副屏断开，要自动切换到主屏显示

 项目结构如下：
  src
   - core 核心模块
    - ...rs 核心模块拆分
   - utils.rs 工具函数
   - main.rs 主函数

## 目录结构

%AppData%/AstraBrew Launcher/   ← 根目录 (root)
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

## 多语言支持
要支持中文和英文的国际化适配，用n18n的规范，用 键值对 的方式来实现多语言切换，比如：“settings.title”:"设置"。
src
 - lang
  - zh.rs 中文（键 → 中文；另导出 KEYS 供一致性测试）
  - en.rs 英文（键 → 英文，键集合必须与 zh.rs 一致）
  - lang.rs 多语言引擎（t / tf / t_in / text / textf / raw / resolve）

### 硬规则（写代码时必须遵守）

1. **界面代码只写键，不写具体文案**。键命名 `<域>.<语义>`，如 `settings.title`、
   `extensions.error.clone_failed`、`tavern.field.port.hint`。
2. **内容进入不翻译的显示端之前，必须已经是翻译好的值**。不翻译的显示端只有这几类：
   - `crate::lang::raw(x)`（专给运行时数据：路径、版本号、日志行）
   - 原生 `iced::widget::text(x)`
   - `text_input(placeholder, ...)` 的第一个参数
   键要么交给 `text()/t()/tf()`，要么在**字符串进入通道那一刻**用 `resolve()` 或 `t().to_owned()` 固化。
3. **`resolve(x)` 只在通道入口调用一次**（错误入 state、日志入队、Toast 入队），
   不要放进每帧渲染路径——键清单是线性查找。
4. **辅助函数按语义定签名**：收键的用 `&'static str` + 内部 `text()`；
   收运行时数据的用 `String`/`&str` + 内部 `raw`。让编译器参与分类。
5. **不要用显示文案做判断条件**（如 `if label == "更新"`、`error == "安装已取消"`）——
   键化后必然失效。改用常量、枚举或显式参数。
6. **`from_key` / `normalize_*` 里的中文是历史值兼容别名**，属于数据不是文案，必须保留。

### 排查「界面显示出翻译键」

```sh
ASTRA_I18N_AUDIT=1 cargo run     # 逐个操作界面，raw() 收到文案键会打印告警
```
默认关闭。出现告警说明某处把键交给了不翻译的显示端，按上面第 2 条修。

更完整的排查步骤见技能 `iced-i18n-key-display-audit`。
