## 窗口规范
软件窗口界面，不能最大化
要适配高清屏，高分辨率屏，保持界面清晰度
要保证每个缩放都能正常显示
要适配多屏切换
要适配如果软件界面在副屏，但副屏断开，要自动切换到主屏显示

> 实现集中在 `Services/WindowPlacement.cs`（由 `MainWindow.SetupWindow` 创建）。
> 新增窗口相关行为请改这里，不要在页面或 MainWindow 里另写一套 DPI / 尺寸计算。

## 构建渠道（Alpha / Beta / Debug / Release）

四条渠道各占一个构建配置，渠道标记**编进程序集**，关于页与设置页据此显示：

| 配置 | 定位 | 关于页标记 | 产物 |
|---|---|---|---|
| `Alpha` | 预览版 | 预览版（琥珀） | `bin/x64/Alpha/…/win-x64` |
| `Beta` | 测试版 | 测试版（紫） | `bin/x64/Beta/…/win-x64` |
| `Debug` | 开发版 | 开发版（红） | `bin/x64/Debug/…/win-x64` |
| `Release` | 正式版 | **不显示标记**（胶囊写「稳定通道」） | `bin/x64/Release/…/win-x64` |

```bash
dotnet build "AstraBrew Launcher.csproj" -c Alpha -p:Platform=x64
dotnet build "AstraBrew Launcher.csproj" -c Beta  -p:Platform=x64
dotnet build "AstraBrew Launcher.csproj" -c Debug -p:Platform=x64
dotnet build "AstraBrew Launcher.csproj" -c Release -p:Platform=x64
```

- 标记来源是 `Services/BuildChannelInfo.cs`：读 csproj 里
  `<AssemblyMetadata Include="BuildChannel" Value="Alpha|Beta|Debug" />` 生成的程序集元数据，
  **没写就是正式版**。不要改用 `#if` / `DefineConstants` —— SDK 的 targets 会后置追加常量，
  项目体里拼进去的值容易被 `DEBUG;TRACE` 顶掉。
- 关于页：`HeroBadges` 里的胶囊（正式版中性灰「稳定通道」，其余写「预览版 / 测试版 / 开发版」
  并换成该渠道的强调色）+「版本与构建 → 更新通道」（预览通道 / 测试通道 / 开发通道 / 稳定通道）。
  设置页版本行同一套判断（`ApplyBuildChannel`），不要各写一份。
- **标题栏左上角也有渠道标记**（`MainWindow.ApplyChannelBadge`）：紧跟「AstraBrew Launcher」之后，
  底色是强调色 0x38 alpha、文字用强调色，10.5 号 SemiBold。**正式版整个 Border 收成
  `Visibility.Collapsed`**（不是空文字）——折叠的子元素不占位，标题栏不会多出空隙。
  这一处是给「截图反馈」用的：用户贴一张图就能看出是哪个渠道的构建，不必再翻关于页。
- 复制版本信息只在非正式版时多写一行「渠道」，正式版拿到反馈时没有这行就代表稳定版。
- `Debug / Alpha / Beta` 都把 Windows App SDK 做**自包含**（`WindowsAppSDKSelfContained`），
  本机没有完整运行时也能直接双击跑；`Release` 保持框架依赖，对外发布走 MSIX / 安装包。
  因此在这台机器上 **Release 的 exe 不能直接双击**（会抛 `REGDB_E_CLASSNOTREG`）——
  要验证正式版界面，用
  `dotnet build -c Release -p:WindowsAppSDKSelfContained=true -p:BaseOutputPath=bin/verify/ -t:Rebuild`
  另建一份自包含产物，验完删掉；不要为了跑一下就把 Release 改成自包含。
- `.sln` 里同步加了 `Alpha|Beta × x86/x64/ARM64`，VS 的配置下拉能直接选。
- **版本号只有一个源头**：csproj 的 `<Version>0.3.0</Version>`，AssemblyVersion / FileVersion 全由它派生。
  关于页、设置页都读 `Assembly.GetName().Version`，XAML 里的 `v0.3.0` 只是设计时占位，
  改版本时**只改 csproj 一处**，不要在各页面里再写死。
- 标题栏胶囊的尺寸与实例徽章（`InstanceChip`）**完全一致**：`Padding="8,2,8,3"` + 11.5 号 SemiBold，
  两枚摆一起高度与基线才齐平（最初用小一号的 10.5 + 1,2 内边距，比旁边矮 4 px，看着一高一低）。
  关于页主卡那三枚（版本 / 渠道 / 架构）同理，纵向内边距统一取 `1,2`
  ——版本胶囊原来是 `2,3`，比旁边两枚高 2 px。

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

### 启动模式只有一个真值

- `LauncherSettings.StartMode`（普通 / 桌面 / 服务器）是**唯一**的模式来源。
  曾经还有一个独立的 `server_mode` 开关，于是出现「启动方式选了服务器模式、服务器选项却没展开」这种
  自相矛盾的状态；现在 `ServerMode` 是 `StartMode == Server` 的推导属性，设置页那个开关已删除，
  服务器子行（服务范围、后台常驻、互联网下的反向代理）跟着启动方式展开，
  **小白版也给**：选了服务器模式却拿不到「服务范围」，等于把人锁在局域网里没法改；
  判定里不要写 `!simple`（`.cs` 里 `ApplyConditions` 的 `ServerRows` / `InternetRows`）。
- 旧配置里 `mode = browser` + `server_mode = true` 的，读取时升到服务器模式（只升不降：
  桌面模式有自己的含义）。写回时 `server_mode` 仍写一份，但只是「给人看的推导值」。
- **服务范围（局域网 / 互联网）写入 `config.yaml` 的 `listen` / `listenAddress.ipv4`**，
  统一走 `TavernSettings.SetScope(...)`：仪表盘的 chip 与设置里的「服务范围」以前各写各的
  （设置页只写 settings.toml，对酒馆毫无影响），现在两边同一入口、读的也是同一份
  （`TavernSettings.Scope`，启动时从 config.yaml 读）。
- **写状态要在写文件之前**：`TavernSettings.SetScope` 先更新 `_scope`，再 `PatchScalar`。
  反过来（先写文件）时，`PatchScalar` 触发的 `LauncherSettings.Changed` 会让设置页按**旧的**
  `TavernSettings.Scope` 回填分段选择器，把用户刚点的选项改回去；RadioButton 的 `Checked`
  还可能晚一拍派发，于是又被当成一次用户点击——表现就是「服务范围来回点，偶尔才能切换成功」。
- **`SegmentedPicker` 先点亮目标、再关掉其余**：反过来会先让另一个分段短暂进入选中态。
  每个分段都写了 `AutomationProperties.Name`（Content 是面板时 RadioButton 自己算不出名字）。
  自动化测试按名字点分段，别按坐标点空白处。
