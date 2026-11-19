using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AstraBrewSetup;

/// <summary>
/// 安装向导的主窗口。
///
/// 布局照 Tauri 2 打出来的 NSIS 安装包来：左边一条竖向品牌条（欢迎页 / 完成页），
/// 内页用一条窄标题栏，底部右侧一排按钮（主按钮在最左，取消在最右）。
/// 颜色、字号、间距都刻意贴近那套模板，而不是 Windows Installer 的灰底对话框。
/// </summary>
internal sealed class SetupWizard : Form
{
    private const int SideWidth = 164;
    private const int HeaderHeight = 64;
    private const int FooterHeight = 58;
    private const int ContentPadding = 26;

    private static readonly Color Accent = Color.FromArgb(0x2B, 0x88, 0xD8);
    private static readonly Color AccentHover = Color.FromArgb(0x23, 0x74, 0xBC);
    private static readonly Color TextPrimary = Color.FromArgb(0x1F, 0x23, 0x28);
    private static readonly Color TextSecondary = Color.FromArgb(0x57, 0x60, 0x6A);
    private static readonly Color Border = Color.FromArgb(0xDF, 0xE3, 0xE8);
    private static readonly Color TrackColor = Color.FromArgb(0xEB, 0xEF, 0xF3);

    private readonly string _msiPath;
    private readonly string _version;
    private readonly string _product;
    private readonly bool _uninstallMode;
    private readonly bool _purge;
    private readonly MsiEngine.ProgressState _progress = new();

    private readonly Panel _side;
    private readonly Panel _header;
    private readonly Panel _body;
    private readonly Panel _footer;
    private readonly Label _headerTitle;
    private readonly Label _headerSubtitle;

    private readonly Panel _pageWelcome;
    private readonly Panel _pageOptions;
    private readonly Panel _pageProgress;
    private readonly Panel _pageFinish;
    private readonly Panel _pageUninstall;
    private readonly Panel _pageDone;

    private readonly RadioButton _scopeAll;
    private readonly RadioButton _scopeMe;
    private readonly TextBox _pathBox;
    private readonly Label _spaceHint;
    private readonly Label _progressTitle;
    private readonly Label _progressAction;
    private readonly Label _progressPercent;
    private readonly FlatProgress _bar;
    private readonly CheckBox _runAfter;
    private readonly CheckBox _removeData;
    private readonly Label _doneText;

    private readonly FlatButton _primary;
    private readonly FlatButton _secondary;
    private readonly FlatButton _cancel;

    private readonly System.Windows.Forms.Timer _ticker;

    /// <summary>用户手动改过路径之后，再切作用域就别覆盖他的选择。</summary>
    private bool _pathTouched;

    /// <summary>正在安装的目标目录：进度按这里已写入的字节数估算。</summary>
    private string? _installingDir;

    /// <summary>预计写入的总字节数（约等于 MSI 体积，实测两者基本一致）。</summary>
    private long _expectedBytes;

    /// <summary>上次枚举目录的时间与结果：每 120ms 递归数一遍文件太浪费。</summary>
    private DateTime _lastEstimateAt;

    private int _lastEstimate;

    private bool _busy;

    private Page _page = Page.Welcome;

    private enum Page
    {
        Welcome,
        Options,
        Progress,
        Finish,
        Uninstall,
        Done,
    }

    /// <summary>按系统界面语言给文案：中文环境说中文，其余说英文。</summary>
    private static bool Zh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        .Equals("zh", StringComparison.OrdinalIgnoreCase);

    private static string S(string zh, string en) => Zh ? zh : en;

    /// <summary>侧栏与标题里用的短名：去掉「（测试版）」这类括号后缀，渠道信息在版本行里。</summary>
    private string ShortProduct
    {
        get
        {
            int cut = _product.IndexOfAny(new[] { '（', '(' });
            return cut > 0 ? _product[..cut].Trim() : _product;
        }
    }

