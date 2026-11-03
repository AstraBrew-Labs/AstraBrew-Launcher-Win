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
- **缓存优先**：`%Temp%\astrabrew-launcher\caches\sillytavern-releases.json`，内存再缓存一层。
  缓存是中间产物（删了只是多请求一次），所以放临时目录而不是 AppData；路径统一取
  `AppPaths.CacheRoot`，不要在页面里自己拼。
  有缓存就不请求网络（未登录的 GitHub API 每小时只有 60 次）；
  只有用户点「刷新版本列表」才强制请求并覆盖缓存。
- 强制刷新失败时继续显示旧缓存，并把原因折成一句话给 InfoBar；**不要**清空列表或弹堆栈。
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