- **网卡地址带缓存**（`NetworkInfo.LocalAddresses`）：`NetworkInterface` 枚举在 Windows 上
  一次 100 ms 出头。仪表盘切换服务范围时原来一次点击要枚举 **4 次**（`LocalAddresses` 一次、
  `HttpUrl(port)` 内部再一次，而 `SyncServiceScope` 被 handler 与 `Changed` 各调一次），
  于是点击后要卡半秒才切过去；设置页不查网卡，所以那边一直是顺的。
  现在结果缓存 30 s、过期先返回旧值再后台重新枚举，`HttpUrl(port, v4)` 复用已有的 v4。
  任何「每次刷新都要查硬件/网络」的信息都按这个模式做，别在 UI 线程上直接枚举。
- 「反向代理」行属于 `InternetRows`：**只有服务器模式 + 互联网**才显示，
  判断读 `TavernSettings.Scope`（唯一状态），别再读 settings.toml 里的镜像值。
- **两个页面要双向同步**：仪表盘改模式/范围 → `TavernSettings.Changed` → 设置页
  `RefreshDerived` → `SyncExternalChoices()` 把分段选择器与开关拉回当前值（滑杆/输入框不碰，
  免得打断正在拖动或输入的操作）；反向由仪表盘的 `SyncFromShared` 负责。

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
- **中转端 403 值得再试一次**：gh-proxy 前面挂着 CDN（响应带 60 秒缓存），
  偶发 403 是它自己的频率限制，等 1.2 秒重试常常就命中缓存了。直连不重试——
  GitHub 的 403 是「这一小时 60 次用完了」，再试只是多花一次额度。
- **缓存是「不要频繁请求」，不是「永远不请求」**：`CacheTtl` 15 分钟内直接用，
  过期在页面打开时自动校验一次；失败后 `FailureCooldown` 5 分钟内不再自动重试
  （手动刷新不受限制）。但冷却**只在手里有缓存、且出口配置没变时**才生效：
  没有缓存还压着不请求，用户看到的就是一个既没数据又不会去要的死页面；
  刚打开加速却继续吃上一次直连失败的冷却，表现就是「开了加速还是那个 403」。
  出口配置 = 加速开关 + 代理方式，记在 `_lastFailureExit`，一变冷却即作废。
- **条件请求不花额度**：缓存里存 GitHub 的 `ETag`，下次带 `If-None-Match`；
  304 按 GitHub 规定不计入限额，所以「经常校验」和「省额度」可以同时成立，
  304 时把 `FetchedAt` 刷新为当前时间，界面显示「已更新于」而不是「缓存于」。
  `ETag` 记来源出处（`_etagSource`），只发回当初那条出口：中转端与直连给的 ETag
  是两套，混着用只会换回无意义的 200 全量响应。
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

## 浏览器选项只在普通模式出现

酒馆配置页的「自动打开浏览器」「默认浏览器」两行（小白版与高手版各一份）只在**普通模式**下显示：
桌面模式由启动器自己开窗口、服务器模式根本不弹页面，这两项对它们没有作用。

- 判断走 `TavernSettings.IsNormalMode`（= `LauncherSettings.StartMode == Normal`），
  在 `SyncBrowserRows()` 里统一处理，`Loaded` 与 `TavernSettings.Changed` 都会调它。
- **连解释条一起去掉**：以前还留着一条「当前不是普通模式，浏览器相关选项已隐藏」的 InfoBar，
  摆在那反而像还留着选项。现在整行消失，什么都不说。
- 副标题与项数要跟着变，否则「端口、监听地址、协议栈与默认浏览器」「5 项」会和界面自相矛盾：
  高手版分组副标题切到「端口、监听地址与协议栈」，小白版项数 5 → 3。

## IP 白名单（两份，服务器模式推导；普通模式记住用户那份）

`TavernSettings` 是唯一入口，内部其实有**两份名单**，谁都不覆盖谁：

| 名单 | 存在哪 | 谁在用 |
|---|---|---|
| 用户名单 `_userHosts` | `settings.toml` 的 `tavern.user_whitelist`（逗号分隔） | 普通 / 桌面模式，可加删，回环不可删 |
| 推导名单 `BuildServerWhitelist()` | 只写 `config.yaml` | 服务器模式，全部锁定 |

| 启动模式 | 名单 | 能否编辑 |
|---|---|---|
| 服务器 · 局域网 | `127.0.0.1`、`::1`、本机 IP、本机网段（如 `192.168.8.0/24`） | 锁死 |
| 服务器 · 互联网 | `127.0.0.1`、`::1`、`0.0.0.0/0`、`::/0`（全开放） | 锁死 |
| 桌面 | 只有 `127.0.0.1`、`::1`（页面由启动器 WebView2 从本机打开） | 锁死 |
| 普通 | 用户名单（首次从 config.yaml 迁移，没有则本机 IP + 网段做底） | 可加删 |

`WhitelistLocked` 是「服务器 **或** 桌面」，`BuildDerivedWhitelist()` 里桌面模式直接返回回环两条——
它没有「服务范围」可讲，界面文案也不该再说「按服务范围生成」。

- **切到服务器模式不许写用户名单**，切回来才能原样恢复。以前只有 config.yaml 一份，
  服务器模式一写就把用户条目冲掉，切回来要么丢了、要么残留 `0.0.0.0/0`——这是最容易复发的坑。
- 模式一变（`LauncherSettings.Changed` + `_whitelistMode` 比较）就 `ApplyWhitelist()`：
  只切换视图，两份数据都不动。只有用户自己增删才走 `SyncUserHosts()`（同时写 settings.toml 与 config.yaml）。
- **去重与规范化**（`NormalizeHost` / `NormalizeHosts` / `Covers`）：
  CIDR 主机位归零（`192.168.8.21/24` → `192.168.8.0/24`）、`/32`、`/128` 折成单个地址、
  完全相同的合并、**被更宽网段覆盖的条目删掉**（有 `192.168.8.0/24` 时 `192.168.8.21` 是废话）。
  添加时两个方向都判：新条目被已有网段覆盖 → 拒绝并说明；新条目覆盖了已有条目 → 清掉那些。
  启动与每次重建都会做一遍，规范化结果写回 settings.toml。
- 只收 IP / CIDR：**`localhost` 会被 SillyTavern 判成 Invalid IP 丢掉**，等价物是 `::1`；
  `0.0.0.0` 单条不匹配任何地址，全开放要写 `0.0.0.0/0`，IPv6 还要 `::/0`。
- 界面：`SyncWhitelistUi()` 按 `WhitelistLocked` 显隐输入框与说明；条目自带 `IsLocked`，
  锁定项不出删除按钮。
- **标签（Tags）而不是列表**：IP / 网段都很短，一行横着能放五六个，竖列表纯属浪费横向空间。
  两个 `ItemsControl`（小白版 / 高手版）都用 `ItemsPanelTemplate` + `panels:WrapPanel`
  （`Views/Controls/EditorPanels.cs` 里的最小实现，WinUI 3 没有 FlexLayout），
  条目模板是页面资源里的 `WhitelistTagTemplate`：锁定项 = 中性底 + 小锁（`LockedTagChipStyle`），
  自定义项 = 强调色淡底 + 小叉（`EditTagChipStyle` + `ChipCloseButtonStyle`）。
