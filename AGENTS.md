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

## 全局消息提示

**所有「说一声」的提示都走全局消息**（装好了、复制了、删掉了、失败了、刷新完了……），
页面里**不再挂 `InfoBar`**，也不再弹 ContentDialog 播报结果：装一次内置实例要一两分钟，
这期间用户很可能已经翻到别的页面，写在页面里的提示会跟着页面一起消失。

- 唯一出口 `Services/AppNotify.cs`：`AppNotify.Success / Info / Warning / Error(title, body,
  actionText?, action?)`。页面侧每个页面留一个 `private static void Notify(title, message,
  severity)` 转发到 `AppNotify.Post`，**不要**自己弹 ContentDialog 播报，也不要直接引用消息控件。
- 展示在 `Views/Controls/NotifyHost`，由 `MainWindow` 挂在内容区右下角、`ContentRoot` 之外
  —— 那层带界面缩放，消息卡跟着缩放会和窗口边缘错开（同弹窗，按 100% 呈现）。
  同屏最多三条，超出时先撤掉最早的一条，保证新消息立刻可见。
- 文案按「一眼读完」写：标题是结论（「内置实例已安装」），正文一句细节 + 下一步点哪儿。
  耗时、路径、退出码留在页面日志里；消息卡正文最多 4 行，超了省略。
- 停留时长按严重程度分：成功 / 提示 5 秒、警告 8 秒、**失败常驻**（必须点关闭）——
  安装失败来不及读就滑走等于没播报。卡片底部那条细线是倒计时，没有线的就是常驻消息。
- 需要用户落地的动作放进消息卡的操作按钮（如失败时的「查看日志」），点按钮先收卡片再执行动作。
- **后台任务的进行中状态用常驻消息**（`AppNotify.Live(key, title, body, actionText?, action?)`）：
  传一把 key，后续同 key 的投递会**原地更新**同一张卡（不重播动画、不叠卡），
  任务结束由发起方 `AppNotify.Close(key)` 收走。
  - 为什么不是弹窗：扫描、安装这类长任务要跑几十秒到十几分钟，弹窗会挡住整个界面逼用户先处理它；
    而页面内的小字一卸载就没了。常驻消息挂在窗口上，翻到任何页面都看得见。
  - 为什么不可关闭：它代表「有任务正在跑」，允许关掉就会出现「关了提示以为任务停了」。
    要停只能走任务自己的「取消」，停完由任务自己撤卡。常驻卡也不会被新消息挤掉。
  - 正文控制在两行以内（一条路径范围 + 一行计数），数字每 250ms 级更新一次即可，
    别把每秒十几万条目录计数直接怼给 UI 线程。

### 什么才配用 Alert（ContentDialog）

只有「不打断就会做错事」的才弹窗，其余一律全局消息：

| 用途 | 例子 | 位置 |
|---|---|---|
| 确认不可逆操作 | 停止服务、删除扩展 / 资源、移除实例、恢复默认设置 | 各页面 `ContentDialog` |
| 让用户做选择 | 缺 Git / Node.js → 「前往设置」；安装依赖前的长任务确认 | `VersionPage` |
| 长任务进度面板 | Git / Node.js 安装、GitHub 连通性测试 | `SettingsPage` 的 `CreateShell` |
| 表单 / 长文阅读 | 扩展安装（输入 Git 地址）、版本更新日志、访问地址详情 | 各页面弹窗 |

> 「进度面板」是**按需**打开的观察窗口，不是任务状态的唯一出口：任务照旧要挂一张常驻消息卡，
> 用户点进弹窗只为了看细账。扫描是例外——用户点了「扫描」就当场把详情弹窗打开（至少让他看到
> 环开始转），但关掉它扫描照跑，消息卡一直在。

- 页面里只留两种 **内联** `InfoBar`，它们是「状态说明」而不是播报：版本页的开发版风险提示
  （`StagingNote`）、酒馆配置页的非普通模式提示（`BrowserModeHint`）——跟着选项常驻，
  用消息卡播报会把「一直成立」说成「刚刚发生」。
- 按钮圆角、主题色等细节见「按钮圆角」一节；弹窗里由 `DefaultButton` 自动套的强调按钮
  仍需在 dialog 上覆写 `ControlCornerRadius`。

## 实例状态

酒馆实例清单与「当前使用哪一份」只有一份数据：`Services/TavernInstances.cs`。
仪表盘的版本下拉、版本管理页的两个页签、标题栏实例徽章、关于页的版本信息全部读写它；
任何页面切换实例都只调 `TavernInstances.Use(...)`，界面刷新统一由 `Changed` 事件驱动。

> 不要在页面里另存一份实例列表或当前选择——历史实现中版本管理页与仪表盘各存一份，
> 结果一边切换另一边毫无反应。新增涉及实例的界面请直接读 `TavernInstances`。
> 本地实例是真实目录，清单落盘在 `data/local_instances.json`（只存路径），
> 版本与依赖状态每次现读磁盘，见下方「本地实例（扫描 / 导入）」。

## 设置持久化

偏好只有一份状态：`Services/LauncherSettings.cs`（内存），落盘只有一个出口：
`Services/SettingsStore.cs` → `%AppData%\AstraBrew Launcher\settings.toml`。

- 页面改设置只调 `LauncherSettings.SetXxx(...)`，**不要自己读写文件**；
  写盘由 `SettingsStore` 监听 `Changed` 完成（400ms 防抖，先写 `.tmp` 再改名，掉电不留半截文件）。
- TOML 由 `Services/Toml.cs` 自己读写，不引入第三方库：Release 开了 `PublishTrimmed`，
  反射式反序列化会被裁掉；而且自己写才能保留分组注释。读写只覆盖「`[section]` + `key = value`」
  这一层，够用就好，加复杂结构前先想清楚是否值得。
- 值一律存人能读懂的字符串（`theme = "dark"`、`font = "Microsoft YaHei UI"`），**不要存枚举数字或下标**——
  枚举顺序一变、候选表一变，老配置就全错位。枚举 ↔ 文本的映射表在 `SettingsStore` 顶部。
  字体是唯一例外：它存的是字体族名本身（`"system"` 表示跟随系统，`"embedded"` 表示随包内置字体），
  本机没装的字体会在设置页标成「（未安装）」并回退，见 `FontCatalog`。
- 退出前 `MainWindow.OnClosed` 必须先 `_placement.PersistBounds()` 再 `SettingsStore.SaveNow()`：
  窗口几何是静默写入的（不发 `Changed`），漏了这一步「记住窗口位置」永远存不下来。
- 设置页右上角的「已保存」徽章读 `SettingsStore.Saved` 事件，不要再用定时器假装保存成功。

## 设置版本（小白版 / 高手版）

设置页有两种呈现，和酒馆配置页同一套交互：`Services/LauncherSettings.cs` 的 `Level`
（落盘 `[ui] level = "simple" | "advanced"`，默认 `simple`）。

- 两版共用同一份状态，**切换只改可见性，不改任何值**：小白版里藏起来的项仍按原设定生效。
  页面侧逻辑集中在 `Views/SettingsPage.xaml.cs` 的 `ApplyLevel()`，新增可隐藏的行/组时
  给元素起个名字并在这里登记，不要另写一份「小白版页面」。
- 小白版去掉：界面里的缩放与字体、基础里的 CPU 核心/服务器模式/数据存放/全局数据目录、
  整个控制台与原型工具、网络里的代理；运行环境收成一行结论（Git / Node.js / WebView2
  齐全 = 正常，缺任意一项 = 环境缺失 + 「修复」按钮逐个补齐）。
- 条件行（服务器模式的子行、自定义代理）由 `ApplyConditions()` 再夹一层 `!simple`：
  旧配置里开着它们，也不能在小白版下把子行顶出来。分隔线跟着所属行一起收起，
  否则卡片里会留下孤立的一条线。
- 依赖行跟着前置条件收放（`ApplyEnvironment()`）：npm 包源只在 Node.js 已就位时出现，
  Node 未装或还在探测时连同分隔线一起收起，别让人给不存在的运行时挑包源。

## 外观三项（主题 / 字体 / 缩放）

统一由 `Services/AppearanceController.cs` 在窗口就位后应用，任何页面都不要自己改。

- **主题**：设内容根的 `RequestedTheme`（`Application.RequestedTheme` 只能启动前设一次）。
  改完要回调 `MainWindow.ApplyTitleBarColors()`，否则系统绘制的窗口按钮停留在旧主题的悬停色。
