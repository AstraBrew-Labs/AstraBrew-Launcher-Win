using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;

namespace AstraBrew_Launcher.Services
{
    /// <summary>启动模式：决定端口怎么暴露、以及是否走浏览器。</summary>
    public enum LaunchMode
    {
        Normal,
        Desktop,
        Server,
    }

    /// <summary>服务器模式下的可访问范围。</summary>
    public enum ServiceScope
    {
        Lan,
        Wan,
    }

    /// <summary>默认浏览器。<see cref="System"/> 对应官方配置里的 <c>default</c>（跟随系统）。</summary>
    public enum BrowserKind
    {
        Chrome,
        Edge,
        Firefox,
        System,
    }

    /// <summary>
    /// 跨页面共享的酒馆启动设置。
    ///
    /// 仪表盘、酒馆配置页、设置页读写的都是这一份：任一页面修改都会通过 <see cref="Changed"/>
    /// 通知其它页面刷新，启动器也按它决定怎么拉起酒馆——不存在「设置里选了桌面模式，
    /// 仪表盘还显示普通模式，启动时又按另一份来」。
    ///
    /// 启动模式与浏览器/服务范围的落点不同：
    /// · 启动模式存在 <c>settings.toml</c>（<see cref="LauncherSettings.StartMode"/>），
    ///   它是启动器自己的偏好；
    /// · 浏览器与服务范围属于酒馆配置，改动会写回 <c>config.yaml</c>。
    /// </summary>
    public static class TavernSettings
    {
        /// <summary>启动模式 / 默认浏览器 / 服务范围发生变化。</summary>
        public static event EventHandler? Changed;

        /// <summary>白名单发生增删。</summary>
        public static event EventHandler? WhitelistChanged;

        static TavernSettings()
        {
            // 设置页改「默认启动方式」写的是 LauncherSettings；这里补一次通知，
            // 让仪表盘与酒馆配置页的模式单选项跟着动
            LauncherSettings.Changed += (_, _) =>
            {
                // 静态构造里自己也会写设置（迁移旧白名单），别把那些写入当成「外部改了配置」
                if (_loading)
                {
                    return;
                }

                // 启动模式决定白名单规则：服务器模式推导 + 锁定，普通 / 桌面用用户自己那份。
                // 这里只切换视图，不覆盖另一份数据——切回普通模式才能原样恢复。
                if (Mode != _whitelistMode)
                {
                    _whitelistMode = Mode;
                    ApplyWhitelist();
                }

                Changed?.Invoke(null, EventArgs.Empty);
            };

            LoadFromConfig();
            LoadUserWhitelist();

            _whitelistMode = Mode;
            _loading = false;

            // 先按当前模式生成一份，界面第一次打开就有内容（此时还没人会去改文件）
            ApplyWhitelist(persist: false);
        }