- **`WrapPanel` 的 Arrange 必须沿用 Measure 的宽度**：外层 ScrollViewer 可能把 `finalSize.Width`
  传成「内容想要的宽度」（比视口宽），拿它换行等于不换行——标签会一路排到卡片外被裁掉。
  面板里记下 `_limit`，Arrange 取 `Math.Min(finalSize.Width, _limit)`。
  同时 ScrollViewer 要写 `HorizontalScrollMode="Disabled"`（只设 `HorizontalScrollBarVisibility`
  仍会按无限宽测量）。改标签布局时先测「刚好比一行多一条」的数量，最容易暴露这个坑。
- **外层仍限高**（`ScrollViewer MaxHeight="150"`，约 5 行标签）：极端情况（几十条）也不拉长页面。
  别把 `MaxHeight` 改成 `MinHeight`：外层页面本来就是 ScrollViewer，高度约束是无限的。
  添加成功后 `ChangeView` 滚到底（新条目接在末尾），用 `DispatcherQueue.TryEnqueue` 等布局完成再滚。
- **回车即添加**：两个输入框都挂 `KeyDown="WhitelistInput_KeyDown"`，`VirtualKey.Enter` 时
  按屏幕上的「添加」同一条路径走，并 `e.Handled = true`；成功后清空文本、焦点留在框里，
  好连着填下一个。提示文字写「输入 IP 或网段，回车即可添加」，别只写示例。
- 起服务前 `TavernRuntime` 读 `WhitelistHosts` 快照写盘并打一行
  `白名单（普通模式）：127.0.0.1、::1、…`——排查「连不上」先看它。
- 集合绑在 ItemsControl 上，**改集合必须在 UI 线程**：`ReplaceWhitelist` 会自己转投。

## 全局数据目录（只显示、不可改）

设置页「全局数据目录」显示的是 `GlobalTavernConfig.GlobalDataRoot` —— 由配置文件位置推导的
`…\AstraBrew Launcher\data\sillytavern`，也就是酒馆真正读写的 `--dataRoot`。

以前这里读的是 `LauncherSettings.GlobalDataPath`（默认 `…\data`，还能「更改」），
但**没人用它**：真实路径只由 `GlobalTavernConfig.GlobalPath` 决定。于是界面显示 `…\data`、
实际用的是 `…\data\sillytavern`，改也没反应。现在这个死设置连同 `settings.toml` 的
`paths.data` 一起删掉，行里只留「打开目录」，说明文字写明「位置固定，随配置文件走」。

## 全局酒馆配置（首次获取）

**生效与操作的只有全局那一份**：`data/sillytavern/config.yaml`。读取、修改、覆盖、删除都针对它，
由 `Services/GlobalTavernConfig.cs` 的 `ActivePath` 统一给出，其它地方不要自己拼路径，
更不要因为全局缺失就退到默认路径去用。

默认位置 `data/default/sillytavern/config.yaml` 只是**恢复默认用的底稿**，不是生效配置：
全局缺失时先 `RestoreFromDefault()` 把它复制成全局（静默、不打扰用户），
底稿也没有时才联网取一份——取回先落默认位置、做几项开箱优化，再复制成全局配置。
此后用户改的都是全局那份，默认位置始终保持一份干净的底稿。

全局不存在且底稿也恢复不出来时，酒馆配置页盖一层遮罩（`ConfigMask`），只能点「获取默认配置」：
`FetchAsync` 按 `BuildTargets()` 的顺序下载，**镜像 → 加速 → 直连**：
- 镜像 = GitCode 上的同一份文件（`GlobalTavernConfig.MirrorUrl`：
  `https://raw.gitcode.com/GitHub_Trending/si/SillyTavern/raw/release/default/config.yaml`），
  国内直连、不占 GitHub 额度，所以排第一；它**不套加速前缀**（镜像不是 GitHub 域名）；
- 镜像取不到时按 `GithubAccel.Targets` 走：开着加速就先加速、失败回退直连，关着就只有直连。

取回后先 `ApplyLaunchDefaults` 做开箱优化，写默认位置，再复制成全局配置。
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
- **地址卡给的是完整 URL + 复制 / 打开**：框里直接是 `http://127.0.0.1:11451/` 这种
  能粘进地址栏的地址（以前只放 IP，用户还得自己补协议和端口），旁边两个按钮——
  复制（同时把按钮文案改成「已复制」）与打开（`UseShellExecute` 走系统默认浏览器，
  不是设置里选的那个）。本机模式的弹窗只放一张卡，宽度收到 520。
- **每次启动先清日志**（`StartAsync` → `ResetLogs`）：清空后留一行
  `新的启动 · HH:mm:ss`。两次启动的输出接在一起，出问题时根本分不清哪一行是这次的。
  清表要经 `Post` 走到 UI 线程再触发 `Cleared`（页面重画列表）——重启流程在后台线程推进，
  直接清表就是跨线程改控件，会抛异常，表现成「点了重启停在已停止，什么也没发生」。
- **重启要等「停止中」结束**（`WaitSettledAsync`）：停止的收尾是投到 UI 线程的，
  后台线程抢在它前面调 `StartAsync`，会被「正在启停」的守卫直接挡回去。
- **网络胶囊说的是「能被谁访问」，跟着启动模式走**：普通模式 → 「本机」+ 本机访问弹窗
  （只列回环地址）；服务器模式才按服务范围显示「局域网 / 互联网」；桌面模式整颗换成
  「打开酒馆」（页面在 WebView2 里，地址对它没有意义）。拿 `config.yaml` 的 `listen`
  直接当显示依据，会让普通模式的用户看到一个用不上的局域网入口。
- **设备访问弹窗只有一份实现**（`Views/TavernAccessDialog.cs`）：控制台与仪表盘服务器模式共用
  `ShowAsync(XamlRoot, theme)`，访问范围判定也在里面（`CurrentScope()`）。两边各画一套时，
  改了一边另一边就会走偏。
- **弹窗里必须有二维码**（局域网 / 互联网）：手机是这套功能的主要使用者，让用户手抄 IP 不现实。
  QR 用 `QRCoder` 的模块矩阵画成**一块尺寸精确的 `Canvas` + 同行合并的 `Rectangle`**，不生成位图。
  **不要用 `Path` / `GeometryGroup` 画码**：`Path` 自己量 GeometryGroup 时量出来的比实际几何小，
  码面会在白框里偏移并被裁掉一截（表现就是「二维码没居中」，踩过）；`Stretch.Fill` 只会把
  码面拉满、吃掉静区。模块边长取整数 DIP（`Math.Floor(132/dim)`），非整数会糊成灰边。
  白框 = 6 DIP 内边距 + 码面自身 4 模块静区；白底黑码写死，不吃主题色。
  本机模式（127.0.0.1）不画码——手机连不上回环地址，画了只会误导。