    public SetupWizard(string msiPath, string product, string version, bool uninstall, bool purge)
    {
        _msiPath = msiPath;
        _product = product;
        _version = version;
        _uninstallMode = uninstall;
        _purge = purge;

        Text = uninstall
            ? S($"{product} 卸载程序", $"{product} Uninstaller")
            : S($"{product} 安装程序", $"{product} Setup");

        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize = new Size(520, 380);
        AutoScaleMode = AutoScaleMode.Dpi;

        _side = BuildSide();
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        _header = BuildHeader(out _headerTitle, out _headerSubtitle);
        _footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = Color.White, Padding = new Padding(0, 12, 14, 12) };
        _body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(ContentPadding, 18, ContentPadding, 10) };

        _primary = MakeButton(S("下一步", "Next"), primary: true);
        _secondary = MakeButton(S("上一步", "Back"), primary: false);
        _cancel = MakeButton(S("取消", "Cancel"), primary: false);

        _primary.Click += OnPrimary;
        _secondary.Click += OnSecondary;
        _cancel.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.White,
        };

        // RightToLeft：先加的在最右，所以「取消」永远在最外侧
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_primary);
        buttons.Controls.Add(_secondary);
        _footer.Controls.Add(buttons);

        // 页面
        _pageWelcome = BuildWelcome();
        _pageOptions = BuildOptions(out _scopeAll, out _scopeMe, out _pathBox, out _spaceHint);
        _pageProgress = BuildProgress(out _progressTitle, out _progressAction, out _progressPercent, out _bar);
        _pageFinish = BuildFinish(out _runAfter);
        _pageUninstall = BuildUninstall(out _removeData);
        _pageDone = BuildDone(out _doneText);

        _body.Controls.Add(_pageDone);
        _body.Controls.Add(_pageUninstall);
        _body.Controls.Add(_pageFinish);
        _body.Controls.Add(_pageProgress);
        _body.Controls.Add(_pageOptions);
        _body.Controls.Add(_pageWelcome);

        right.Controls.Add(_body);
        right.Controls.Add(_header);
        right.Controls.Add(_footer);

        // Dock 顺序：先 Fill 再 Left，否则侧栏会把整块内容挤到右边
        Controls.Add(right);
        Controls.Add(_side);

        _ticker = new System.Windows.Forms.Timer { Interval = 120 };
        _ticker.Tick += (_, _) => PumpProgress();

        _scopeAll.CheckedChanged += (_, _) => ApplyScope();
        _scopeMe.CheckedChanged += (_, _) => ApplyScope();
        _pathBox.TextChanged += (_, _) => _pathTouched = true;

        _scopeMe.Checked = !uninstall;
        _scopeAll.Checked = uninstall;

        FormClosing += OnFormClosing;
        Shown += (_, _) => GoTo(uninstall ? Page.Uninstall : Page.Welcome);
    }

    // ==================== 外观构件 ====================

    private static FlatButton MakeButton(string text, bool primary) => new()
    {
        Text = text,
        Primary = primary,
        Width = primary ? 96 : 88,
        Height = 31,
        Margin = new Padding(8, 0, 0, 0),
    };

    private Panel BuildSide()
    {
        var side = new SidePanel { Dock = DockStyle.Left, Width = SideWidth };

        var icon = new PictureBox
        {
            Image = LoadIcon(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(24, 26, 40, 40),
            BackColor = Color.Transparent,
        };

        // 两行字标：第一行品牌、第二行产品，宽度不够时也不会挤成一团
        string[] words = ShortProduct.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        var name = new Label
        {
            Text = words.Length > 0 ? words[0] : ShortProduct,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(24, 78, SideWidth - 40, 26),
            BackColor = Color.Transparent,
        };

        var sub = new Label
        {
            Text = words.Length > 1 ? words[1] : string.Empty,
            ForeColor = Color.FromArgb(0xDC, 0xEC, 0xFA),
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(24, 104, SideWidth - 40, 22),
            BackColor = Color.Transparent,
        };

        var ver = new Label
        {
            Text = _version,
            ForeColor = Color.FromArgb(0xCF, 0xE6, 0xFA),
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            AutoSize = false,
            Bounds = new Rectangle(24, 144, SideWidth - 44, 20),
            BackColor = Color.Transparent,
        };

        var vendor = new Label
        {
            Text = "AstraBrew Labs",
            ForeColor = Color.FromArgb(0xB6, 0xD4, 0xEE),
            Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point),
            AutoSize = false,
            Bounds = new Rectangle(24, 334, SideWidth - 44, 18),
            BackColor = Color.Transparent,
        };

        side.Controls.Add(icon);
        side.Controls.Add(name);
        side.Controls.Add(sub);
        side.Controls.Add(ver);
        side.Controls.Add(vendor);
        return side;
    }

    private static Panel BuildHeader(out Label title, out Label subtitle)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = Color.White };

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Border };

        var icon = new PictureBox
        {
            Image = LoadIcon(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(26, 16, 30, 30),
        };

        title = new Label
        {
            Bounds = new Rectangle(66, 14, 420, 22),
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = TextPrimary,
            AutoSize = false,
        };

        subtitle = new Label
        {
            Bounds = new Rectangle(66, 35, 420, 18),
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextSecondary,
            AutoSize = false,
        };

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(icon);
        header.Controls.Add(line);
        return header;
    }

    private Panel BuildWelcome()
    {
        var page = NewPage();

        // 侧栏已占 164 宽，标题再塞产品全名会折成三行；产品名放进正文更耐看
        var title = NewTitle(S("欢迎使用安装向导", "Welcome to the Setup Wizard"));

        var body = NewBody(S(
            $"这个向导将引导你把 {_product} 安装到这台电脑上。\n\n建议继续前关闭其他正在运行的程序，安装完成后可能需要重启酒馆服务。",
            $"This wizard will install {_product} on this computer.\n\nClose other running programs before continuing."));

        var tip = new Label
        {
            Text = S($"版本 {_version}", $"Version {_version}"),
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            AutoSize = false,
            Bounds = new Rectangle(0, 200, 300, 20),
            Name = "field",
        };

        page.Controls.Add(tip);
        page.Controls.Add(body);
        page.Controls.Add(title);
        return page;
    }

    private Panel BuildOptions(out RadioButton allScope, out RadioButton meScope, out TextBox pathBox, out Label hint)
    {
        var page = NewPage();

        var title = NewTitle(S("安装选项", "Installation options"));

        var scopeLabel = NewFieldLabel(S("安装范围", "Install for"), 44);

        var all = new RadioButton
        {
            Text = S("为这台电脑上的所有用户安装", "Install for all users"),
            Bounds = new Rectangle(0, 74, 320, 24),
            Checked = true,
        };

        var me = new RadioButton
        {
            Text = S("仅为当前用户安装", "Install for me only"),
            Bounds = new Rectangle(0, 100, 320, 24),
        };

        var pathLabel = NewFieldLabel(S("安装位置", "Install location"), 138);

        var path = new TextBox
        {
            Bounds = new Rectangle(0, 164, 300, 28),
            Name = "pathBox",
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        };

        var browse = new Button
        {
            Text = S("浏览…", "Browse…"),
            Name = "browseButton",
            Bounds = new Rectangle(308, 163, 74, 29),
            FlatStyle = FlatStyle.System,
        };

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = S("选择安装位置", "Choose install location"),
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(path.Text) ? path.Text : string.Empty,
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                path.Text = dialog.SelectedPath;
            }
        };

        hint = new Label
        {
            Bounds = new Rectangle(0, 202, 320, 20),
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            AutoSize = false,
            Text = S("安装包约 137 MB，程序目录会写入所选位置。", "About 137 MB on disk. Files go to the folder above."),
        };

        page.Controls.Add(hint);
        page.Controls.Add(browse);
        page.Controls.Add(path);
        page.Controls.Add(pathLabel);
        page.Controls.Add(me);
        page.Controls.Add(all);
        page.Controls.Add(scopeLabel);
        page.Controls.Add(title);

        allScope = all;
        meScope = me;
        pathBox = path;
        return page;
    }

    private Panel BuildProgress(out Label title, out Label action, out Label percent, out FlatProgress bar)
    {
        var page = NewPage();

        title = new Label
        {
            Name = "progressTitle",
            Bounds = new Rectangle(0, 26, 320, 24),
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextPrimary,
            AutoSize = false,
            Text = S("正在准备…", "Preparing…"),
        };

        bar = new FlatProgress
        {
            Name = "progressBar",
            Bounds = new Rectangle(0, 64, 300, 8),
        };

        action = new Label
        {
            Name = "progressAction",
            Bounds = new Rectangle(0, 108, 300, 20),
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextSecondary,
            AutoSize = false,
        };

        percent = new Label
        {
            Name = "progressPercent",
            Bounds = new Rectangle(0, 84, 300, 20),
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Accent,
            AutoSize = false,
        };

        page.Controls.Add(percent);
        page.Controls.Add(action);
        page.Controls.Add(bar);
        page.Controls.Add(title);
        return page;
    }

    private Panel BuildFinish(out CheckBox run)
    {
        var page = NewPage();

        var title = NewTitle(S("完成安装", "Completing Setup"));

        var body = NewBody(S(
            $"{_product} 已安装到这台电脑上。桌面上和开始菜单里都有快捷方式。",
            $"{_product} is installed. Shortcuts were added to your desktop and Start menu."));

        run = new CheckBox
        {
            Text = S($"立即运行 {ShortProduct}", $"Run {ShortProduct}"),
            Bounds = new Rectangle(0, 170, 320, 24),
            Checked = true,
        };

        page.Controls.Add(run);
        page.Controls.Add(body);
        page.Controls.Add(title);
        return page;
    }

    private Panel BuildUninstall(out CheckBox keep)
    {
        var page = NewPage();

        var title = NewTitle(S($"卸载 {_product}", $"Uninstall {_product}"));
        title.Font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);

        var body = NewBody(S(
            $"将从这台电脑上移除 {_product}。\n\n酒馆实例、角色卡与聊天记录不会被删除。",
            $"This will remove {_product} from your computer.\n\nTavern instances and chat data are kept."));

        keep = new CheckBox
        {
            Text = S("同时删除配置与缓存（设置、日志、内置运行时）", "Also delete settings and cache"),
            Bounds = new Rectangle(0, 168, 320, 24),
            Checked = false,
        };

        page.Controls.Add(keep);
        page.Controls.Add(body);
        page.Controls.Add(title);
        return page;
    }

    /// <summary>卸载完成后单独放一页，避免复用一个标题还写着「安装」的控件。</summary>
    private Panel BuildDone(out Label text)
    {
        var page = NewPage();

        var title = NewTitle(S("卸载完成", "Uninstall complete"));

        text = NewBody(string.Empty);

        page.Controls.Add(text);
        page.Controls.Add(title);
        return page;
    }

    private static Panel NewPage() => new() { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };

    private static Label NewTitle(string text) => new()
    {
        Name = "title",
        Text = text,
        Bounds = new Rectangle(0, 0, 452, 34),
        Font = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold, GraphicsUnit.Point),
        ForeColor = TextPrimary,
        AutoSize = false,
    };

    private static Label NewBody(string text) => new()
    {
        Name = "body",
        Text = text,
        Bounds = new Rectangle(0, 50, 452, 96),
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        ForeColor = Color.FromArgb(0x3A, 0x41, 0x48),
        AutoSize = false,
    };

    private static Label NewFieldLabel(string text, int top) => new()
    {
        Name = "field",
        Text = text,
        Bounds = new Rectangle(0, top, 300, 20),
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        ForeColor = TextSecondary,
        AutoSize = false,
    };

    private static Icon? _iconCache;

    private static Image? LoadIcon()
    {
        try
        {
            _iconCache ??= Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            return _iconCache?.ToBitmap();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ==================== 页面流转 ====================

    private void GoTo(Page page)
    {
        _page = page;

        foreach (Panel candidate in new[] { _pageWelcome, _pageOptions, _pageProgress, _pageFinish, _pageUninstall, _pageDone })
        {
            candidate.Visible = false;
        }

        Panel target = page switch
        {
            Page.Welcome => _pageWelcome,
            Page.Options => _pageOptions,
            Page.Progress => _pageProgress,
            Page.Finish => _pageFinish,
            Page.Uninstall => _pageUninstall,
            _ => _pageDone,
        };

        target.Visible = true;
        target.BringToFront();

        // 欢迎页 / 完成页用左侧品牌条，内页用顶部标题栏——和 NSIS 模板一致
        bool sidebar = page is Page.Welcome or Page.Finish or Page.Done;
        _side.Visible = sidebar;
        _header.Visible = !sidebar;

        // 侧栏显隐会改变内容区宽度，必须放在上面两行之后
        FitToWidth(target);

        switch (page)
        {
            case Page.Welcome:
                SetFooter(S("下一步", "Next"), back: false, cancel: true);
                AcceptButton = _primary;
                break;

            case Page.Options:
                _headerTitle.Text = S("安装", "Install");
                _headerSubtitle.Text = S("选择安装范围与目标文件夹，然后点「安装」。", "Pick a scope and folder, then choose Install.");
                SetFooter(S("安装", "Install"), back: true, cancel: true);
                _primary.Width = 96;
                AcceptButton = _primary;
                break;

            case Page.Progress:
                SetFooter(null, back: false, cancel: true);
                _cancel.Enabled = false;
                _headerTitle.Text = _uninstallMode
                    ? S("正在卸载", "Uninstalling")
                    : S("正在安装", "Installing");
                _headerSubtitle.Text = S("这通常需要十几秒到一分钟。", "This usually takes under a minute.");
                _progressTitle.Text = _uninstallMode
                    ? S($"正在卸载 {_product}…", $"Uninstalling {_product}…")
                    : S($"正在安装 {_product}…", $"Installing {_product}…");
                break;

            case Page.Finish:
                SetFooter(S("完成", "Finish"), back: false, cancel: false);
                AcceptButton = _primary;
                break;

            case Page.Uninstall:
                _headerTitle.Text = S("卸载", "Uninstall");
                _headerSubtitle.Text = S("确认后开始卸载。", "Review and confirm.");
                SetFooter(S("卸载", "Uninstall"), back: false, cancel: true);
                AcceptButton = _primary;
                break;

            case Page.Done:
                _doneText.Text = _purge || _removeData.Checked
                    ? S($"{_product} 已卸载，配置与缓存已一并删除。", $"{_product} was removed, including settings and cache.")
                    : S($"{_product} 已卸载，配置与缓存已保留。", $"{_product} was removed. Settings and cache were kept.");
                SetFooter(S("完成", "Finish"), back: false, cancel: false);
                AcceptButton = _primary;
                break;
        }
    }


    /// <summary>
    /// 内容区宽度随侧栏显隐变化（有侧栏时窄 164），固定坐标写的宽度一定会被裁。
    /// 这里按可用宽度重算：标题与正文折行后高度也跟着量一次，避免文字被切掉半行。
    /// </summary>
    private void FitToWidth(Panel page)
    {
        int usable = Math.Max(180, _body.ClientSize.Width - _body.Padding.Horizontal);

        Label? title = null;
        Label? body = null;
        var fields = new List<Label>();

        foreach (Control child in page.Controls)
        {
            switch (child.Name)
            {
                case "title" when child is Label label:
                    title = label;
                    label.Width = usable;
                    label.Height = Measure(label, usable, minimum: 34);
                    break;

                case "body" when child is Label label:
                    body = label;
                    label.Width = usable;
                    label.Height = Measure(label, usable, minimum: 60);
                    break;

                case "field" when child is Label label:
                    fields.Add(label);
                    label.Width = usable;
                    break;

                case "progressTitle":
                case "progressAction":
                case "progressPercent":
                    child.Width = usable;
                    break;

                case "progressBar":
                    child.Width = usable;
                    break;

                case "pathBox":
                    child.Width = Math.Max(150, usable - 82);
                    break;

                case "browseButton":
                    child.Left = Math.Max(0, usable - 74);
                    break;

                default:
                    if (child is CheckBox or RadioButton)
                    {
                        child.Width = usable;
                    }

                    break;
            }
        }

        // 只有「标题 + 正文 + 一行脚注」的页面才重排纵向位置，选项页是精确坐标，别动
        if (page == _pageWelcome || page == _pageDone)
        {
            if (title is not null && body is not null)
            {
                body.Top = title.Bottom + 14;
            }

            int top = body?.Bottom + 20 ?? 200;

            foreach (Label field in fields)
            {
                field.Top = top;
                top += 24;
            }
        }
    }

    private static int Measure(Label label, int width, int minimum)
    {
        Size size = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            new Size(width, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

        return Math.Max(minimum, size.Height + 4);
    }

    private void SetFooter(string? primaryText, bool back, bool cancel)
    {
        _primary.Visible = primaryText is not null;
        _primary.Text = primaryText ?? string.Empty;
        _primary.Enabled = !_busy;

        _secondary.Visible = back;
        _secondary.Enabled = !_busy;

        _cancel.Visible = cancel;
        _cancel.Enabled = !_busy;
    }

    private void OnPrimary(object? sender, EventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome:
                GoTo(Page.Options);
                break;

            case Page.Options:
                StartInstall();
                break;

            case Page.Finish:
                if (_runAfter.Checked)
                {
                    TryLaunchApp();
                }

                Close();
                break;

            case Page.Uninstall:
                StartUninstall();
                break;

            case Page.Done:
                Close();
                break;
        }
    }

    private void OnSecondary(object? sender, EventArgs e)
    {
        if (_page == Page.Options)
        {
            GoTo(Page.Welcome);
        }
    }

    private void ApplyScope()
    {
        if (_scopeAll.Checked == _scopeMe.Checked)
        {
            return;
        }

        _spaceHint.ForeColor = TextSecondary;

        // 用户改过路径就不再覆盖；没改过则跟着作用域给默认值
        if (!_pathTouched)
        {
            _pathBox.Text = DefaultPath(_scopeMe.Checked == false);
        }
    }

    private static string DefaultPath(bool forAllUsers)
    {
        // 两个作用域都落在「用户数据」里：所有人用 ProgramData，单人用 LocalAppData。
        // 装到 Program Files 的话，程序目录对普通用户只读，自动更新换文件还得再提权。
        return forAllUsers
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AstraBrew Launcher")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AstraBrew Launcher");
    }

    // ==================== 安装 / 卸载 ====================

    private async void StartInstall()
    {
        string dir = _pathBox.Text.Trim();

        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir))
        {
            MessageBox.Show(
                this,
                S("请选择一个有效的安装位置。", "Choose a valid install location."),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            _pathBox.Text = dir;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _busy = true;
        _progress.Reset();
        GoTo(Page.Progress);
        _ticker.Start();

        bool machine = _scopeAll.Checked;

        // MSI 的进度消息字段格式不固定（不同版本 MSI 输出还不一样），
        // 这里用「目标目录已写入多少字节」来推进度条：确定性、和数据一致。
        _installingDir = dir;
        _expectedBytes = Math.Max(1, new FileInfo(_msiPath).Length);
        _lastEstimateAt = DateTime.MinValue;
        _lastEstimate = 0;

        int code;

        try
        {
            code = await Task.Run(() => MsiEngine.Install(_msiPath, dir, machine, _progress, Zh));
        }
        catch (Exception ex)
        {
            _ticker.Stop();
            _busy = false;
            WriteLog(ex.ToString());
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            GoTo(Page.Options);
            return;
        }

        _ticker.Stop();
        _busy = false;

        int estimated = _installingDir is { Length: > 0 } ? EstimatePercent(_installingDir) : 0;
        _installingDir = null;

        WriteLog($"安装结束：退出码 {code}，估算 {estimated}%，MSI 自报峰值 {_progress.PeakPercent}%，消息 {_progress.MessageCount} 条");

        if (code is MsiEngine.Success or MsiEngine.RebootRequired)
        {
            PumpProgress(final: true);
            GoTo(Page.Finish);
            return;
        }

        ShowFailure(code);
        GoTo(Page.Options);
    }

    private async void StartUninstall()
    {
        _busy = true;
        _progress.Reset();
        GoTo(Page.Progress);
        _ticker.Start();

        int code;

        try
        {
            code = await Task.Run(() => MsiEngine.Uninstall(_msiPath, _progress, Zh));
        }
        catch (Exception ex)
        {
            _ticker.Stop();
            _busy = false;
            WriteLog(ex.ToString());
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            GoTo(Page.Uninstall);
            return;
        }

        _ticker.Stop();
        _busy = false;

        if (code is not (MsiEngine.Success or MsiEngine.RebootRequired))
        {
            ShowFailure(code);
            GoTo(Page.Uninstall);
            return;
        }

        if (_removeData.Checked || _purge)
        {
            await Task.Run(MsiEngine.RemoveUserData);
        }

        GoTo(Page.Done);
    }

    private void PumpProgress(bool final = false)
    {
        (int percent, string action, string? error) = _progress.Snapshot();

        if (!final && _installingDir is { Length: > 0 })
        {
            percent = Math.Max(percent, EstimatePercent(_installingDir));
        }

        _bar.Value = final ? 100 : percent;
        _progressPercent.Text = S($"已完成 {percent}%", $"{percent}% complete");

        if (action.Length > 0)
        {
            _progressAction.Text = action;
        }

        if (error is { Length: > 0 })
        {
            _progressAction.Text = error;
        }
    }

    /// <summary>
    /// 按目标目录已写入的字节数估算进度。写文件是安装里最耗时的一段，
    /// 这条曲线和实际观感基本一致；封顶 97%，剩下的留给注册表与快捷方式。
    /// </summary>
    private int EstimatePercent(string directory)
    {
        if ((DateTime.UtcNow - _lastEstimateAt).TotalMilliseconds < 300)
        {
            return _lastEstimate;
        }

        _lastEstimateAt = DateTime.UtcNow;

        long written = 0;

        try
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        written += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                        // 正在写的文件拿不到大小就算了
                    }
                }
            }
        }
        catch (Exception)
        {
            return 0;
        }

        if (_expectedBytes <= 0)
        {
            return 0;
        }

        _lastEstimate = (int)Math.Clamp(written * 100 / _expectedBytes, 0, 97);
        return _lastEstimate;
    }

    private void ShowFailure(int code)
    {
        (_, _, string? error) = _progress.Snapshot();

        string detail = code switch
        {
            MsiEngine.UnknownProduct => S("没有找到已安装的 AstraBrew Launcher。", "AstraBrew Launcher is not installed."),
            MsiEngine.RebootRequired => S("需要重启才能完成。", "A restart is required."),
            1603 => S("安装过程中出错，详情见 %TEMP%\\astrabrew-launcher\\setup.log。", "The installation failed. See %TEMP%\\astrabrew-launcher\\setup.log."),
            _ => S($"安装程序返回错误码 {code}。", $"The installer returned error {code}."),
        };

        WriteLog($"MSI 退出码 {code}：{error}");

        MessageBox.Show(
            this,
            error is { Length: > 0 } ? $"{detail}\n\n{error}" : detail,
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void TryLaunchApp()
    {
        string exe = Path.Combine(_pathBox.Text.Trim(), "AstraBrew Launcher.exe");

        try
        {
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
        }
        catch (Exception ex)
        {
            WriteLog(ex.ToString());
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "astrabrew-launcher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "setup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志写不了不影响安装
        }
    }

    // ==================== 自绘控件 ====================

    /// <summary>左栏：深蓝到品牌蓝的渐变，代替 NSIS 那张位图。</summary>
    private sealed class SidePanel : Panel
    {
        public SidePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var brush = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(0x12, 0x33, 0x52),
                Color.FromArgb(0x2B, 0x88, 0xD8),
                LinearGradientMode.Vertical);

            e.Graphics.FillRectangle(brush, ClientRectangle);

            // 底部一条更深的色带，压住「AstraBrew Labs」那一行
            using var shade = new SolidBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            e.Graphics.FillRectangle(shade, 0, Height - 46, Width, 46);
        }
    }

    /// <summary>扁头按钮：主按钮品牌蓝，其余浅灰描边（贴近 NSIS 的按钮观感，但更干净）。</summary>
    private sealed class FlatButton : Button
    {
        private bool _hover;

        public bool Primary { get; set; }

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill;
            Color text;

            if (!Enabled)
            {
                fill = Primary ? Color.FromArgb(0xB9, 0xD6, 0xEE) : Color.FromArgb(0xF2, 0xF3, 0xF4);
                text = Primary ? Color.White : Color.FromArgb(0x9A, 0xA0, 0xA6);
            }
            else if (Primary)
            {
                fill = _hover ? Color.FromArgb(0x23, 0x74, 0xBC) : Accent;
                text = Color.White;
            }
            else
            {
                fill = _hover ? Color.FromArgb(0xF0, 0xF4, 0xF8) : Color.White;
                text = TextPrimary;
            }

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(rect, 4))
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);

                if (!Primary)
                {
                    using var pen = new Pen(Enabled ? Border : Color.FromArgb(0xE8, 0xEA, 0xEC));
                    g.DrawPath(pen, path);
                }
            }

            TextRenderer.DrawText(
                g,
                Text,
                Font,
                ClientRectangle,
                text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath Round(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>细进度条：默认的 WinForms 进度条是绿色方块条，跟这套界面格格不入。</summary>
    private sealed class FlatProgress : Control
    {
        private int _value;

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (clamped != _value)
                {
                    _value = clamped;
                    Invalidate();
                }
            }
        }

        public FlatProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(track, Height / 2))
            {
                using var brush = new SolidBrush(TrackColor);
                g.FillPath(brush, path);
            }

            if (_value <= 0)
            {
                return;
            }

            int width = Math.Max(Height, track.Width * _value / 100);
            var fill = new Rectangle(0, 0, width, track.Height);

            using (var path = Round(fill, Height / 2))
            {
                using var brush = new SolidBrush(Accent);
                g.FillPath(brush, path);
            }
        }

        private static GraphicsPath Round(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
