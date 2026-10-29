## 窗口规范
软件窗口界面，不能最大化
要适配高清屏，高分辨率屏，保持界面清晰度
要保证每个缩放都能正常显示
要适配多屏切换
要适配如果软件界面在副屏，但副屏断开，要自动切换到主屏显示

> 实现集中在 `Services/WindowPlacement.cs`（由 `MainWindow.SetupWindow` 创建）。
> 新增窗口相关行为请改这里，不要在页面或 MainWindow 里另写一套 DPI / 尺寸计算。

## 实例状态

酒馆实例清单与「当前使用哪一份」只有一份数据：`Services/TavernInstances.cs`。
仪表盘的版本下拉、版本管理页的两个页签、标题栏实例徽章、关于页的版本信息全部读写它；
任何页面切换实例都只调 `TavernInstances.Use(...)`，界面刷新统一由 `Changed` 事件驱动。

> 不要在页面里另存一份实例列表或当前选择——历史实现中版本管理页与仪表盘各存一份，
> 结果一边切换另一边毫无反应。新增涉及实例的界面请直接读 `TavernInstances`。
> 清单目前是内存原型数据，落盘位置见下方 `data/local_instances.json`。

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