- **互联网模式要公网 IP**（`Services/PublicIp.cs`）：`https://api-ipv4.ip.sb/ip` 与
  `https://api-ipv6.ip.sb/ip` 分别请求、各自 5 秒超时、失败互不影响（家用宽带多数只有 v4，
  硬等 v6 只会卡住弹窗）；结果里的内网 / 回环 / 链路本地地址一律丢弃（运营商 NAT 有时回内网地址）。
  成功缓存 5 分钟、全失败只压 30 秒。弹窗**先显示再填充**（`ShowAsync` 拿到 operation 后
  `_ = FillPublicAsync(...)`），别让人对着空窗等接口。只有一个都取不到时，才退回显示内网地址并说明。
- **弹窗布局**：卡片统一 `CardWidth = 500` 且 `HorizontalAlignment=Center`，卡片内左侧二维码方框
  （码面 ≤132 DIP + 6 内边距）、右侧地址 + 复制/打开，几张卡叠起来左右边界才对得齐；
  提示文字同样 500 宽居中。
- **弹窗必须自带「关闭」按钮**：`BuildContentHost` 里画一颗右对齐的 `AccentActionButtonStyle`
  按钮（`dialog.Hide()`）。不要依赖 ContentDialog 自带的按钮区——只有一颗按钮时它会被拉成
  整行宽的横条，跟卡片宽度对不上；更不能像上一版那样把底栏整个丢掉，触摸屏上等于关不掉。
- **继承来的 `NODE_OPTIONS` 要修正**（`NodeEnvironment`）：`--import` / `--loader` 的
  模块说明符只认 `file:` / `data:` / `node:` 协议，传 Windows 路径（`C:\…`）时 Node 把盘符
  当协议名，进程立刻以退出码 1 退出（`ERR_UNSUPPORTED_ESM_URL_SCHEME`，旧版本让用户卡在这）。
  启动前把裸路径改写成 `file://` URL 并记一行日志；真报出这个错时补一句成因说明，
  别让人以为是酒馆代码坏了。

## 桌面模式（内嵌 WebView2）

启动方式选「桌面模式」时，酒馆启动成功后由启动器开一个装着 WebView2 的独立窗口，
不再交给系统浏览器。实现全在 `Services/DesktopWindow.cs`。

- **不要照搬旧版的自建 Win32 消息循环**：Rust 版之所以手写 `CreateWindowExW`，是因为 winit
  的事件循环一个进程只能建一次；WinUI 3 的 `Window` 本来就是自己的窗口，直接
  `new Window { Content = WebView2 }` 即可。
- **用户数据目录必须可写**：默认会落到 exe 旁边，装在 Program Files 下时那是只读目录，
  窗口会一直白着。用 `%LocalAppData%\AstraBrew Launcher\WebView2`。
  WebView2 的 C#/WinRT 投影只有 `CreateAsync()` 与 `CreateWithOptionsAsync(folder, userData, options)`，
  **没有** 2/3 参数版的 `CreateAsync`（写了会 CS1501）。
- **生命周期**：`MarkRunning` 里按模式开门（普通模式不开——酒馆自己会拉浏览器，开两次就是两个页面）；
  `FinishStopped` / 进程异常退出 / 主窗口关闭都会收掉它。
- **用户自己关窗看 `LauncherSettings.StopTavernOnWindowClose`**：开着 → `TavernRuntime.StopAsync()`
  （窗口关了就等于本次用完，不留占端口的后台进程），关着 → 酒馆继续跑，全局消息给「重新打开」，
  走 `DesktopWindow.Open` 重建。这个开关同时管启动器主窗口的关闭——两处都读同一个值，
  不要再出现「只挂在主窗口关闭上、关桌面窗口却不停」的半截实现（踩过）。
- **只有一个窗口**：`Open` 已有窗口时只切地址 + `Activate`；「打开酒馆」按钮和访问弹窗的
  「打开」都走 `OpenTavern`（桌面模式开窗、其余模式仍用浏览器，WebView2 初始化失败时回退浏览器）。
- **窗口被关掉要有地方找回来**：`DesktopWindow.Changed` 在开窗 / 关窗时触发（UI 线程），
  仪表盘的「打开酒馆」说明跟着在「窗口已打开 · 点击唤到前台」与「窗口已关闭 · 点击重新打开」
  之间切换；**服务在跑时**主卡就多一颗 `HeroOpenButton`（不再只限桌面模式）——排在
  「重启服务」「停止」右侧，两者之间一条 `OpenDivider` 竖线（20×1，`DividerStrokeColorDefaultBrush`）。
  **文案统一是「访问酒馆」**，三种模式的差别只用图标区分：普通模式 `\uE774`（地球 / 浏览器）、
  桌面模式 `\uE7F4`（显示器 / 内嵌窗口）、服务器模式 `\uE968`（设备 / 地址弹窗）——
  和「默认启动方式」选择器里那三颗图标是同一套，用户不用重新学。行为不变：
  浏览器 / 开窗口 / 弹地址弹窗。服务停下时按钮与竖线一起收起。
  **竖线的可见性跟按钮同一个布尔值**：按钮收起时线也得收起，否则启停两颗按钮右边会剩一根孤线。
  控制台那颗运行态胶囊在桌面模式下也变成**「打开酒馆」**（普通 / 服务器模式仍是本机 / 局域网 / 互联网）。
  **不要**把重开入口只放在关窗时那条全局消息上——消息一滑走就再没有入口了。
- **「访问酒馆」在三种模式里是三件事**：普通模式交给浏览器（`BrowserLauncher`，按设置的浏览器）、
  桌面模式开 / 唤回内嵌窗口、服务器模式弹设备访问弹窗（地址比在本机再开一页有用）。
  按钮文案三处统一为「访问酒馆」（主卡 `HeroOpenButton`、快捷操作 `Qa1`、控制台运行态胶囊），
  靠图标区分模式；分支只有 `DashboardPage.OpenTavern_Click` 与 `ConsolePage.NetworkButton_Click`。
  改文案时三处一起改，别又出现「打开酒馆 / 访问地址 / 打开酒馆」各说各话。
- **记住酒馆窗口的位置**（`startup.remember_tavern_window`，默认开）：关窗时把 `AppWindow`
  的 `Position`/`Size`（物理像素）写进 `LauncherSettings.TavernWindowRect` → `settings.toml`
  的 `[tavern_window]`，开窗时由 `PlaceWindow` 还原。三条硬规则：
  A. 存的时候要在窗口销毁之前（`Close()` 里先 `SaveBounds` 再清引用；用户点叉走的
  `OnWindowClosed` 也存一次），最小化的 `-32000` 哨兵坐标要跳过；
  B. 还原时必须过 `ClampToWorkArea`——用户可能拔了副屏或改了分辨率，坐标一律夹到最近那块屏的
  工作区里，尺寸按工作区收窄，保证标题栏可达；
  C. 开关关掉时既不存也不读（关掉后开窗回到居中 1600×900），但**不清已存的坐标**——
  用户临时关一下再打开，上次的位置还在。
  这一项只在「默认启动方式 = 桌面模式」时显示（`DesktopRows`），普通模式没有宿主窗口。