- **字体**：改 `ThemeDictionaries` 里的 `ContentControlThemeFontFamily` 只管「之后新建」的元素；
  已建好的元素必须遍历可视化树补一遍，两者缺一个就有一半界面不跟着变。
  写 `Application.Resources` 顶层无效（主题字典优先级更高），字体也**不能**在 `App` 构造函数里改
  ——那会让启动直接 fail-fast（无托管异常、无日志）。图标字体与 `Consolas` 保持不动。
  下拉项（`ComboBoxItem`）跳过不刷：字体选择器用每项自己的字做预览，覆盖了就看不出观感。
  候选表由 `Services/FontCatalog.cs` 用 GDI `EnumFontFamiliesEx` 枚举——**不要**改成翻字体目录猜文件名，
  文件名（msyh.ttc）和 XAML 要的族名（Microsoft YaHei UI）不是一回事；也**不要**写死几个方案。
- **内置字体**：`Assets/Fonts/` 里的 4 个 ttf 随包分发，是出厂默认（配置值 `embedded`），
  用 `new FontFamily("ms-appx:///Assets/Fonts/<file>.ttf#<族名>")` 引用，
  **不要求本机安装**（实测 Debug 非打包构建也能加载，靠的是 csproj 里 `Content` 复制到输出目录）。
  三个坑：
  1. URI 里 `#` 后面必须是 ttf **name 表里的族名**，不是文件名。这款字体登记了两个名字，
     zh-CN 是「有爱魔兽圆体 CN」、zh-TW/HK 是「有愛魔獸圓體 CN」，两个都要认。
  2. 文件名保持 ASCII（用 PostScript 名）。中文文件名要 URI 转义，出错时没有任何报错信息。
  3. 族名对不上时 XAML **静默回退**到系统字体，不抛异常、不留日志。判断有没有生效：
     看 `FontFamily.Source` 是保留了 URI 原串（没解析）还是变成了族名，或者直接和
     `font = "system"` 的截图对比字形。
  多字重：XAML 一个 URI 只认一个文件，SemiBold/Bold 由框架合成。打包 Light/Medium/Bold
  是为将来加字重控制留的，当前不参与渲染。
- **缩放**：内容层 `ScaleTransform` + 宽高按 `1/scale` 反向补偿；标题栏不参与缩放，
  否则会和系统窗口按钮错位。弹窗挂在 XamlRoot 上，不随缩放变化（按 100% 呈现）。

## 内置环境安装

第三方组件的下载、解压、落位统一走 `Services/ComponentInstaller.cs`，
每个组件在 `Services/*Installer.cs` 里只声明差异项（`InstallSpec`：版本、源、安装目录、
校验函数、被占用时的说辞），**不要**再复制一份安装流程。
当前已接入 `MingitInstaller`（`bin\mingit`）与 `NodejsInstaller`（`bin\nodejs`）。
界面只做两件事：把进度画出来、把结果翻成一句话。

- **镜像优先、直连兜底**：Git 走 华为云 → npmmirror → GitHub 直连；
  Node.js 走 华为云 → npmmirror → 阿里云 → 官方直连。单个源失败静默换下一个，
  只有全部失败才对用户说话——国内直连 GitHub / nodejs.org 十次有八次超时，别让它排第一。
- **官方 Node.js 包多一层顶层目录**（`node-v22.20.0-win-x64\`），解压时按
  `ComponentInstaller.DetectRootPrefix` 剥掉；MinGit 是平铺的，前缀为空走同一条路。
  剥完 `node.exe` 必须落在 `bin\nodejs\node.exe`，`RuntimeProbe` 就查这个位置。
- **日志统一走** `Services/InstallLog.cs` → `%AppData%\AstraBrew Launcher\logs\<task>-<时间戳>.log`。
  换源细节、HTTP 状态、异常堆栈全进文件；界面只留一句能操作的话
  （「连不上下载源，请检查网络后重试。」），**不要**把日志正文糊进弹窗或 InfoBar。
- **解压先落暂存目录** `bin\.<name>-staging`，校验关键文件（如 `cmd\git.exe`）存在后再
  `Directory.Move` 到正式目录。中途失败只清暂存目录，绝不在正式目录留下半个环境——
  半成品比没装更糟：探测能过，一跑就报错。
- **超时分两段**：不用 `HttpClient.Timeout`（大包会把正常慢速判死），用「连接 20s + 连续 25s
  无数据」；每次读到数据就重置。进度回调必须限流（约 120ms / 每 1%），
  81920 字节一个 chunk 不节流会压满 UI 线程。
- **代理跟随设置页**（`LauncherSettings.Proxy`：无 / 系统 / 自定义），统一由
  `Services/ProxyHttpClient.cs` 创建 `HttpClient`；自定义地址解析失败时退回系统代理。
- 失败原因用 `InstallError` 枚举表达，界面按枚举挑文案，**不要**去解析异常字符串。
- **npm 版本读 `package.json`**，不跑 `npm --version`：内置环境读
  `bin\nodejs\node_modules\npm\package.json`，系统环境读 `<node 目录>\node_modules\npm\package.json`
  与 `%ProgramFiles%\nodejs\node_modules\npm\package.json`。`.cmd` 得起 `cmd.exe`，
  为一个版本号拉子进程不值。设置页里「npm」行显示版本，「npm 包源」是另一行（选源），
  两行都跟着 Node.js 是否存在一起收放。

## 源码下载通道

酒馆源码只能有一个来源入口：`Services/TavernSource.cs`。仓库地址、加速前缀、
回退顺序都在这里拼，设置页显示、后续的拉取与更新一律从它取，**不要**在别处再写一遍 URL。

- 三个通道按固定顺序出现在分段切换器里（枚举顺序＝界面顺序，别乱调）：
  `Gitee`（`AstraBrew-Labs/SillyTavern`）、`GitCode`（`GitHub_Trending/si/SillyTavern`）、
  `Github`（`SillyTavern/SillyTavern`）。默认 `GitCode`。
- **加速开关是全局能力，不是某条通道的附属品**：判断与拼地址只在 `Services/GithubAccel.cs`
  一处（`Apply` / `Targets` / `ExpandSources`）。凡是 GitHub 链接——版本列表接口、Git 安装包、
  扩展克隆、将来的 Raw 资源——都先过它，**只看开关，不看下载源选了什么**。
  因此加速行**常显**，选镜像时也不收起。
- **一个开关管两条路，别拆成两个开关**：
  - 应用内 HTTP / 下载 —— `GithubAccel` 在地址前拼 `gh-proxy.org`；
  - 命令行 git —— `Services/GitGlobalConfig.cs` 把 `insteadOf` 写进 `~/.gitconfig`。
  命令行拿到的是干净的 `https://github.com/…`，拼不了前缀，只能让 git 自己改写；
  酒馆自动更新、酒馆里装扩展都靠这条。两者**必须同开同关**，否则会出现「界面显示已加速、
  酒馆更新却还是直连」的错位。**不要再给全局 git 规则单独做一个开关。**
- **用法照官方文档**（gh-proxy.com/docs/github-accelerator）：把完整 GitHub 资源 URL
  **原样**拼在中转域名后面，路径与查询串都不改，例如
  `https://gh-proxy.org/https://github.com/o/r/releases/download/v1/x.zip`。
- **只加速 GitHub 自己的域名，别的站拼前缀必报错**：`AccelerableHosts` 是一张**白名单**，
  按文档「支持的资源格式」逐条对应，**不要**改回后缀匹配——后缀匹配早晚把长得像 GitHub 的
  域名放进来，而这个错误的表现是「用户装不上」。名单分四组：
  - 主站与接口：`github.com`（仓库页、Releases、archive、raw 路径、blob、Actions 产物、
    clone、patch/diff、atom）、`api.github.com`、`codeload.github.com`；
  - 文本资源：`raw.githubusercontent.com`、`raw.github.com`（历史域名）、
    `gist.githubusercontent.com`、`gist.github.com`；
  - 短链：`gh.io`（http / https 都算）；
  - 静态资产：`avatars.githubusercontent.com`、`opengraph.githubassets.com`、
    `github.githubassets.com`、`desktop.githubusercontent.com`，以及下载 302 的落点
    `objects.githubusercontent.com`、`release-assets.githubusercontent.com`。
  不在名单里的地址一律原样返回、零改动——Gitee / GitCode 镜像、npmmirror、华为云、
  nodejs.org、npm 源、本地地址都是这一类。