        /// <summary>
        /// 浏览器与服务范围的真相在 <c>config.yaml</c> 里（<c>browserLaunch.browser</c> /
        /// <c>listen</c>）。启动时先读一遍：否则仪表盘会按「默认谷歌浏览器、局域网」
        /// 显示，而文件里写的其实是 <c>default</c> 或 <c>listen: false</c>。
        /// </summary>
        private static void LoadFromConfig()
        {
            try
            {
                if (!GlobalTavernConfig.HasGlobalConfig)
                {
                    return;
                }

                Dictionary<string, string> scalars = YamlConfig.ReadScalars(GlobalTavernConfig.ReadGlobal());

                if (scalars.TryGetValue("browserLaunch.browser", out string? browser))
                {
                    _browser = browser.ToLowerInvariant() switch
                    {
                        "chrome" => BrowserKind.Chrome,
                        "edge" => BrowserKind.Edge,
                        "firefox" => BrowserKind.Firefox,
                        _ => BrowserKind.System,
                    };
                }

                if (scalars.TryGetValue("listen", out string? listen)
                    && bool.TryParse(listen, out bool allowed))
                {
                    _scope = allowed ? ServiceScope.Lan : ServiceScope.Wan;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就按默认值走，不影响启动
            }
        }

        private static BrowserKind _browser = BrowserKind.Chrome;
        private static ServiceScope _scope = ServiceScope.Lan;

        /// <summary>
        /// 启动模式。读写的是 <see cref="LauncherSettings.StartMode"/>（那份会被存进 settings.toml），
        /// 本类只是它的一层视图——两个枚举顺序一致，直接按序号换算。
        /// </summary>
        public static LaunchMode Mode
        {
            get => (LaunchMode)(int)LauncherSettings.StartMode;
            set
            {
                if (Mode == value)
                {
                    return;
                }

                LauncherSettings.SetStartMode((QuickStartMode)(int)value);
            }
        }

        public static BrowserKind Browser
        {
            get => _browser;
            set
            {
                if (_browser == value)
                {
                    return;
                }

                _browser = value;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 服务范围。<b>只改内存</b>——给「从 config.yaml 读到状态」这类同步用。
        /// 用户在界面上的改动走 <see cref="SetScope"/>，那才会落到文件里。
        /// </summary>
        public static ServiceScope Scope
        {
            get => _scope;
            set
            {
                if (_scope == value)
                {
                    return;
                }

                _scope = value;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 改服务范围并写回 <c>config.yaml</c>（<c>listen</c> / <c>listenAddress.ipv4</c>）。
        ///
        /// 仪表盘与设置页共用这一个入口：以前两边各写各的——仪表盘写文件，设置页只写
        /// settings.toml，于是「在设置里选互联网」对酒馆没有任何影响。
        /// </summary>
        public static void SetScope(ServiceScope scope, bool persist = true)
        {
            bool changed = scope != _scope;

            // 先落内存再写文件。
            // 写文件会经 LauncherSettings.Changed 触发各页面刷新，那些刷新会按 TavernSettings.Scope
            // 回填控件；要是晚一步才更新 _scope，回填就会拿到旧值，把用户刚点的选项改回去——
            // 于是「选互联网」看起来像是没点中，只有运气好才能生效。
            _scope = scope;

            if (persist)
            {
                bool lan = scope == ServiceScope.Lan;

                // 服务范围就是酒馆的 listen：开着才监听网卡
                GlobalTavernConfig.PatchScalar("listen", lan ? "true" : "false");
                GlobalTavernConfig.PatchScalar("listenAddress.ipv4", lan ? "0.0.0.0" : "127.0.0.1");

                LauncherSettings.SetServiceScope(
                    lan ? ServiceScopeSetting.Lan : ServiceScopeSetting.Internet);
            }

            // 白名单跟着服务范围走：局域网放行本机网段，互联网全开放
            ApplyWhitelist(persist);

            if (changed)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>服务范围的中文名，进日志与提示用。</summary>
        public static string ScopeName => _scope == ServiceScope.Wan ? "互联网" : "局域网";

        /// <summary>只有普通模式才和浏览器相关，其余模式相关选项应当隐藏。</summary>
        public static bool IsNormalMode => Mode == LaunchMode.Normal;

        public static string ModeName => Mode switch
        {
            LaunchMode.Desktop => "桌面模式",
            LaunchMode.Server => "服务器模式",
            _ => "普通模式",
        };

        public static string BrowserName => _browser switch
        {
            BrowserKind.Edge => "Edge",
            BrowserKind.Firefox => "火狐浏览器",
            BrowserKind.System => "系统默认浏览器",
            _ => "谷歌浏览器",
        };

        /// <summary>
        /// 白名单。
        ///
        /// 两份名单各管一段时间，互不污染：
        ///
        /// · <b>服务器模式</b>：由服务范围推导、全部锁定——局域网放行本机网段，
        ///   互联网全开放（<c>0.0.0.0/0</c> 与 <c>::/0</c>）。推导结果只写进 config.yaml，
        ///   **不碰**用户自己那份，所以切回来时不会残留 <c>0.0.0.0/0</c> 或本机网段。
        /// · <b>桌面模式</b>：固定只有 <c>127.0.0.1</c> 与 <c>::1</c>——页面是由启动器的
        ///   WebView2 从本机打开的，别人本来就不该连进来，名单没有可调之处。
        /// · <b>普通模式</b>：显示用户自己维护的那份
        ///   （存在 settings.toml 的 <c>tavern.user_whitelist</c>），只锁住两个回环地址。
        ///
        /// 不写 <c>localhost</c>：SillyTavern 只认 IP / CIDR，主机名会被判成 Invalid IP 丢掉；
        /// 它对应的回环地址是 <c>::1</c>，这里直接写 IP。
        /// </summary>
        public static ObservableCollection<WhitelistEntry> Whitelist { get; } = new();

        /// <summary>白名单当前快照。后台线程（起服务）读这个，别去读可绑定的集合。</summary>
        private static IReadOnlyList<string> _hosts = Array.Empty<string>();

        /// <summary>
        /// 用户自己维护的那份（普通模式用，含两个回环）。
        /// 服务器 / 桌面模式的推导绝不写进这里——那正是「切回来能恢复」的关键。
        /// </summary>
        private static List<string> _userHosts = new();

        /// <summary>当前白名单是按哪个模式算出来的；模式一变就要重算。</summary>
        private static LaunchMode _whitelistMode = LaunchMode.Normal;

        /// <summary>静态构造期间 LauncherSettings 的写入会回调回来，先别当作用户改了设置。</summary>
        private static bool _loading = true;

        /// <summary>当前名单的参数快照，供 <see cref="TavernRuntime"/> 在后台线程读取。</summary>
        public static IReadOnlyList<string> WhitelistHosts =>
            _hosts.Count > 0
                ? _hosts
                : WhitelistLocked ? BuildDerivedWhitelist() : LocalDefaults();

        /// <summary>服务器与桌面模式下白名单锁定，只有普通模式可编辑。</summary>
        public static bool WhitelistLocked => Mode is LaunchMode.Server or LaunchMode.Desktop;

        /// <summary>是不是桌面模式：它的白名单固定为回环，没有「服务范围」可讲。</summary>
        public static bool IsDesktopMode => Mode == LaunchMode.Desktop;

        /// <summary>是不是回环地址：这两条永远保留，删掉就连不上本机了。</summary>
        public static bool IsLoopbackHost(string value) =>
            value is "127.0.0.1" or "::1";

        /// <summary>
        /// 按启动模式算出的白名单条目（服务器 / 桌面模式用）。
        ///
        /// 桌面模式只放行回环：页面是启动器用 WebView2 从本机打开的，没有任何外部设备需要连进来；
        /// 服务器模式才按服务范围展开。
        /// </summary>
        public static IReadOnlyList<string> BuildDerivedWhitelist()
        {
            var hosts = new List<string> { "127.0.0.1", "::1" };

            if (Mode == LaunchMode.Desktop)
            {
                return hosts;
            }

            if (_scope == ServiceScope.Wan)
            {
                // 完全开放：IPv4 与 IPv6 各自一条「全部」
                hosts.Add("0.0.0.0/0");
                hosts.Add("::/0");
                return hosts;
            }

            (string? v4, _) = NetworkInfo.LocalAddresses();
            if (v4 is { Length: > 0 })
            {
                hosts.Add(v4);

                if (NetworkInfo.LocalSubnet() is { Length: > 0 } subnet)
                {
                    hosts.Add(subnet);
                }
            }

            return hosts;
        }

        /// <summary>普通 / 桌面模式的初始名单：回环 + 本机地址与本机网段（都可删，回环除外）。</summary>
        private static IReadOnlyList<string> LocalDefaults()
        {
            var hosts = new List<string> { "127.0.0.1", "::1" };

            (string? v4, _) = NetworkInfo.LocalAddresses();
            if (v4 is { Length: > 0 })
            {
                hosts.Add(v4);

                if (NetworkInfo.LocalSubnet() is { Length: > 0 } subnet)
                {
                    hosts.Add(subnet);
                }
            }

            return hosts;
        }

        /// <summary>
        /// 读用户那份名单：优先 settings.toml，没有（老版本）就从 config.yaml 迁移一次。
        /// </summary>
        private static void LoadUserWhitelist()
        {
            List<string> stored = SplitHosts(LauncherSettings.UserWhitelist);

            if (stored.Count == 0)
            {
                // 老版本没有这个字段：把 config.yaml 里那份当作用户名单迁移过来。
                // 但服务器模式下文件里放的是推导结果（可能含 0.0.0.0/0），不能当成用户的。
                stored = Mode == LaunchMode.Server
                    ? LocalDefaults().ToList()
                    : GlobalTavernConfig.ReadWhitelist().ToList();

                if (stored.Count == 0)
                {
                    stored = LocalDefaults().ToList();
                }
            }

            _userHosts = NormalizeHosts(stored);

            // 规范化结果写回：老名单里的重复项、/32、已被更宽网段覆盖的条目，
            // 在文件里留一份干净的就够了，不必每次启动重算一遍
            string joined = JoinHosts(_userHosts);
            if (!string.Equals(joined, LauncherSettings.UserWhitelist, StringComparison.Ordinal))
            {
                LauncherSettings.SetUserWhitelist(joined);
            }
        }

        /// <summary>
        /// 重建白名单：服务器模式按服务范围推导、桌面模式固定回环，普通模式用用户自己那份。
        /// 结果写回 <c>config.yaml</c>（白名单是酒馆实际执行的策略，界面只是它的视图）。
        /// </summary>
        /// <param name="persist">true 时同时写进 config.yaml。</param>
        public static void ApplyWhitelist(bool persist = true)
        {
            bool locked = WhitelistLocked;
            List<WhitelistEntry> entries;

            if (locked)
            {
                entries = ComposeEntries(BuildDerivedWhitelist(), locked: true);
            }
            else
            {
                if (_userHosts.Count == 0)
                {
                    _userHosts = NormalizeHosts(LocalDefaults());
                }

                // 顺手规范化：旧的名单里可能有 /32、重复项，或已被更宽网段覆盖的条目
                _userHosts = NormalizeHosts(_userHosts);
                entries = ComposeEntries(_userHosts, locked: false);
            }

            _hosts = entries.Select(entry => entry.Value).ToList();

            ReplaceWhitelist(entries);

            if (persist)
            {
                GlobalTavernConfig.ApplyWhitelist(_hosts);
            }

            WhitelistChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>规范化后决定每一项锁不锁：锁定模式下全锁，可编辑模式下只锁回环。</summary>
        private static List<WhitelistEntry> ComposeEntries(IEnumerable<string> hosts, bool locked)
        {
            var entries = new List<WhitelistEntry>();

            foreach (string value in NormalizeHosts(hosts))
            {
                entries.Add(new WhitelistEntry(value, locked || IsLoopbackHost(value)));
            }

            return entries;
        }

        /// <summary>替换界面集合；不在 UI 线程时转投过去（集合绑在 ItemsControl 上）。</summary>
        private static void ReplaceWhitelist(IReadOnlyList<WhitelistEntry> entries)
        {
            Microsoft.UI.Dispatching.DispatcherQueue? queue = AstraBrew_Launcher.App.MainWindow?.DispatcherQueue;

            if (queue is null || queue.HasThreadAccess)
            {
                Swap();
                return;
            }

            queue.TryEnqueue(Swap);

            void Swap()
            {
                Whitelist.Clear();
                foreach (WhitelistEntry entry in entries)
                {
                    Whitelist.Add(entry);
                }
            }
        }

        /// <summary>添加白名单项（仅普通 / 桌面模式）。</summary>
        public static bool TryAddWhitelist(string? raw, out string error)
        {
            if (WhitelistLocked)
            {
                error = "服务器模式下白名单由「服务范围」自动生成，不能手动修改。";
                return false;
            }

            string? value = NormalizeHost(raw);

            if (value is null)
            {
                error = $"「{(raw ?? string.Empty).Trim()}」不是有效的 IP 或网段（例如 192.168.1.20、192.168.1.0/24）。";
                return false;
            }

            foreach (string existing in _userHosts)
            {
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"「{value}」已在白名单中，无需重复添加。";
                    return false;
                }

                // 已有 192.168.8.0/24 时再填 192.168.8.21 是重复的：它本来就被放行了
                if (Covers(existing, value))
                {
                    error = $"「{value}」已被 {existing} 覆盖，无需重复添加。";
                    return false;
                }
            }

            // 反过来：新加的网段可能吞掉已有条目（加 10.0.0.0/8 之后 10.1.2.3 就是废话），
            // 顺手清掉，名单里不留冗余项
            _userHosts.RemoveAll(existing =>
                !IsLoopbackHost(existing) && Covers(value, existing));

            _userHosts.Add(value);
            SyncUserHosts();

            error = string.Empty;
            return true;
        }

        /// <summary>删除白名单项（回环与服务器模式下的条目删不掉）。</summary>
        public static bool RemoveWhitelist(WhitelistEntry? entry)
        {
            if (WhitelistLocked
                || entry is null
                || entry.IsLocked
                || !Whitelist.Contains(entry))
            {
                return false;
            }

            _userHosts.RemoveAll(item => string.Equals(item, entry.Value, StringComparison.OrdinalIgnoreCase));
            SyncUserHosts();
            return true;
        }

        /// <summary>
        /// 把用户那份写回 settings.toml，并同步界面与 config.yaml。
        /// 只有用户自己增删时才调——模式切换不能走这里（那会把服务器模式的推导写进用户名单）。
        /// </summary>
        private static void SyncUserHosts()
        {
            _userHosts = NormalizeHosts(_userHosts);

            string joined = JoinHosts(_userHosts);
            if (!string.Equals(joined, LauncherSettings.UserWhitelist, StringComparison.Ordinal))
            {
                LauncherSettings.SetUserWhitelist(joined);
            }

            ApplyWhitelist();
        }

        // ==================== 规范化与去重 ====================

        private static List<string> SplitHosts(string? raw) =>
            (raw ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        private static string JoinHosts(IEnumerable<string> hosts) => string.Join(",", hosts);

        /// <summary>
        /// 规范化一条：去空格、CIDR 主机位归零（192.168.8.21/24 → 192.168.8.0/24）、
        /// /32 与 /128 折成单个地址。不是 IP / CIDR 返回 null。
        /// </summary>
        private static string? NormalizeHost(string? raw)
        {
            string value = (raw ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                return null;
            }

            int slash = value.IndexOf('/');
            string addressPart = slash >= 0 ? value[..slash] : value;

            // SillyTavern 只认 IP / CIDR：主机名（含 localhost）会被它判成 Invalid IP 丢掉
            if (!IPAddress.TryParse(addressPart, out IPAddress? address))
            {
                return null;
            }

            int max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            int prefix = max;

            if (slash >= 0
                && (!int.TryParse(value[(slash + 1)..], out prefix) || prefix < 0 || prefix > max))
            {
                return null;
            }

            byte[] bytes = address.GetAddressBytes();

            for (int i = 0; i < bytes.Length; i++)
            {
                int bits = prefix - i * 8;

                if (bits >= 8)
                {
                    continue;
                }

                bytes[i] = bits <= 0 ? (byte)0 : (byte)(bytes[i] & (0xFF << (8 - bits)));
            }

            string network = new IPAddress(bytes).ToString();
            return prefix == max ? network : $"{network}/{prefix}";
        }

        /// <summary>
        /// 去重：完全相同的合并；被更宽网段覆盖的丢掉（保留那条网段）。
        /// 回环固定放最前，且永远不删。
        /// </summary>
        private static List<string> NormalizeHosts(IEnumerable<string> hosts)
        {
            var result = new List<string>();

            foreach (string raw in hosts)
            {
                string? value = NormalizeHost(raw);

                if (value is null)
                {
                    continue;
                }

                bool duplicate = result.Any(existing =>
                    string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)
                    || Covers(existing, value));

                if (duplicate)
                {
                    continue;
                }

                // 新条目覆盖了前面的窄条目：192.168.0.0/16 出现后，192.168.8.21 就是废话
                result.RemoveAll(existing =>
                    !IsLoopbackHost(existing) && Covers(value, existing));

                result.Add(value);
            }

            foreach (string loopback in new[] { "::1", "127.0.0.1" })
            {
                if (!result.Any(item => string.Equals(item, loopback, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Insert(0, loopback);
                }
            }

            return result;
        }

        /// <summary>解析成「地址 + 前缀」；条目本身应当已经规范化过。</summary>
        private static bool TryParseRange(string entry, out IPAddress address, out int prefix)
        {
            address = IPAddress.None;
            prefix = 0;

            int slash = entry.IndexOf('/');
            string addressPart = slash >= 0 ? entry[..slash] : entry;

            if (!IPAddress.TryParse(addressPart, out IPAddress? parsed))
            {
                return false;
            }

            int max = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            int given = slash >= 0 && int.TryParse(entry[(slash + 1)..], out int value) ? value : max;

            if (given < 0 || given > max)
            {
                return false;
            }

            address = parsed;
            prefix = given;
            return true;
        }

        /// <summary><paramref name="outer"/> 是否覆盖 <paramref name="inner"/>（同族、前缀更短或相等、网络位相同）。</summary>
        private static bool Covers(string outer, string inner)
        {
            if (!TryParseRange(outer, out IPAddress outerAddress, out int outerPrefix)
                || !TryParseRange(inner, out IPAddress innerAddress, out int innerPrefix))
            {
                return false;
            }

            if (outerAddress.AddressFamily != innerAddress.AddressFamily || outerPrefix > innerPrefix)
            {
                return false;
            }

            byte[] left = outerAddress.GetAddressBytes();
            byte[] right = innerAddress.GetAddressBytes();

            int full = outerPrefix / 8;
            int remainder = outerPrefix % 8;

            for (int i = 0; i < full; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            if (remainder == 0)
            {
                return true;
            }

            int mask = 0xFF << (8 - remainder);
            return (left[full] & mask) == (right[full] & mask);
        }
    }

    /// <summary>白名单条目视图模型：锁定项不提供删除入口，并显示「锁定」标记。</summary>
    public sealed class WhitelistEntry
    {
        public WhitelistEntry(string value, bool isLocked)
        {
            Value = value;
            IsLocked = isLocked;
        }

        public string Value { get; }

        public bool IsLocked { get; }

        public Visibility LockVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DeleteVisibility => IsLocked ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>锁定项显示锁，自定义项显示地球，一眼区分默认与手动添加。</summary>
        public string Glyph => IsLocked ? "\uE72E" : "\uE774";

        public string AutomationName => IsLocked ? $"{Value}（已锁定）" : Value;
    }
}