- **窗口图标必须给 .ico**：`AppWindow.SetIcon` 收下 `Sillytavern.png` 的路径时**不抛异常**，
  但窗口拿到的是一张空白图标（标题栏变成系统默认的页面图标）——这点很坑，实测过。
  现在的做法：`Assets\Sillytavern.ico` 由同目录的 `Sillytavern.png` 生成（16/24/32/48/64/128/256 七档），
  `ApplyIcon` 按 `Sillytavern.ico` → `app.ico` 的顺序取第一个存在的。换掉 PNG 后要重新生成 ico：
  在 `Assets` 目录下跑 `python -c "from PIL import Image; ..."`（sizes 给 16~256 七档），
  或直接把生成的 ico 覆盖进去。
- **WebView 内存上限**（`desktop.webview_memory_mb`，默认 2048）：Chromium 没有「整体内存上限」
  这种开关，实际能约束的是 V8 堆，所以走 `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`
  传 `--js-flags=--max-old-space-size=<MB>`（渲染进程命令行里能直接验证到）。取值夹在
  `LauncherSettings.MinWebViewMemoryMb`（1024）与 `MaxWebViewMemoryMb`（`SystemMemory.MaxLimitMb`：
  物理内存减去「20% 或 2 GB 取大者」，向下取整到整 GB）。**环境是每次开窗新建的**，
  所以改完必须重开窗口才生效——设置页那句「改完下次打开窗口生效」不能删。
- **下载与导出**：`DownloadStarting` 统一改到设置的导出目录、同名加 `_1` 后缀；
  页面里 `a[download]` 指向 blob / 大 data URL 的情况，由注入脚本经 IPC 频道 `fileDownloader`
  把 base64 交给原生层落盘（超过 64 MB 直接提示，不走 base64）。
- **文件选择兜底**：注入脚本在 `change` 后按 accept 检查扩展名，选错类型时给出中文说明，
  避免「选了文件但页面毫无反应」。
- **窗口**：1600×900 居中（小屏按工作区收窄）、图标用 `Assets\Sillytavern.ico`（酒馆自己的 ST 标志，
  取不到才退回 `app.ico`）、标题跟随页面 `DocumentTitle`；导航失败自动重试一次，
  仍失败给可点击的「重新加载」；浏览器进程退出也提示重开，而不是停在白屏。

## 快捷操作区

宽屏是 2 行 3 列、窄屏是 3 行 2 列，**两种状态都要给 `RowSpacing`**（10）。
宽屏的 VisualState 里曾把 `QuickGrid.RowSpacing` 设成 0，视觉上两排按钮直接贴在一起；
行距为 0 只适合只有一行的情况（比如同组的 `MetricsGrid`）。

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

**快捷操作是真的会做事**：「打开酒馆」按启动模式分流——普通模式走 `BrowserLauncher.Open`
（按 App Paths 找 chrome/edge/firefox，找不到退回系统默认浏览器）、桌面模式开或唤回内嵌窗口
（说明文字读 `DesktopWindow.IsOpen`）、服务器模式弹设备访问弹窗；数据目录开资源管理器；
导入角色卡把选中的 PNG/JSON 复制进 `characters`（同名加 `(2)` 后缀），复制完刷新指标。
地址一律取自 `NetworkInfo.LocalAddresses()` + 实际端口，别再写示例 IP。

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
- **四项**：官网 `https://github.com`、API `…/repos/SillyTavern/SillyTavern/releases`、
  Raw `…/refs/heads/release/default/config.yaml`、**克隆仓库**（真把仓库完整拉一份到临时目录）。
- **出口按「代理 × 加速」四种组合决定**（`ResolveProxyPlan`）：
  都没开 → 不走代理、不拼前缀；只开代理 → 走代理；只开加速 → **不走代理**、API/Raw/克隆
  拼 `GithubAccel.Prefix`；两个都开 → 既走代理也拼前缀。
  代理是**显式**配到 `HttpClientHandler` 上的（`UseProxy = false` 就是真不走），
  不能靠系统默认——「只开加速」时测出来的绿必须是中转给的，否则用户关掉代理就装不上。
- **官网不拼前缀**：中转对 HTML 页面只会 301 回 github.com（实测），
  拼上去等于多绕一跳还看不出真实结果。`GithubProbe.Accelerable` 就是干这个的。
- **克隆怎么测**：与「从哪下载最快」用**同一套参数**（`GitCommand.CreateSpeedTestClone`：
  `--depth 1 --single-branch --no-tags --progress`），真把约 100 MB 的文件传下来，
  结果写「24.4 秒」而不是毫秒。以前用 `--filter=blob:none --no-checkout` 只取元数据：
  一两秒就绿，可安装时下的是完整仓库——测的根本不是同一件事（中转常见「元数据秒回、拉文件很慢」）。
- **必须隔离全局 git 配置**（`isolateGlobalConfig: true`）：加速开关会往 `~/.gitconfig` 写 `insteadOf`，
  不隔离的话「直连」会被 git 自己改写成加速地址，那一行的绿是错的那条线给的。
- **进度要报出来**：`GitProgressParser`（`Services/GitProgress.cs`，与测速共用一份正则）解析 stderr 的
  `Receiving objects: 45% …, 1.2 MiB/s`，折算成 0~100 后经 `IProgress<GithubCloneProgress>`
  交给界面写成文字进度。没有进度时界面只有一个「探测中…」，用户看不出它到底在传东西还是卡死了。
- **加速开着仍走「加速 → 直连」两条**（与 HTTP 三条同规矩）：中转自己也会 403，只试一条等于白丢一条线路。
  结果带上线路与出口（「24.4 秒 · 加速 · 走代理」），代理开没开一眼可见。
- 克隆有**硬超时，超了就是不可用**（不是「通了只是慢」）：
  - 单条线路 `CloneRouteTimeout = 45 秒`：约 100 MB 要在 45 秒内下完，等于 ≥2.3 MB/s；
  - 整个探测 `CloneBudget = 90 秒`，加速 → 直连两条线路**共用**这份预算，
    剩余不足 `MinCloneWindow = 8 秒` 就不再开下一条；
  - 到点 `GitCommand.Kill` 并返回「超时（45 秒没拉完，通道太慢）」，`MergeAttempts` 照常拼接两条线路的原因。
  - 为什么必须这么狠：装酒馆下载的就是同一份仓库，测出来「慢慢能拉完」的通道，安装时同样让人等到放弃。
- `finally` 里删目录（只读文件先归位，删不掉重试 8 次）；开测前 `TempDir.ClearStale("gh-connect", 2 小时)`
  收掉上次强关的残留。同时设 `GIT_TERMINAL_PROMPT=0` / `GIT_ASKPASS=echo` / `GIT_LFS_SKIP_SMUDGE=1`，
  别让凭据框或 LFS 把测试挂住；代理通过 `-c http.proxy= -c https.proxy=` 传（git 不认 PAC）。