- **两个中转域名分工不同，别混**：`gh-proxy.org` 是资源代理（文件、API、git 协议），
  `gh-proxy.com` 是主站 Web 转换器，只在「用浏览器打开仓库主页 / Releases 页面、由它列出
  资产」时才用。本项目只做程序化下载与克隆，统一走 `GithubAccel.Prefix`；
  `WebPrefix` 只用于识别「这条链接已经加速过」，不要拿它拼下载地址。
- **不自己重写路径**：`/blob/` 转 raw、`raw.github.com` 与 `gist.github.com` 的规范化、
  下载 302 的跟随都由中转端做（文档第 3、7 节）。我们只拼前缀，改路径会和它打架。
- **唯一例外是内置实例克隆仓库**：`TavernSource.InstallTargets()` 里只有下载源选
  `Github` 才给「加速 → 直连」两个候选；选镜像且镜像里有该版本就只走镜像，镜像里没有则退回
  GitHub **直连、不加速**（用户没选过直连，不替他决定要不要中转）。想用加速就把下载源切过去。
  注意：加速开关打开时全局 `insteadOf` 已经生效，**即使这里给的是直连地址，git 也会把它改写成
  加速地址**——这是开关打开后的预期行为，不要当成 bug 去「修」。
- 一律「加速在前、直连在后」：中转挂了静默退直连，不能因为开了开关反而装不上。镜像失败不自动
  跳别的仓库，否则用户会拿到一个不是自己选的版本。
- 别在别处再写一次 `gh-proxy.org`：前缀是 `GithubAccel.Prefix`，已带前缀的地址不会再套一层；
  任何「自己拼前缀」的写法都绕过白名单，等于把报错机会留给用户。
- 配置文件里的 `download_channel` 只有 `gitee / gitcode / github` 三个合法值，
  **不留旧值兼容**：读不到就按默认 `GitCode` 处理，不写迁移分支。

### 全局 git 规则（insteadOf）

加速开关打开时，除了应用内拼前缀，还要让**命令行里的 git** 也走加速，否则酒馆自动更新、
酒馆里装扩展这类「不在我们手里发请求」的场景会漏掉。做法照 GH-Proxy 文档的
「Git 全局自动替换 (insteadOf)」：

```
git config --global url."https://gh-proxy.org/https://github.com/".insteadOf "https://github.com/"
git config --global --unset url."https://gh-proxy.org/https://github.com/".insteadOf
```

- 全部逻辑只在 `Services/GitGlobalConfig.cs`：`ApplyAsync` / `RevertAsync` / `IsAppliedAsync` /
  `SyncAsync(enabled)`。**不要直接读改 `~/.gitconfig` 文本**，让 git 自己去写。
- 键名里带完整 URL（`url.<前缀>https://github.com/.insteadOf`），所以它天然只命中 github.com，
  相当于一张白名单；Gitee / GitCode / 自建服务、`git@github.com:` 这类 scp 写法都不会被改写。
- 写入用 `--replace-all`：反复开关不会攒出一串重复项。移除用 `--unset-all`——
  用 `--unset` 在存在多值时会失败并**把规则留在配置里**，那时用户以为已恢复直连、实际还在加速。
- `git config --unset` 在键不存在时返回 5，等价于「已经是目标状态」，按成功处理；其余非 0 才算失败。
- 以 **git 实际配置为真相源**：进设置页跑一次 `SyncAsync` 对齐（用户可能在终端里手动 `--unset`
  过、或换过机器），开关状态始终取加速开关本身，不用另一个布尔值记录。
- 失败不回滚开关：加速服务对应用内请求仍然有效，只是命令行那条路没铺好，用 Warning 说明；
  关闭时删不掉规则更严重——git 会继续走加速，用 Error。
- 没有 Git 时不报错也不阻断：规则写不上，说明里写清「命令行仍是直连」。

## 内置实例安装

`Services/TavernInstaller.cs` 是唯一的安装入口：`git clone --depth 1 --single-branch
--branch <tag|staging>` 到暂存目录 → 校验 `package.json` → 改名落位到
`%AppData%\AstraBrew Launcher\sillytavern` → `npm install`。**依赖装完才算安装成功**：
代码在、依赖缺时 `TavernInstances.BuiltinDepsReady == false`，按钮变成「安装依赖」。

一颗按钮三种身份，由 `Views/VersionPage.xaml.cs` 的 `UpdateSwitchButton()` 决定：
`安装 vX`（没代码）/ `切换到 vX`（有代码但不是这个版本）/ `安装依赖`（代码就是这个版本、
只缺 `node_modules`）。「安装依赖」只跑 npm，不重下源码。

- **先查环境再动手**：Git（拉源码）与 Node.js（跑酒馆 + 提供 npm）任一缺失就弹窗，
  主按钮落到「设置 → 运行环境」（`SettingsPage.OpenRuntimeSection()` + `StartBringIntoView`）。
  装完启动不了比装不上更气人，所以不硬装。只补依赖时（`needGit: false`）不查 Git。
- **镜像比对决定从哪拉**：`Services/TavernMirrors.cs` 用一次 `git ls-remote --refs <镜像地址>`
  取回镜像的全部分支与标签（内存缓存 30 分钟，失败缓存 60 秒），再和当前选的版本比；
  有同一个标签 → 界面显示「镜像可用」并走镜像，没有 → 显示「直连安装」并退回 GitHub。
  选的是 `Github` 通道时不显示「镜像可用」，只有「直连安装」。
  不走「按版本逐个探测」——二三十个版本就是二三十次往返。
- **换版不先删旧版**：旧目录先改名到 `.sillytavern-old`，新代码就位后才删它；
  落位失败会把旧目录原样挪回。**删目录必须走 `DeleteTree`**：Git 把 `.git/objects/pack`
  设成只读，直接 `Directory.Delete(recursive:true)` 会中途抛 `UnauthorizedAccessException`，
  留下删了一半的目录（这条是实测踩出来的）。
- **装完以磁盘为准**：`TavernInstances.ReloadBuiltin()` 重读 `package.json` 的 `version`，
  失败路径也要调一次——回滚过的目录只能由磁盘说了算。
- **日志**：`logs\sillytavern-<时间戳>.log`，含最终选用的源、`git` 原始输出与耗时；
  远端 `Counting/Compressing objects` 的百分比行不进日志（一秒几十行，只会冲掉有用信息）。
  界面进度按 `Receiving objects → Resolving deltas → Updating files` 三段映射，回调限流 1%。
- **依赖装在哪、怎么装**：代码落位之后在安装目录里跑
  `node <node 目录>\node_modules\npm\bin\npm-cli.js install --omit=dev --no-audit --no-fund
  --loglevel=http --registry=<设置里的源>`。`node_modules` 有几万文件，放暂存目录再整体改名只会更慢；
  而且依赖失败时留下的正是「有代码、缺依赖」，用户点一次「安装依赖」就能修好。
- **不改全局 npm 配置**：源用 `--registry` 传参，只作用于这一次进程，绝不写 `~/.npmrc`。
  也不要改成 `npm config set`——那会悄悄改掉用户自己的环境。
- **npm 要跑 node 而不是 npm.cmd**：`.cmd` 是批处理，重定向时得先起 cmd.exe，
  退出码与编码都会拐一道弯；直接用 node 跑 `npm-cli.js` 拿到干净的行流。
- **依赖阶段的进度**：npm 没有可信百分比，界面用不确定态；文案按「解析依赖（packument 计数）→
  安装依赖（tar 包计数）」推进，另有每秒一次的心跳把耗时写进文案——npm 解包时完全不出声，
  没有心跳界面会像卡死。
- **失败语义**：`TavernInstallResult.DepsReady` 区分「代码在、依赖缺」与「彻底没装」。
  前者 InfoBar 用 Warning（依赖没有装好），后者才是 Error（安装失败）。

## 版本列表（内置实例）

`Services/TavernReleases.cs` 是版本列表的唯一入口，仓库页只调 `GetAsync`，不要自己拼 API 地址。

- 接口固定为 `TavernReleases.ApiUrl`（`/releases?per_page=30`），地址经
  `GithubAccel.Targets()` 展开：加速开着先 `gh-proxy.org` 再直连，关着只直连。
  与下载源选的是镜像还是直连**无关**——版本列表始终在 `api.github.com`。
- **加速是独立出口，不是同一条线路的备胎**：中转端有自己的 Token 池，和直连那
  60 次/小时互不相干。所以某条出口返回 403/429 时**必须继续试下一条**，
  不能像以前那样一句「换直连也一样」直接放弃——那正好把这个功能废掉了。
  只有所有出口都被拒才报限流，并在文案里带上 `Retry-After` / `X-RateLimit-Reset`
  换算出的恢复时间；加速关着时提示去设置里打开。
