## 窗口规范
软件窗口界面，不能最大化
要适配高清屏，高分辨率屏，保持界面清晰度
要保证每个缩放都能正常显示
要适配多屏切换
要适配如果软件界面在副屏，但副屏断开，要自动切换到主屏显示

> 实现集中在 `Services/WindowPlacement.cs`（由 `MainWindow.SetupWindow` 创建）。
> 新增窗口相关行为请改这里，不要在页面或 MainWindow 里另写一套 DPI / 尺寸计算。

## 按钮圆角

所有按钮圆角统一 8（与卡片同档），不要出现 4 以下的按钮圆角。

- 全局默认值在 `Styles/Controls.xaml` 顶部的隐式 `Button` 样式里，覆盖所有没显式挂 Style 的
  按钮（含代码里 `new Button` 出来的）；
- 显式挂 Style 的按钮不受隐式样式影响，各自样式里必须自己写 `CornerRadius=8`
  ——新增按钮样式时照抄这条；
- 强调按钮用 `AccentActionButtonStyle`（= 框架 `AccentButtonStyle` + 圆角 8），
  不要直接挂框架的 `AccentButtonStyle`，它的圆角来自 `ControlCornerRadius` 的 4；
- 弹窗里由 `DefaultButton` 自动套上的强调按钮拿不到自定义 Style，需要在该 `ContentDialog`
  上写 `dialog.Resources["ControlCornerRadius"] = new CornerRadius(8)`。
- 例外：`ChipCloseButtonStyle`（14×14 的标签小叉）保持 7，等于圆形。

> 不要把 `ControlCornerRadius` 全局改成 8 —— 那会连带改掉输入框、下拉框、滑块等一大片控件。

## 实例状态

酒馆实例清单与「当前使用哪一份」只有一份数据：`Services/TavernInstances.cs`。
仪表盘的版本下拉、版本管理页的两个页签、标题栏实例徽章、关于页的版本信息全部读写它；
任何页面切换实例都只调 `TavernInstances.Use(...)`，界面刷新统一由 `Changed` 事件驱动。

> 不要在页面里另存一份实例列表或当前选择——历史实现中版本管理页与仪表盘各存一份，
> 结果一边切换另一边毫无反应。新增涉及实例的界面请直接读 `TavernInstances`。
> 清单目前是内存原型数据，落盘位置见下方 `data/local_instances.json`。

## 路径显示

界面上出现的路径一律是展开后的真实路径，不要直接显示 `%AppData%`、`%Temp%`、`%LOCALAPPDATA%`
这类环境变量——用户没法把变量粘进资源管理器。

- 取值统一走 `Services/AppPaths.cs`（`RoamingRoot` / `LocalRoot` / `TempRoot`），
  或用 `Environment.GetFolderPath(...)`、`AppPaths.Expand(...)` 把变量换成实际目录；
- 覆盖范围：页面文本、ToolTip、反馈条（InfoBar）、控制台日志、对话框、复制到剪贴板的文本；
- 宽度不够时用 `TextTrimming="CharacterEllipsis"` 截断 + ToolTip 给完整路径，
  **不要**反过来把真实路径折叠成 `%APPDATA%\…` 来省宽度；
- 变量形式只允许出现在存储值（配置文件里记的默认目录）与注释 / 文档示意中，
  这类值读出后必须先 `Expand` 再交给界面。

> 反例（已修）：仪表盘「打开数据目录」曾直接弹 `%AppData%\AstraBrew Launcher\data`；
> 设置页曾把 `C:\Users\…\AppData\Roaming\…` 折叠成 `%APPDATA%\…` 显示。

## 目录结构

%AppData%/AstraBrew Launcher/   ← 根目录 (root)
├── data/                       ← 软件数据目录
│   ├── default/                ← 默认数据子目录
│   │   └── sillytavern/        ← 酒馆默认数据子目录
│   │       ├── config.yaml     ← 全局统一酒馆配置文件
│   │       └── settings.json   ← 全局统一酒馆WebUI配置文件
│   ├── sillytavern/            ← 酒馆数据子目录
│   │   └── data/               ← 默认全局酒馆数据目录
│   │       ├── config.yaml     ← 全局统一酒馆配置文件
│   │       └── default-user/  ← 全局统一酒馆默认用户目录
│   │           └── settings.json ← 全局模式酒馆WebUI设置
│   └── local_instances.json
├── logs/                    ← 软件日志目录
├── sillytavern/             ← 内置酒馆路径
├── bin/                 ← 内置环境目录 (内置 NodeJS、MinGit 等)
│   ├── nodejs/             ← 内置 NodeJS 目录
│   ├── mingit/             ← 内置 MinGit 目录
│   ├── pm2/                ← 内置 PM2 目录
│   └── caddy/              ← 内置 Caddy 目录
└── config.json              ← 启动器配置文件

%Temp%/astrabrew-launcher/         ← 根目录·临时目录 (temp)
%Temp%/astrabrew-launcher/caches   ← 缓存目录，存放API数据缓存等 (caches)