- **克隆的进度是文字，跟引导「从哪下载最快」一模一样**：拉取中那一行右侧写
  「下载 45% · 10.56 MiB/s」（`ApplyCloneProgress`：阶段 + 百分比 + 网速），
  测完换成「5.5 秒 · 直连 · 走代理」，失败写 git 的原话。
  - 曾经做过「环形进度 + 悬浮胶囊」：环是自绘 Ellipse + `StrokeDashArray`，胶囊是 Border + 尖角。
    视觉上没问题，但胶囊是 `Canvas` 里的绝对定位元素，一旦行右对齐就贴着弹窗边缘，
    文字稍长就被弹窗裁掉（截图里就剩「下载 8% · 直连 · 走代」）。**回归简单文本**：
    右对齐的 TextBlock 永远在内容区内，长度再长也只会截断加省略号，不会跑出去。
  - 想再上图形化进度，先解决「浮层要不要参与布局」：`Canvas` 溢出在 ContentDialog 里会被裁，
    要么让它占位（撑开行宽），要么换成 `Popup` 并自己算边界。
- **弹窗里的 Esc 提示要关掉**：`CreateShell` 给 `ContentDialog` 补了 Esc 快捷键，
  默认会在弹窗中间飘一个「Esc」小方块（正好压在进度行旁边，用户以为是自己长出来的）。
  设 `dialog.KeyboardAcceleratorPlacementMode = Hidden`，快捷键仍然生效，提示不再显示。

- 延迟 = 从发请求到拿到响应头。只读到响应头就断开（`ResponseHeadersRead` + `Range: 0-2048`）。
- 弹窗顶部写出「当前出口」（`DescribeExit`），免得用户猜到底走了哪条。
- 网络异常要翻成人话（`DescribeHttpError`）：`The SSL connection could not be established…`
  对用户毫无意义，界面写「TLS 握手失败（域名可能被阻断）」。
- 「最快」只统计三条 HTTP：克隆要建仓库、要握手，混进去比会失真；克隆单独在汇总里说一句
  （「克隆通道正常（24.4 秒）」），也**不写** `GithubTestResult`。
- 测试并发跑（`Task.WhenAll`），HTTP 超时 10 秒、克隆单线路 45 秒 / 总预算 90 秒；串行四条能让人等到关弹窗。
  克隆本身在线程池里跑（`Task.Run` + 轮询进程状态），不占 UI 线程。

## 首次引导（4 步）

`Views/OnboardingView.xaml` 是外壳（840×500 卡片 + 左侧 208 宽竖向时间轴 + 底部按钮），
每步的内容是 `Views/Onboarding/OnboardingStepN*.xaml` 里的独立面板，
统一实现 `Views/Onboarding/IOnboardingStep.cs`（`CanContinue` / `IsBusy` / `BlockingReason` /
`EnterAsync` / `Leave` / `Reset` + `StateChanged` 事件）。

- **左轨是时间轴，不是列表**：`OnboardingRailItem` 一节固定 71 高（圆点 24 + 标题 13/18 + 副标 11/15），
  连线画在条目内部的第二行（`Line` 2×47，从圆点底一路连到下一节圆点顶），四节叠起来才是一条不断的线。
  **连线必须显式给 Width/Height**：`Shape` 的 `MeasureOverride` 只按几何包围盒算，竖线的包围盒宽度是 0，
  靠 `Stretch` 撑开就画不出来（踩过）。跳过态用虚线 + 空心圆，`Skipped` 只在 `Finish(Skipped)` 时下发。