- **缓存是「不要频繁请求」，不是「永远不请求」**：`CacheTtl` 15 分钟内直接用，
  过期在页面打开时自动校验一次；失败后 `FailureCooldown` 5 分钟内不再自动重试
  （手动刷新不受限制）。
- **条件请求不花额度**：缓存里存 GitHub 的 `ETag`，下次带 `If-None-Match`；
  304 按 GitHub 规定不计入限额，所以「经常校验」和「省额度」可以同时成立，
  304 时把 `FetchedAt` 刷新为当前时间，界面显示「已更新于」而不是「缓存于」。
- 缓存路径：`%Temp%\astrabrew-launcher\caches\sillytavern-releases.json`，内存再缓存一层。
  缓存是中间产物（删了只是多请求一次），所以放临时目录而不是 AppData；路径统一取
  `AppPaths.CacheRoot`，不要在页面里自己拼。
- 刷新失败时继续显示旧缓存，并把原因折成一句话给全局消息；**不要**清空列表或弹堆栈。
  手里还有列表时标题写「刷新失败」，一条都没有才写「版本列表不可用」。
- 发布说明是 Markdown，展示前只做最小整理（去 `#`、`*` 换 `•`、压空行），不引渲染库。
- 内置实例的版本优先读 `sillytavern\package.json` 的 `version`；读不到才用
  `TavernInstances.DefaultVersion` 占位。元信息（日期 / 标签）只写发布列表里真有的事实，
  **不要**编体积和文件数。
- 没装（`TavernInstances.BuiltinInstalled == false`）时界面写「未安装」：不显示版本号、
  不显示元信息胶囊、不出现「使用中」，下拉框也不给任何版本打「当前」。
- 有代码、缺依赖（`BuiltinInstalled && !BuiltinDepsReady`）时卡片挂「待装依赖」胶囊，
  隐藏「使用内置实例」（切过去也起不来），仪表盘版本说明写「缺少依赖」。
  这类实例不能成为当前实例（`TavernInstances.Use` 与仪表盘下拉都会拦）。

## 本地实例（扫描 / 导入）

本地实例是**真实目录**：进列表只有一条判据——目录里的 `package.json` 的 `name` 等于
`sillytavern`。目录叫什么、有没有 `config.yaml` 都不作数。判据只在
`InstanceScanner.TryReadManifest` 一处，扫描、手动导入、启动时恢复清单一律走它，
免得三处各写一套、慢慢走偏。

- **引擎与会话分开**：`Services/InstanceScanner.cs` 是纯扫描逻辑（无 UI、可独立跑），
  `Services/InstanceScan.cs` 是「本次扫描」的状态机。页面与弹窗都只读
  `InstanceScan.State`，不自己算进度。
- **扫描在后台任务里跑，弹窗只是观察窗口**：点「扫描」= `InstanceScan.Start()` + 开弹窗；
  关掉弹窗不中断，扫描期间再点按钮只把弹窗重新打开（`IsRunning` 时不重复起）。只有
  「取消扫描」才停。取消/失败都保留已找到的实例——扫到的东西没理由因为取消就丢掉。
- **线程按盘分配**：总预算由「设置 → 占用 CPU 核心」决定（`ResolveBudget`），三档的区别是
  **给系统留多少**——自动 = 留一点给系统（8 核以上留 2，小机器留 1）其余全用来扫；
  一半 = 对半分；全部 = 逻辑核心全开（IO 密集型任务抢不卡系统）。
  再用 `AllocateThreads` 均分到每块盘、余数从前往后各加一条，**合计严格等于预算**
  （向上取整的均分会让 4 盘 × 4 = 16 超出自动的 14）。每盘至少一条，单盘上限 16。
  弹窗右上角把「N 条线程 · 自动 · 12 核留 2 给系统」直接写出来，让人看得出设置生效了。
  只扫 `DriveType.Fixed` 且已就绪的盘，再用 `PhysicalDrive.Describe` 问一遍总线类型，
  USB / 1394 / 读卡器一律不扫——外接硬盘常被系统报成「固定磁盘」，插着不动跟内置盘没差别，
  但拔掉就没了。硬盘是 IO 密集型，别为了「看起来快」把线程数调大，一块盘上多出来的线程只是排队。
- **机械盘要单独说明**：`PhysicalDrive` 用 `IOCTL_STORAGE_QUERY_PROPERTY` 的
  `StorageDeviceSeekPenaltyProperty` 问「有没有寻道代价」——机械为真、固态为假，问不到再看型号里
  有没有 SSD / NVMe，仍问不到就是 `Unknown`（不猜）。扫描范围里出现机械盘时，弹窗挂一句
  「机械硬盘的读取速度较慢，请耐心等待」：固态上十几秒的事，机械盘可能要好几分钟，
  不解释一句就会被当成卡死。句柄用 0 权限打开，普通用户就能问，不必提权。
- **排除规则要保守**：跳过错了就是用户看不见自己的实例。名单只有两类——
  按名字（系统目录、`$Recycle.Bin`、`node_modules`/`.git`、各种 Cache/Temp）与
  按根（`%LocalAppData%\Temp|Packages|Microsoft`、`WinSxS`、`TempRoot`、内置实例目录、
  暂存目录）。重解析点（junction / symlink）一律不跟进去，否则会绕回上层或连到别的盘。
  一台开发机上九成以上的目录数都在 `node_modules` 与各种缓存里，跳过它们才有可能几十秒扫完。
- **命中就进列表**：`onHit` 在工作线程回调，`InstanceScan` 转投到 UI 线程再调
  `TavernInstances.AddLocal`，因此扫描期间列表是实时长的，不用等全盘扫完。
  `InstanceScan.Changed` 一律在 UI 线程触发，订阅者可以直接改控件。
- **扫完的盘把线程让出来（工作窃取）**：`Work(home)` 里本盘 `Pending` 归零就不再从本盘取活，
  改由 `FindVictim()` 挑积压最多的盘借目录处理，全部盘都完了才退出。固态盘通常先完事，
  它的核心于是转去啃机械盘，整轮结束时间由最慢的盘决定，不该让快的盘提前下班。
  进度、命中、当前目录一律记在**目录所属的那块盘**上（借来的目录也用 victim 的 state 处理），
  否则「这块盘扫到哪」又会变成别人的路径。每盘 `Workers`（含支援）与开局分到的 `Threads`
  是两个数，弹窗在还在扫的盘上直接写「已查 N 个 · M 线程」，让空闲核心的去向看得见。
- **进度要有分母才算进度**：目录总数扫完才知道，所以 `ScanStats` 把每块盘上次扫完的数记在
  `data/scan_stats.json`，下次开扫就有分母。本次发现数超过它时只往上抬（`Expected` 只增不减）。
  没有记录（首次）时 `Estimating` 为真：环转圈、右边写「正在估算时间…」，不拿「已发现」当分母
  画弧——那个分母一直在长，弧会先冲高再冻住，比转圈更让人以为坏了。扫完一律画满环；
  但「取消 / 失败」只画已扫到的比例，给个满环再配「已停止」是自相矛盾的。
- **环的百分比只涨不跌**（`ScanDriveRow.Floor`）：分母还在长的时候比例会往回掉，环倒退看起来像卡死。
- **剩余时间取最慢的那块盘**：几块盘并行扫，整场结束时间由最慢的一块决定，把剩余时间加起来只会吓人。
- **一眼看得懂的两处显示、一份数据**：列表顶部不再放摘要条（扫到哪了、找到几个这些话由右下角的
  常驻消息一直说着），只留一个进详情的图标按钮；弹窗只放消息里没有的细节——每块盘各自的
  进度环、剩余时间、此刻的目录。弹窗不持有任何进度，`Render()` 整块重画，行对象复用只改数值
  （换新对象会让 `ProgressRing` 每次重建，动画一直从头开始）。
- **导入选的是 `package.json` 而不是目录**：判据就在这个文件里，选文件等于把判据摆出来，
  也省得替他猜 package.json 在哪一层。校验走 `TavernInstances.AddLocal`，用
  `LocalAddStatus` 区分「已在列表 / 是内置实例 / 不是酒馆 / 路径读不到」，界面按枚举挑文案。
- **内置实例目录直接拒绝**（`IsBuiltinPath`）：它已经由「内置实例」页签托管，再加一份会出现
  两个入口指向同一份代码。扫描时也把它整体排除，扫出来再拒绝一遍纯属浪费。