- **文案规范**：主文案 ≤14 汉字、说明 ≤22 汉字；`8.8.8.8`、`403`、`浅克隆`、`bin\`、`.migrate-*`
  这类技术细节一律下沉到灰字或 (i)，不占主句；路径单独一行灰字（`SetMeta`），必须是展开后的真实路径。
- **不要动这几个哨兵字符串**：`RuntimeSetup` 产出 `"完成"` / `"准备中…"`，`InstallTaskDialog` 拿它们判行胶囊；
  `MirrorBenchmark` 的 `"已取消"` 也用同一套比较。改文案只改显示侧，比较基准保持原样。
- **第 3 步必须一屏装下**：两张卡（`MinHeight=168`、`Padding=14,10`、组件行 22 高）+ 单行「胶囊 + 包说明」
  + 两个按钮；说明单独占一行会把按钮挤出可视区（踩过）。卡头留 26 右边距给 `OptionCardStyle` 的选中角标。
- **页脚「先跳过」唯一**：第 2 步限流卡里的按钮叫「跳过这项」，别再出现两颗同名按钮——
  自动化和用户都会点错。
- **外壳只做三件事**：切面板、按当前面板算页脚（`NextButton.IsEnabled = CanContinue && !IsBusy`）、
  切步骤时取消上一步的 CTS（`Leave()`）。面板改了状态必须发 `StateChanged`，否则按钮会停在旧状态。
- **完成标记仍是 `OnboardingState`**（`%LOCALAPPDATA%\AstraBrew Launcher\onboarding.state`），
  只在 `Finish()` 里写一次；「跳过引导」全程保留，两步都走同一个出口。

### 第 1 步 · 数据迁移（`Services/OnboardingMigration.cs`）

- 两条来源都先**预演**（`PlanGui` / `PlanLegacy` 纯读），用户看到清单并确认后才动磁盘。
- GUI 迁移：默认 `%AppData%\SillyTavern Launcher GUI\data\st_data`，找不到就让用户手选 config.yaml；
  整目录复制到 `GlobalTavernConfig.GlobalDataRoot`，**同名覆盖**——覆盖前把旧文件移进
  `.migrate-backup-<ts>`，逐文件写 journal，失败逆序回滚。成功后自动把 `data_location` 设为 Global
  （数据搬到了全局目录，还留在「跟随实例」等于白搬）。
- 旧版迁移：旧根就是 `AppPaths.RoamingRoot`，只按**目录内容**识别组件
  （`node.exe`→nodejs、`cmd/git.exe`→mingit、`caddy.exe`→caddy、`node_modules/pm2`→pm2），
  `lib\` → `bin\`、`data\sillytavern\data\*` 抬升一层；**不认识的目录原样保留**，
  `config.json` 不解析。目标已存在就把旧目录移进 `.migrate-old-<ts>`，绝不覆盖。
- 旧版内置 Node 是 v24、当前规范 v22：`CheckLegacyNodeAsync` 主版本不等即提示重装，
  `ReinstallNodeAsync` 先把旧目录改名保住，装失败自动还原。

### 第 2 步 · 网络检查（`Services/NetworkProbe.cs` / `MirrorBenchmark.cs`）

- 顺序：PING 8.8.8.8 → HTTPS www.baidu.com → GitHub API + Raw（并发）→ 四条镜像测速。
  「没网」和「DNS 坏了」要分开报，否则后面测 GitHub 只会给出一堆看不懂的错。
- **API 403 是限流不是断网**：判据是 `GithubProbeResult.Status == 403`（不要解析错误文案）。
  **API 或 Raw 任意一条 403 都要弹卡片**——以前只看 API，Raw 那行写着「HTTP 403」却没出口。
- **一条探测要试两条线路**（`ResolveUrls` / `MergeAttempts`）：加速开着时先 `gh-proxy.org` 再直连。
  中转侧自己也会被限流（实测 403），只试一条就下结论，会出现「开了加速反而 403、设置里却能通」的假故障。
  两条都 403 才是真限流（`Route = null`、`Error = 两条线路都返回 403`）；一条 403 一条别的错就报 403 那条。
  成功时 `Route` 写进那一行的状态（`568 ms · 直连`），加速开着却回落直连用户看得见。
- **403 卡片按「谁被限流」给动作**（`RateLimitFix`）：直连 403 → 「开加速再试」；加速 403 → 「关加速再试」；
  两条都 403 → 「再查一次」。加速明明开着却叫用户「开加速」是最招人烦的提示，别写死。
- **点「开加速再试」要立刻作废旧的那一轮**（`AbortRun` + `BeginRun` + `_runId`）：
  卡片是在测速开始**之前**弹出来的，用户很可能在四条线正拉仓库时点它。以前 `RunAsync` 只管起、
  不管停，旧的那一轮拿着旧 token 继续按**旧出口**拉到几十秒后，界面上的进度还在爬，
  用户看到的就是「点了开启加速，还在没加速的环境里测」。现在的规矩：
  - 每次 `RunAsync` 都 `BeginRun()`：取消并换掉 `_runCts`、`_runId++`，新 token 从 `_cts` 链接；
  - 点击瞬间先 `AbortRun()`（同步全局 git 要起子进程，这一两秒里旧轮次还在拉）；
  - 每个 `await` 之后 `if (runId != _runId) return;`，被取代的那轮不许写界面、不许动 `_busy`；
  - 测速的 `IProgress` 回调也认轮次（`Progress<T>` 会排队进 UI 线程，旧回调可能后到）；
  - `StartMirrorsAsync` 开跑前 `ResetMirrorRows()`，把上一轮的「下载 45% · 6 MiB/s」清回「等着」。
- **测速要真拉仓库**（`GitCommand.CreateSpeedTestClone`）：`--depth 1 --single-branch --progress`，
  四条通道各拉一次完整工作区（约 100 MB）到临时目录，谁先拉完用谁。
  **不能用 `CreateClone`**（那是元数据克隆：`--filter=blob:none --no-checkout`，一两秒就「成功」）——
  拿它测速，四条线永远秒回，名次与真实安装速度毫无关系。
- **拉取中要显示进度**：`--progress` 让 git 把 `Receiving objects / Resolving deltas / Updating files`
  写进 stderr，`MirrorBenchmark` 解析后折算成 0~100（下载 3~85、整理 85~95、写入 95~99），
  按整数百分比节流后经 `IProgress<MirrorProgress>` 发给界面（`下载 45% · 6.3 MB/s`）。
  必须 `BeginErrorReadLine` 读流，且 stdout 也要读掉，否则管道写满会把 git 卡死。
- **第一条拉完就定胜负**：胜者出现后再等 6 秒（`GraceAfterFirstWin`），其余直接取消并显示
  「没等它拉完」——为一条死线路挂满 90 秒超时没有意义。单条超时 90 秒。
- **代理开着时 GitHub 拆成两条**（`BuildChannels`）：`GitHub 直连 · 不走代理` / `· 走代理`、
  `GitHub 加速 · 不走代理` / `· 走代理`，共六条；代理没开就只有四条、名字也不带后缀。
  镜像（Gitee / GitCode）**永远直连**——中转方不认镜像站，套代理只会更慢。
  「不走代理」的克隆要显式 `-c http.proxy= -c https.proxy=` 并清 `HTTP_PROXY` 等环境变量，
  只靠「不给 -c」拦不住 git 从环境里捡代理。
- **选中即对齐设置**（`ApplyAsync`），**但只对 GitHub 线路**：
  - GitHub 加速线 → 开加速并同步 `insteadOf`；GitHub 直连线 → 关掉加速
    （否则 git 会把直连地址改写成加速地址，最快等于白测）；标着「不走代理」→ 代理设为「不使用代理」；
  - **Gitee / GitCode 镜像胜出时只换 `download_channel`，加速与代理一个都不碰**。
    镜像本来不走 GitHub，而加速开关管的是 Raw / API / 安装包那些直链；
    以前「镜像更快」顺手把加速关了，用户刚为 Raw 打开的加速转头就被关掉，
    到第 3 步装工具时又连不上 GitHub（实际踩到过）。
- 应用被强关时会留下没删干净的克隆目录：`TempDir.ClearStale("mirror-test", 2 小时)` 在每轮开测前收一次。
- **单位是秒**：`MirrorBenchmark.DescribeElapsed` 把毫秒折成「13.8 秒」；界面不要再出现 `ms`。
  克隆耗时也**不写** `GithubTestResult`（那是 HTTP 延迟，混进去会让设置页显示「最佳 13800 ms」）。
- **必须隔离 `GIT_CONFIG_GLOBAL`（指向空文件）**：加速开关会往 `~/.gitconfig` 写 `insteadOf`，
  不隔离的话「GitHub 直连」会被 git 自己改写，四条通道实际只剩三条。
- 继续判据：ping ✔ && dns ✔ && (api ✔ || 已跳过 403) && raw ✔ && 至少一条镜像可用。
- **设置页的连通性弹窗同理**（`ShowGithubTestAsync` / `ProbeGithubAsync`）：每一轮自带
  `CancellationTokenSource`，「重新测试」或**出口（加速 / 代理）被改动**都会先取消旧的一轮再重跑，
  顶部那行「当前出口」一并刷新；被取消的那轮结果不写进界面（`token.IsCancellationRequested` 守卫），
  `Progress<GithubCloneProgress>` 同样先看 token 再写「下载 45%」。
- **Raw 不通时也要给出口**：403 走「限流卡片」（`ShowRateLimit`），**非 403 的失败**（TLS 握手失败、
  域名解析失败、连接重置）走 `ShowBlocked` —— 同一张卡片，文案写实际原因：
  - 加速没开 → 「GitHub 文件地址连不上：TLS 握手失败（域名可能被阻断）。打开加速换条线路就能继续。」
    + 按钮「开加速再试」（一键开加速 + 重测）；
  - 加速已经开着还连不上 → 不再劝「开加速」，改成提示检查代理 + 按钮「再查一次」。
  以前只有 403 才弹卡片，于是 Raw 那行红着「TLS 握手失败」，用户既不知道该开加速，也没地方点。
- 卡片出现时**测速照跑**（不 return）：用户能顺便看到哪条镜像快；`_busy` 在测速结束后显式复位。

### 第 3 步 · 运行环境（`Services/RuntimeSetup.cs`）

- 两侧（内置 / 系统）各出一张卡片，组件状态来自 `RuntimeProbe`；每行都有 (i) 说明「用途 / 缺失后果」。
- **基础包 = Node.js + npm**（普通模式跑酒馆的最小集合），Git / Caddy / PM2 属完整包，缺了不挡路。
- **按钮能不能点，看「这一包还剩几项」**：`RuntimeSetup.Plan(side, _package, profile).Count`。只装必需且已齐 → 0 项，
  按钮显示「已经装齐了」并禁用（否则点下去只弹一句「这边已经齐了」，用户以为能补全）；
  完整包全齐时保留「重装一遍」入口。
- 自动选中「能跑的那一侧」并写 `LauncherSettings.Runtime`（全局只有一套运行环境，
  引导里选的和仪表盘显示必须同源）。
- **切换环境不弹安装窗**：`Side_Checked` 只刷新卡片与按钮。用户可能只是想看看这边缺什么，
  自动弹窗会像「已经动手装了」，而且默认包会让人误以为只装一项就完事。
- **安装窗顶部写清「这一包的范围」**：`InstallTaskDialog.ShowAsync(..., note: ...)`。
  默认选「只装必需」时清单里只有 Node.js（npm 随 Node 一起），不加说明就像漏了
  Git / Caddy / PM2；`note` 写「这次只装基础包（Node.js + npm）。Git、Caddy、PM2 在下面的「一次装齐」里」。
  选「一次装齐」时清单会真的列出 Git / Caddy / PM2 三项（已实测）。
- 内置侧走 `InstallTaskDialog`：`ContentDialog.Closing` 里取消关闭（倒计时阶段与安装结束后放行），
  3 秒倒计时自动开始，行内进度 + 总进度按权重合成，串行安装（PM2 依赖 Node）。
  系统侧**不代装**（要 UAC / msi 交互）：主按钮 `InstallButton` 在系统侧是**禁用态**（`IsEnabled = !_busy && builtin`）+ ToolTip 说明，旁边一颗 `SystemHelpButton`「缺什么？」用 `GhostButtonStyle` 进说明窗；`Side_Checked` 的自动弹窗只对内置侧生效。
  **按钮状态要在 `RefreshAsync` 的 finally 里再算一次**——探测期间 `_busy = true`，那时算出来的永远是禁用，漏了这一步切到内置也一直是灰的（踩过）。
- 装完必须 `ProbeAsync(force: true)` 重探，卡片版本号只能来自磁盘。

**PM2 的装法与共享**（`Services/Pm2Installer.cs`）：

- npm 包，不走 zip 那套，`npm install -g pm2@6.0.5 --prefix %AppData%\AstraBrew Launcher\bin\pm2`：
  包落在 `bin\pm2\node_modules\pm2`，命令 shim 落在 `bin\pm2\pm2.cmd`。
- **内置与系统共用同一份**：安装位置写死成 `InstallRoot`，不看用的是哪套 Node.js；
  `RuntimeProbe.ProbeSystemAsync` 也把这条路径放进候选，因此切到「系统环境」时 PM2 仍显示同一个版本
  （以前会显示「未安装」）。
- **`PM2_HOME` 也统一**：`Pm2Installer.HomePath` = `%AppData%\AstraBrew Launcher\pm2`，
  任何要跑 pm2 的子进程先过 `Pm2Installer.ApplyEnvironment(startInfo)`（注入 PM2_HOME + 把安装目录塞进 PATH），
  否则内置、系统各起一份 daemon，`pm2 ls` 看到的是两套进程。
- `IsInstalled` 要求 **package.json 与 `pm2.cmd` 都在**：早期「非全局装」只有包没有命令，
  只认 package.json 会把它当成已装、永远不会补 shim。
- 设置页 PM2 行**常显入口**（`ApplyDependency(..., alwaysInstall: true)`），未装写「安装」、已装写「重装」——
  从旧的非全局安装升级到全局安装只能靠重装。

**安装弹窗的收尾**（`InstallDialogAsync` / 模拟弹窗 / `InstallTaskDialog` 三处一致）：

- 成功 → **3 秒后自动收起**（`DispatcherQueue.CreateTimer`，一次性）：结论能看清，又不用再点一次「关闭」。
  失败 / 取消**不自动关**——那时的报错、日志路径比「关得快」重要。
- 关闭后（`dialog.Closed`）再 `RefreshEnvironmentAsync(force: true)` 探一次：
  弹窗里那份是安装当口的快照，卡片上的版本号必须以磁盘为准。引导侧由
  `OnboardingStep3Runtime` 在 `ShowAsync` 之后自己 `RefreshAsync(force: true)`，效果一样。
- 手动关闭、点「重试」都会先 `autoClose.Stop()`，避免对着已经消失的弹窗再 `Hide()`。
- **PM2 的安装弹窗没有进度条**：`InstallJob.HasProgress = false` —— npm 只吐日志、不给百分比，
  挂一条永远停在 0% 的进度条比没有更像卡死。无进度模式下：
  - 右上角换成 `ProgressRing`（一直转），细进度条与百分比文字都收起；
  - 标题固定「正在安装 PM2…」不随日志跳，npm 的当前动作写进下面一行小字（`Clip` 截到 110 字）；
  - 完成 / 失败 / 取消时环停下并收起，也不写「100%」。
  Git / Node.js / Caddy 仍走真实百分比（`HasProgress` 默认 true）。

**Caddy 的装法**（`Services/CaddyInstaller.cs`）：

- 官方 Release 的 zip，`caddy_{Version}_windows_{arch}.zip`，解压到 `bin\caddy`；
  版本只写 `Version` 一处，`ReleaseUrl` 由它拼出来。
- **按系统架构选包**：`BuildArch` 看 `RuntimeInformation.OSArchitecture` 而不是进程架构——
  启动器可能以 x64 跑在 ARM64 上（系统模拟），那时候照样该装 arm64 的原生 exe。
  Caddy 只发 windows-amd64 / windows-arm64，没有 x86，所以非 ARM64 一律 amd64。
- 下载源 `GithubAccel.ExpandSources`：**加速优先、直连兜底**（开关关着就只有直连）；
  `ComponentInstaller` 逐个源尝试，失败换下一个，全挂了才报错。
- 设置页同样常显入口（安装 / 重装）：换版本、换架构都靠重装。

### 第 4 步 · 添加实例（`Services/OnboardingInstances.cs`）

- 本地：`InstanceScan` 扫描（可取消、实时出行）或手选 `package.json`；缺依赖就地补装再启用。
- 内置：`TavernReleases` 取最新稳定版 + 当前通道 + 镜像可用性 → `TavernInstaller.InstallAsync`，
  装完 `ReloadBuiltin()` 再 `Use()`（版本以磁盘为准），缺依赖时有「补装依赖」。
- 任一实例启用后「完成」才可点；`TavernInstaller` 有 `IsRunning` 闸门，避免与版本页同时安装。

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