- **去重按归一化路径**：`PathMatch.Normalize`（绝对路径 + 去尾分隔符）+ `OrdinalIgnoreCase`，
  不按目录名、不按版本号——同一份代码被扫两次是最容易犯的错。
- **清单只存路径**（`data/local_instances.json`，先写 `.tmp` 再换名）：版本、依赖一律现读磁盘，
  用户 `git pull` 过之后界面才是对的。启动时逐条复核（目录在 + 仍是酒馆），失效的静默丢弃；
  用户自己移动目录是很正常的事，不该弹错误。
- **本地实例补依赖**走 `TavernInstaller.InstallDependenciesAsync(root, …)`：装在该实例自己的
  目录里，不碰内置实例那一份。装完一律 `TavernInstances.ReloadLocal` 以磁盘为准，
  不拿返回结果里的标记当真。
- **各行的 `Node` 是同一套运行环境**：启动器只有一个 Node（设置里选内置还是系统），
  不是每个目录各自带一个。探测一次后由 `SetRuntimeLabel` 铺到所有行，探不到就写「Node 未检测」，
  不要按目录名猜版本。

## 全局酒馆配置（首次获取）

**生效与操作的只有全局那一份**：`data/sillytavern/config.yaml`。读取、修改、覆盖、删除都针对它，
由 `Services/GlobalTavernConfig.cs` 的 `ActivePath` 统一给出，其它地方不要自己拼路径，
更不要因为全局缺失就退到默认路径去用。

默认位置 `data/default/sillytavern/config.yaml` 只是**恢复默认用的底稿**，不是生效配置：
全局缺失时先 `RestoreFromDefault()` 把它复制成全局（静默、不打扰用户），
底稿也没有时才联网取一份——取回先落默认位置、做几项开箱优化，再复制成全局配置。
此后用户改的都是全局那份，默认位置始终保持一份干净的底稿。

全局不存在且底稿也恢复不出来时，酒馆配置页盖一层遮罩（`ConfigMask`），只能点「获取默认配置」：
`FetchAsync` 按 `GithubAccel.Targets` 的顺序下载（**开着加速就先加速、失败回退直连**，
关着就只有直连），取回后先 `ApplyLaunchDefaults` 做开箱优化，写默认位置，再复制成全局配置。
优化只有四项，且**逐行改、不整份重写**（注释与其它选项原样保留）：
`port: 11451`、`listen: true`（允许局域网）、`protocol.ipv6: true`、白名单只留
`127.0.0.1` 与 `localhost`。只认顶格的 `port:` / `listen:` / `whitelist:`——
`browserLaunch.port`、`whitelistMode`、`whitelistDockerHosts` 这些都不能误伤。
拿到手要校验内容像 YAML（含 `whitelistMode` 与 `port:`），别把代理塞回来的错误页写进配置。
成功后遮罩撤掉，表单同步成同一组值（端口、局域网开、白名单调 `TavernSettings.ResetToLocalOnly`）。

「重置为默认」按钮走同一条回退链，但**不弹遮罩**：`ResetAsync` 有底稿就强制覆盖全局
（与 `RestoreFromDefault` 的「只在全局缺失时恢复」不同，重置本来就是要抹掉改动），
底稿不在就直接 `FetchAsync` 联网取。结果用 `GlobalConfigFetchResult.Downloaded`
区分「本地恢复」还是「联网下载」，消息里据此写清来源；期间只禁用按钮、改状态文案。

**导入配置**：选完 `config.yaml` 先问「覆盖还是合并」（`ContentDialog` 的
Primary=覆盖 / Secondary=合并 / Close=取消），两者都只写全局那一份：

- 覆盖 = `OverwriteGlobal`，整份替换，现有改动全丢；
- 合并 = `GlobalTavernConfig.MergeIntoGlobal` + `Services/YamlMerge.cs`：**以当前配置为基座，
  只补导入里「当前没有的项」**，当前已有的值一个不动。实现是整块搬运而不是解析成对象再序列化——
  官方配置大半是注释说明，重新序列化会把它们全丢掉。插入位置取「段内最后一个内容行之后」，
  不是段尾（段尾常是给下一个段做说明的注释）；缩进按父段现有子键对齐；
  类型对不上（当前标量 vs 导入是块）就保留当前，宁可少合也不产出坏 YAML。
- 导入前用 `LooksLikeConfig` 挡掉不像酒馆配置的 YAML（缺 `port` / `whitelistMode`）。

**配置页读写的是真实文件**（`Services/YamlConfig.cs`）：进页面 `LoadFromConfig()` 从全局配置读，
改动**自动保存**——没有保存按钮。用户在控件上动一下 → `MarkDirty()` 重启 700ms 防抖计时器 →
停手后才 `SaveToConfig()` 落盘（边打字边写文件没有意义）；离开页面走 `OnNavigatedFrom` 立刻补一次，
不能等 `Unloaded`（那之前窗口可能已经关了）。首次获取 / 恢复默认 / 导入之后都重新读一遍，
不能只改界面不上盘——否则重进页面又变回原样。写盘走 `YamlConfig.SetScalar/SetList` 的定点改写
（只改命中的那一行/那一段，注释与其它选项原样保留），路径是点分形式
（`listenAddress.ipv4`、`browserLaunch.enabled`）。加载期间用 `_loading` 标记屏蔽保存触发，
否则「把文件读进控件」会被当成用户改动又写回一遍。
已接的字段：`port`、`listen`、`listenAddress.ipv4|ipv6`、`heartbeatInterval`、
`browserLaunch.enabled|browser`、`whitelist`（整段替换，`localhost` / `127.0.0.1` 恒锁定）。

**高手版字段靠命名约定自动接入**（`Field_` / `FieldArray_` / `FieldEnum_` 前缀）：

| 名字 | 含义 |
|---|---|
| `Field_ssl__certPath` | 标量 → `ssl.certPath`，层级用 `__` 分隔 |
| `FieldArray_cors__origin` | 多行文本 ↔ 数组（`cors.origin`），空列表写回 `key: []` |
| `FieldEnum_thumbnails__format` | 下拉框 ↔ 关键字，`Tag="jpg,png,webp"` 提供索引到值的映射 |

类型由控件决定：`CheckBox` / `ToggleSwitch` → 布尔，`NumberBox` → 数字（`NaN` 时跳过，不把 `null` 写成 0），
`TextBox` / `PasswordBox` → 字符串。`EnumerateFields()` 从视觉树收集控件（折叠的分组也在树里），
`HookFieldEvents()` 统一挂变更事件——这些控件原本没有任何事件，不挂就不会触发自动保存。
新增高手版选项时**只要按约定起名**，读写与自动保存都会自动生效；没起名的控件不会被写回，
所以接一个字段前先确认它在官方 config.yaml 里的路径（别拿界面文案猜）。

**两套说明的写法不同，别混**：

- 高手版：说明照官方 `default/config.yaml` 的注释写（中文转述，不丢信息），末尾标出对应字段，
  例如「毫秒；同一用户两次聊天备份之间的最小间隔。对应 backups.chat.throttleInterval。」——
  用户拿记事本打开配置时能一一对上。裸 CheckBox 的说明放进 `ToolTipService.ToolTip`，口径相同。
  单位以官方为准：`heartbeatInterval` 是**秒**、`sessionTimeout` 是**秒**、`throttleInterval` 是**毫秒**。
- 小白版：只放日常几项，说明要口语化、不出现字段名与协议术语，
  例如「手机、平板访问酒馆时用的门牌号，一般不用改。」——两版的差别不只是数量，也是措辞。
尚未接入：缩略图尺寸（`thumbnails.dimensions` 是对象数组，需要索引写入支持）、内存缓存容量
（官方是 `'100mb'` 字符串，界面是数字框）。

## 控制台与酒馆进程

**运行状态只有一份**：`Services/TavernRuntime.cs`（进程、PID、端口、启动时间、日志）。
控制台页面只负责画，仪表盘的服务徽标、地址、运行时长、PID 也全部读它——
以前每个页面各自模拟一套，切换页面就自相矛盾。

- **启动顺序**：定环境来源 → 查 Node.js、Git 并逐行输出它们的版本 → 校验实例
  （`server.js`、`node_modules`）→ 读配置端口 → 查端口占用 → 组命令 → 起进程 → 轮询端口就绪。
- **环境来源不跨来源回退**：`RuntimeProbe.LocateNode/LocateGit(RuntimeSource)`——
  内置只看启动器 `bin/`（历史版本是 `lib/`），系统只看 PATH。选了内置却拿系统的 node，
  用户以为在用一个自包含的便携环境，实际并没有。无参版本（安装器用）才做「内置优先、再 PATH」。
- **Git 缺失只警告不拦截**：酒馆本体不依赖 Git，装扩展才用；缺了照样启动，但要把后果写清楚。
- **启动参数照旧版 `core/tavern_process.rs`**：`node server.js`；全局数据模式补
  `--configPath <配置> --dataRoot <目录>`；桌面模式补 `--browserLaunchEnabled false`
  （浏览器由启动器自己开）；有代理时补 `--requestProxyEnabled/Url/Bypass` 并注入
  `HTTP(S)_PROXY`。代理来自自定义设置，系统模式读 WinINET 的 `ProxyEnable/ProxyServer`。
- **PATH 必须前置 node 与 git 目录**：酒馆的子进程（npm、扩展安装脚本）靠它找工具。
- **状态通知一律转投 UI 线程**（`Raise()` 内部走 `Post`）。启动流程在线程池上跑，
  直接 `Invoke` 会跨线程改控件抛 COMException，而且抛在事件里没人接得住，
  表现成「状态一直停在启动中、PID 永远是 —」。
- **就绪靠连端口判断**，不靠日志里那句 listening：用户换端口、换日志格式都不该影响判定。
- **停止先礼后兵**：`taskkill /T` 不带 `/F`，taskkill 报失败（控制台程序没有窗口，几乎必然）
  或 3 秒内没退出就升级 `/F`。别空等 6 秒——那段时间界面上只会显示「停止中」。
- **退出启动器要收子进程**（`MainWindow.OnClosed` → `TavernRuntime.Shutdown()`，按
  `stop_tavern_on_window_close`）：运行时活在静态状态里，不收就是孤儿进程一直占着端口。
  用同步杀进程树，窗口一关事件循环就没了，异步收尾跑不完。
- **日志**：stdout/stderr 逐行收、去 ANSI 色码、上限 2000 行，存在服务层——
  切页面回来还能看到历史。空日志区会先放一行「控制台就绪」，否则像是输出没接上。
- **自动跟随别用「离底部多远」判断**：酒馆刷屏时内容一直在长，会把自己一路判成
  「已暂停跟随」。记下自己滚到的位置（`_followTarget`），明显低于它才是用户往上翻了。
- **白名单只认 IP / CIDR**：`localhost` 会被 SillyTavern 判为 `Invalid IP` 直接忽略，
  而浏览器打开 localhost 常走 IPv6 回环，缺 `::1` 就会「服务起来了、页面被挡住」。
  `GlobalTavernConfig.NormalizeWhitelist()` 在起服务前把主机名换成 `::1` 并补齐回环地址，
  改动会在日志里说一句；默认配置也直接写 `127.0.0.1` + `::1`。
- **设备访问弹窗的地址来自网卡**（`NetworkInterface`，取已连接网卡的全局单播地址），
  别再写死示例 IP；二维码占位已去掉——扫不出东西的码比没有更糟。

## 仪表盘（真实数据）

仪表盘上**没有样例数字**：每一格都来自真实来源，切换页面 / 改设置 / 启停服务后都跟着变。

| 位置 | 来源 |
|---|---|
| 状态、PID、运行时长、启动耗时 | `TavernRuntime.State`（`StartedAt` / `StartupSeconds`） |
| 内存、CPU | `TavernMetricsService.SampleProcess()` 读子进程（CPU 要两次采样，第一秒显示「—」） |
| 今日访问 | 酒馆自己写的 `<dataRoot>/access.log`（按本地日期数行）；日志没开就显示「—」并提示原因 |
| 已装扩展 | 内置 = `<实例>/public/scripts/extensions/*/manifest.json`；第三方 = `third-party` + `<dataRoot>/extensions` + `default-user/extensions`，按目录名去重 |
| 角色卡 | `<dataRoot>/default-user/characters` 的**顶层** png——同名目录里是表情差分，递归数会把一个角色数成二十几张 |
| 磁盘占用 | 安装目录 + 数据目录的总字节（后台遍历，20 秒缓存，刷新按钮强制重算） |

**联动规则**（这是本轮的重点，改之前先读）：

- **端口只有一份**：`TavernRuntime.CurrentPort`。运行中取实际监听的端口，停下就回读
  `config.yaml`——只看快照里的 `Port` 会让「改成 8080 后仪表盘还显示 11451」。
- **配置写盘后要通知**：`GlobalTavernConfig.Saved` 在每次原子写之后触发（保存 / 导入 / 恢复默认 /
  首次获取），仪表盘据此重读端口与访问地址。订阅方可能不在 UI 线程，自己转投。
- **启动模式只有一个状态**：`TavernSettings.Mode` 是 `LauncherSettings.StartMode` 的视图
  （两个枚举顺序一致，直接按序号换算），存 `settings.toml`。设置页的「默认启动方式」也是三项，
  在任一页改，仪表盘、酒馆配置页、启动器实际行为同时变——以前这里是两份状态，
  设置里选桌面模式、仪表盘还显示普通模式、启动时按第三份来。
- **浏览器与服务范围属于酒馆配置**：改了要 `GlobalTavernConfig.PatchScalar(...)` 写回
  `config.yaml`（`browserLaunch.browser` / `listen` / `listenAddress.ipv4`），启动时
  `TavernSettings` 再从文件读回来。官方默认值是 `browser: default`（跟随系统），
  界面用「三个 chip 都不选中」表示它，别把 `default` 静默改成 `chrome`。
- **环境来源 / 数据存放只读展示**：`RuntimeEnvText` 显示内置/系统环境与 Node 版本，
  `DataModeText` 显示数据存放策略，点开 Tooltip 给路径。要改请去设置页。
- **主按钮随状态变**：没在跑是「启动服务」，跑起来才是「重启服务」；「停止」只在运行时出现。
  停止状态点「重启」什么都不会发生，那是以前的问题。

**快捷操作是真的会做事**：打开酒馆走 `BrowserLauncher.Open`（按 App Paths 找 chrome/edge/firefox，
找不到退回系统默认浏览器）；数据目录开资源管理器；导入角色卡把选中的 PNG/JSON 复制进
`characters`（同名加 `(2)` 后缀），复制完刷新指标。地址一律取自
`NetworkInfo.LocalAddresses()` + 实际端口，别再写示例 IP。

### config.yaml 写入的三个坑（都踩过）

1. **多行文本框必须按 `\r` 与 `\n` 一起切**（`SplitLines`）。WinUI 的回车可能只留 `\r`，
   只按 `\n` 切会把整段当一条，写出来是 `- 'localhost\ncdn…'` 这种跨行标量，酒馆直接解析失败。
2. **`YamlConfig.Format` 先把换行压成空格**。这个模块按行改，值里带换行必然产出坏 YAML；
   宁可压平也不能写坏配置。
3. **加载期间不许置脏**（`MarkDirty` 在 `_loading` 时直接返回）。否则「只是打开配置页看一眼」
   也会在离开时触发整份保存，把 `browser: default` 改成 `chrome`、把数组重排——用户没改任何东西。

## 扩展管理（真实环境）

页面上的每个数字都对得上磁盘，没有样例扩展。数据层是 `Services/TavernExtensions.cs`。

- **落点有四处**，顺序即优先级：`<实例>/public/scripts/extensions/third-party`（安装默认位置）→
  `<实例>/data/default-user/extensions` → `<ResolveUserRoot>/extensions` → 自带
  `<实例>/public/scripts/extensions`。同一份扩展可能在两处都有，**按目录名去重，先出现的赢**。
  自带目录里只认带 `manifest.json` 的子目录（`assets` 这类纯资源目录不是扩展）；
  第三方目录连没有清单的一起列出来并标「清单损坏」，否则用户没法删掉装坏的残留。
- **开关状态只有一处真相**：`<ResolveUserRoot>/default-user/settings.json` 的
  `extension_settings.disabledExtensions`（数组，元素是目录名）。酒馆自己就是这么判的。
- **只改那一个数组**：`SetEnabled` 走 `JsonArrayEditor` 按字符扫描替换，绝不 `JsonNode.Parse` 再整体写回——
  settings.json 有一百多个字段，整体反序列化会改写数字格式、把中文转成 `\uXXXX`，
  一份用户配置为加一个名字被改了样。
- **缩进要从 `\n` 之后开始量**（已踩）：`IndentOf` 一开始写成 `json[lineStart..index]`，
  把行首那个 `\n` 也算进了缩进，写出来是 `[\r\n\n  "x"` 的空行。要写成 `json[(lineStart + 1)..index]`。
- **删除走回收站**（`ShellFile.MoveToRecycleBin`，`SHFileOperation` + `FOF_ALLOWUNDO`），
  `Directory.Delete` 一按就没了。酒馆正在运行时删不掉（文件占用），把失败原因原样提示。
- **检查更新是比 HEAD**：`git rev-parse HEAD` vs `git ls-remote origin HEAD`，只有 `.git` 目录存在
  （clone 来的）才查得动；离线包装的一律跳过，别报「已是最新」骗人。
- **安装不覆盖**：目标目录已存在就加 `-2`、`-3` 后缀另装一份。Git 走
  `git clone --depth 1 --branch <b>`（GitHub 地址按加速开关中转），离线包解压后
  在最多三层里找「直接含 manifest.json」的那一层再搬运。
- **分支列表把远端默认分支排第一**（`ls-remote --symref --heads`）。只按 main/master/字母序排，
  酒馆主仓库（默认 `release`）会把 `copilot/xxx` 顶到第一项，一不留神装错分支。
- **侧栏角标是真的**：`MainWindow.SetExtensionsBadge(pending)`，0 就整颗隐藏。
  以前 XAML 里写死 `Value="3"`，点进去一个都找不到。

### 离线包批量安装

对齐旧版 `pages/extensions.rs` 的 `offline_packages: Vec<…>` 语义：**先整批预检，再逐个安装**。

- `TavernExtensions.InspectOfflinePackages` 只读压缩包目录，不写盘：找最浅的 `manifest.json`
  （最多三层），装出来的目录名＝清单所在目录，清单在根上时用压缩包文件名。
  「同一批里两个包同名」**整组标为无效**——按「重名加后缀」的规则会装成 x 与 x-2，
  用户分不清哪个才是要的，必须让他自己删掉重复的。
- 有任何一个包无效就**整批不动**，弹窗里逐个标红并给出原因，右侧 ✕ 可以移除单个包后重试。
  半批装完再回滚比直接拦住更难收拾。
- `InstallFromZipBatchAsync` 逐个安装、单个失败不影响其余包（与旧版「遇错整批中止」不同：
  批量安装最怕「第三个坏了，前两个白装」）。进度按整批换算百分比，日志里带
  `▶ 文件名 → 目录`，失败逐条列在末尾。
- 文件名选择用 `PickMultipleFilesAsync`，路径去重后整批重检；每次重选＝替换整批。

### 一键启用 / 停用

- 页面右上「全部停用 / 全部启用」按当前状态决定方向（有开着的就整体停用），
  只作用于第三方扩展——自带扩展不受启动器控制。
- 走 `TavernExtensions.SetEnabledMany`：**一次算好目标集合、一次落盘**。
  逐个调 `SetEnabled` 会把整份 settings.json 重写 N 次，中途失败还会留下半套状态。
- 行里的 `Enabled` 是加载时抄下来的副本，批量改完必须同步回 `ExtensionRow`，
  否则开关与「已启用 x/y」会停在旧状态（这个坑真踩过）。
- 按钮在有数据前保持 Collapsed：XAML 里的默认文字会是一个错误的方向（写着「全部停用」
  而实际全都停用）。

## 资源管理（真实环境）

数据层是 `Services/TavernLibrary.cs`，只读磁盘；导入 / 删除由页面调用 `ImportAsync` 与 `ShellFile`。

| 分类 | 目录 | 解析方式 |
|---|---|---|
| 角色卡 | `<userRoot>/characters/*.png`、`*.json` | PNG 块链里的 `tEXt`/`zTXt` 找 `ccv3`（v3）或 `chara`（v2 兼容），base64 → JSON；`IHDR` 取像素尺寸。JSON 卡直接按 `chara_card_v2/v3` 读 |
| 世界书 | `<userRoot>/worlds/*.json` | `entries` 可能是对象字典也可能是数组，两种都认；`disable` = 停用 |
| 对话 | `<userRoot>/chats/<角色>/*.jsonl` | 逐行一个 JSON，`is_user` / `mes` / `send_date`；只留最后 8 条做预览，条数全量数 |
| 预设 | `<userRoot>/* Settings/*.json` | `prompt_order` 决定每条提示词的启停；OpenAI 之外的来源字段名不同（见下） |

- **`userRoot` 要用 `TavernRuntime.ResolveUserRoot()`**，不是 `ResolveDataRoot()`：
  后者在实例模式下返回的是代码目录，而酒馆没传 `--dataRoot` 时用的是 `<代码目录>/data`。
  另外全局数据目录可能刚切过去、只有空壳，方法里带「没有 `default-user` 就回退另一处」的兜底，
  否则资源页会突然一片空白，看起来像数据被删了。仪表盘的指标也改用同一个方法。
- **预设不是只有 OpenAI**：`TextGen Settings` 用 `max_context_length` / `genamt`，
  `NovelAI` 用别的字段，`chat_completion_source` 也只有 OpenAI 预设里有。
  所以模型 / 上下文 / 上限要按候选字段依次取，接口名按来源目录映射；列表按来源目录**分组**显示。
- **缩略图按解码宽度取**（`BitmapImage.DecodePixelWidth`，列表 96 / 详情 264）。
  直接绑原图，二十张 3 MB 的卡会把页面拖住好几秒。
- **导入不覆盖**：同名文件加 `(2)` 后缀；预设统一进 `OpenAI Settings`，别让用户猜该放哪一家。
- **删除走回收站**，与扩展一致。

### 导入前的结构检查

参考旧版 `core/library` 的 `validate_character` / `validate_world_book` / `validate_preset`，
但**刻意放松**——那套逐字段校验类型（每个数字、每个布尔），拦得住「类型写错的卡」，
也拦得住一堆酒馆自己都能读的老文件。这里只判断「这是不是这一类资源」（`Services/ResourceValidator.cs`）：

- **角色卡**：PNG 必须真有 `chara`/`ccv3` 文本块（普通 PNG 会被拦下），JSON 必须能解析；
  `spec`/`spec_version` 只认 v1/v2/v3，别的版本明确说认不出；`name` 不能为空。
  版本判定：`spec` 含 v3 或 `spec_version` 以 3 开头 ⇒ v3；v2 同理；没有 spec 时
  **`ccv3` 块 ⇒ v3**（不少 v3 卡的块里是平铺 JSON，没有 spec 外壳），有 `data` ⇒ v2，否则 v1。
- **世界书**：有 `entries`（对象或数组）且非空，至少一条 `content` 非空。
- **预设**：有 `prompts` 数组，或含常见生成参数（`temperature` / `openai_max_context` /
  `chat_completion_source` / `genamt` …）——老预设本来就没有 `prompts`。
- 不查字段类型、不查 extensions、不查字节大小（只设 64 MB 上限拦巨型文件）。
- **单个文件不合格只跳过它**，同批其它文件照常导入；结果用 `ImportReport` 返回，
  有失败才弹「导入结果」窗口逐条列原因（全成功走全局消息，不打扰）。
  复制仍然「同名加后缀」，不会覆盖用户已有的资源。

## 工作台（真实环境）

从「资源管理」右侧点「打开工作台」进入，编辑的是磁盘上那份文件本身。数据层是
`Services/WorkbenchDocument.cs`，表单与写回在 `Views/WorkbenchPage.xaml.cs`。

- **类型在打开时锁死**：`WorkbenchPage.PendingKind` + `PendingPath` 由资源页写入，页面里
  没有切换入口（原来的三段 RadioButton 已删）。角色卡 → 角色卡工作台，预设 → 预设工作台，
  世界书 → 世界书工作台；对话记录没有工作台（按钮本身不显示）。
  角色卡里的「打开世界书工作台」链接也一并删了——那同样是一次跨类型跳转。
- **PNG 角色卡改写的是文本块，不是整份文件**：保留 IHDR/IDAT 等全部块与像素数据，
  只替换 `chara` / `ccv3` 两个 `tEXt` 块的 base64 文本并重算 CRC。两种块各自的形状
  （v1 平铺 / v2-v3 带 `spec`+`data`）分别沿用，不能把一个的形状套给另一个。
- **JSON 定点改键**：世界书与预设的字段远多于界面露出来的那几个（`insertion_order`、
  `use_regex`、`prompt_order`…）。整体重建会把这些抹掉，所以只覆盖表单里有的键。
- **序列化必须走 `Utf8JsonWriter`**：本项目关了反射式序列化，
  `JsonNode.ToJsonString(options)` 会抛「must specify a TypeInfoResolver setting」。
- **TextBox 要 `UpdateSourceTrigger=PropertyChanged`**：默认的 LostFocus 会让「输入即保存」
  推迟到鼠标点到别处，防抖 700ms 等于白设。
- **没有改动就不写盘**（`_dirty`）。打开看一眼再关掉，文件的字节与修改时间都该原样。
- **预设的 `prompt_order` 是顺序的唯一真相**：保存时按原 order 更新 enabled，
  删掉的提示词从 order 里去掉，新加的接在末尾；**原本不在 order 里的提示词不能顺手加进去**
  （酒馆根本不会注入它，加进去等于替用户改了预设）。
- **标记类提示词**（`marker: true`：chatHistory / worldInfoBefore / dialogueExamples…）在文件里
  没有 `content`/`role`，保存时不要补这两个字段，补了就是脏数据。
- **世界书的 `entries` 形状要沿用**：可能是 `{uid: {...}}` 字典也可能是数组，保存时按原形状写回，
  字典的键不能重新生成。另外世界书用 `disable`（true = 停用），角色卡内嵌条目用 `enabled`，
  两者语义相反，写错会让所有条目开关翻转。
- **换卡面是「换图不换卡」**：取新 PNG 的块表、丢掉它自带的 `chara`/`ccv3`、
  接上这张卡自己的文本块，再整体写回。直接覆盖原图会让角色设定全部丢失。
  JSON 卡没有卡面，按钮是禁用的。
- **离开页面要补一次保存**：防抖计时器不会在页面卸载后继续跑，`OnNavigatedFrom` 与
  「完成 / 返回」都会调 `CommitSave()`。

### 打开速度（实测，改之前先看这张表）

工作台的打开耗时几乎全在**首帧布局**上，不是读文件：`WorkbenchDocument.Open` 只要 10 ms 上下，
剩下的都是 XAML 在量控件。长文本输入框尤其贵（一张卡 40–90 ms），列表项一多就成倍放大。

| 场景 | 优化前 | 现在 |
|---|---|---|
| 预设 171 条提示词 | 420 ms | **44 ms** |
| 世界书 6 条 | 535 ms | **83 ms** |
| 世界书 120 条 | 1104 ms | **94 ms** |
| 角色卡（简介 3009 字） | 183 ms | 183 ms（本就要显示四个编辑器） |

做法（改列表时别退回去）：

- **列表项里不要放长文本 TextBox**。预设卡片改成「名称 + 角色 + 正文预览（4 行截断）+
  笔形按钮」，正文在弹窗里改；世界书条目改成可折叠——收起时只有序号、标题、正文预览、
  关键词数、开关，点开才铺编辑区（一张展开卡约 90 ms，所以默认全收起）。
- **列表分页**：预设一页 6 条、世界书一页 6 条。120 条的书写如果全铺，光是收起状态也要 1.1 秒。
- **页码条不要「每页一个按钮」**：171 条 = 29 个按钮 = 380 ms。页数 > 6 时收成
  「x / y」一个按钮，点开才用 MenuFlyout 生成页面列表。
- **首帧只画框架**：预设的卡片列表改到 `Loaded` 之后才铺，先显示「正在准备提示词…」。
- 计时办法：临时在构造函数与 `Loaded` 里打 `Stopwatch` 段（init / load / paint / toLoaded），
  定位到具体是哪一段慢，别凭感觉改。

### 长文本编辑区（角色卡简介 / 性格 / 场景 / 开场白）

- **卡片高度写死 252**，不是 `MinHeight`。工作台整页在 ScrollViewer 里，高度约束是无限的，
  一旦只给 `MinHeight`，3000 字的简介会把输入框一路撑高、把整页拉成一条竖线——
  现在内容只在框内滚动，四块对齐、页面高度稳定。
- 真要仔细改就点表头的放大按钮（`ExpandText_Click`）：弹窗里给 420 高的大框 + 实时字数，
  关掉才写回模型（边打字边写回会让防抖保存每 700ms 触发一次）。
- **`TextBox.Text` 必须在挂进可视树之后（Loaded）再赋值**：构造时直接 `new TextBox { Text = 多行 }`，
  文末第一个换行之后的内容会被丢掉（实测 3009 字的简介只剩 369 字）。
  这条对所有「代码里临时造的多行 TextBox」都适用。
- 卡里的正文常是 CRLF：喂进框之前统一成 LF，保存时按原风格还原，避免「只是打开看一眼」
  就把整段换行改掉。

## 代理（系统代理状态）

读取系统代理只有一处：`Services/SystemProxy.cs`。设置页的「系统代理状态」行、
`TavernRuntime` 起酒馆时注入的 `--requestProxyUrl`，读的都是它。

- **「跟随系统」必须把 Windows 的真实状态摆出来**：这四个字本身不传递信息——
  系统那边很可能压根没开代理，用户却以为流量已经走了代理。所以选中「系统代理」时
  多出一行：绿色「已开启」+ 实际地址，或灰色「未开启」+ 「选这一项等于直连」。
- **三种开启形态要分开**：手动代理（`ProxyEnable=1` + `ProxyServer`）、PAC 脚本
  （`AutoConfigURL`，即使 `ProxyEnable=0` 也生效）、自动检测（`AutoDetect=1`，WPAD）。
  只有三者都不成立才是未开启；PAC 与 WPAD 拿不到固定地址，要说清楚而不是留空白。
- **`ProxyServer` 的四种写法都要归一**：`host:port`、`http://host:port`、
  `http=…;https=…`、`socks=…`。给 node / git 用时只认 http(s)，所以优先挑 http 段；
  socks 段要补 `socks5://`，否则会被当成 http 代理连。
- 这一行只在「系统代理」被选中、且不是小白版时出现（小白版整组收起）；
  旁边给「刷新」（重新读注册表）与「去设置」（`ms-settings:network-proxy`）。
- **每次 `RefreshDerived` 都重读**：用户在 Windows 设置里改了代理，回到启动器就该看到新状态。

## GitHub 连通性测试

数据层是 `Services/GithubConnectivity.cs`，弹窗在 `Views/SettingsPage.xaml.cs`
（`ShowGithubTestAsync` / `ProbeGithubAsync`）。

- **每个数字都是这次请求实测的**。以前是四条写死的延迟（412/268/0/168），
  换了代理、断了网，点「重新测试」照样一片绿——那种「测试」比没有更糟。
- 探测的节点：`github.com` 仓库页、`api.github.com` releases、`codeload.github.com`
  的 tar.gz、`raw.githubusercontent.com` 的 start.sh；**加速开着才多探一条**
  `gh-proxy.org`（真实加速地址），**装了 Git 才多探一条** `git ls-remote`。
- 延迟 = 从发请求到拿到响应头。只读到响应头就断开（`ResponseHeadersRead` + `Range: 0-2048`），
  不能为了测连通性把几十 MB 的压缩包拖下来。
- 代理走 `ProxyHttpClient`，与版本列表、安装器同一套规则；弹窗顶部把「当前出口」写出来，
  免得用户猜到底有没有用代理。
- **中转 4xx 要说成「这条加速地址不可用」**，不能等同于「GitHub 不通」：两者处置完全不同。
- 网络异常要翻成人话（`DescribeHttpError`）：`The SSL connection could not be established…`
  对用户毫无意义，界面写「TLS 握手失败（域名可能被阻断）」。
- 「最快」只统计 GitHub 自己的域名：中转与 git 通道不是同一维度的数字。
  结果写回 `LauncherSettings.SetGithubTestResult`，设置行显示「最佳 xxx ms」。
- 测试并发跑（`Task.WhenAll`），单个超时 8 秒、git 20 秒；串行六条能让人等到关弹窗。

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
├── .sillytavern-staging/       ← 换版暂存目录，安装过程中才存在
├── .sillytavern-old/          ← 被替换下来的旧版本，删除失败时才会残留
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
├── bin/                    ← 内置环境目录 (内置 NodeJS、MinGit 等)
│   ├── nodejs/             ← 内置 NodeJS 目录
│   ├── git/                ← 内置 Git/MinGit 目录
│   ├── pm2/                ← 内置 PM2 目录
│   └── caddy/              ← 内置 Caddy 目录
└── settings.toml              ← 启动器配置文件

%Temp%/astrabrew-launcher/          ← 根目录·临时目录 (temp)
├── caches/                         ← 缓存目录，存放API数据缓存等 (caches)
│   └── sillytavern-releases.json   ← 内置实例的版本列表缓存
└── <组件安装包>.zip                 ← 下载中的安装包，落位/失败后即删