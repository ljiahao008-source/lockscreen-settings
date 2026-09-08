using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LockscreenGui;

/// <summary>
/// 主界面（v3.1 视觉重构）：功能逻辑与 v3.0 完全一致（直接调用 PowerManager / SaverManager /
/// DynlockManager / ConfigIO，不经 HTTP、无后台进程）。
/// v3.1 变化仅在视觉层：统一色板 / 圆角卡片替代 GroupBox / 自绘三态按钮 / 现代菜单与状态栏
/// / 窗口淡入 / 操作成功闪绿反馈（见 Ui.cs）。
/// </summary>
public class MainForm : Form
{
    // ---- 电源设置项（key 与 PowerManager.Items 对齐）----
    static readonly (string key, string name, string desc)[] ITEMS =
    {
        ("lock", "锁屏后黑屏", "锁屏壁纸出现后，多久自动关闭显示器（黑屏）。设为「永不」= 锁屏后屏幕一直亮着。"),
        ("display", "关闭显示器", "无任何操作闲置多久后关闭屏幕。"),
        ("sleep", "睡眠", "闲置多久后进入睡眠。睡眠唤醒后仍需登录，也会看到锁屏界面。"),
        ("hibernate", "休眠", "闲置多久后进入休眠。需系统已开启休眠功能，否则此项不会生效。")
    };

    static readonly (int sec, string label)[] PRESETS =
    {
        (0, "永不"), (60, "1分"), (180, "3分"), (300, "5分"), (600, "10分"),
        (900, "15分"), (1800, "30分"), (3600, "1小时")
    };

    // 屏幕保护预设：单位 = 分钟（与「等待时间(分钟)」NumericUpDown 的分钟单位一致）。
    // 坑：旧版误用秒（(60,"1分")），点击回调把 Tag 当分钟用 → 点「1分」实际写成 1 小时。
    static readonly (int min, string label)[] SAVER_PRESETS =
    {
        (1, "1分"), (5, "5分"), (10, "10分"), (15, "15分"), (30, "30分"), (60, "1小时")
    };

    // ---- UI 控件 ----
    MenuStrip _menu = new();
    TabControl _tabs = new();
    StatusStrip _statusBar = new();
    ToolStripStatusLabel _stScheme = new();
    ToolStripStatusLabel _stHibernate = new();

    Panel _saverPanel = new();                    // 屏幕保护 tab 容器
    Panel _dynlockPanel = new();                  // 动态锁 tab 容器

    // 电源设置 tab 用 TableLayoutPanel（而不是 Panel）：
    // 原来 Panel + AutoScroll + Dock=Top/AutoSize=true 的组合在 Win11 下 AutoScrollMinSize 计算异常，
    // 内容溢出却不显示滚动条。TableLayoutPanel + AutoSize + AutoScroll 是标准可靠的滚动内容容器。
    TableLayoutPanel _powerPanel = new();

    // 自建 tab 头按钮（覆盖在 TabControl 头上方，因为 Win11 系统主题下 TabControl 头无法显示文字）
    Panel _tabBar = new();
    RoundedButton _tabPower = null!, _tabSaver = null!, _tabDynlock = null!;

    // 电源项当前值标签：key:mode -> Label
    readonly Dictionary<string, Label> _itemValueLabels = new();

    // 屏幕保护
    CheckBox _saverOn = new();
    NumericUpDown _saverTimeout = new();

    // 动态锁
    CheckBox _dynlockOn = new();
    Label _dynlockDevice = new();
    Label _dynlockKey = new();
    Label _dynlockWarn = new();   // 配对 / 勾选 / 密钥异常提示

    bool _suppress;   // 刷新回填控件值时抑制事件，避免触发多余的写入

    // 最近一次读取的系统状态（供回填 / 重建电源项）
    bool _hasBattery;
    bool _hibernateAvailable;
    Dictionary<string, (int? ac, int? dc)> _powerMap = new();
    DynlockManager.DynlockState? _dynlock;

    // ---- 操作成功"闪绿"反馈：值标签短暂变绿后回到主色 ----
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 900 };
    Label? _flashLabel;

    public MainForm()
    {
        Text = "锁屏设置";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(880, 850);
        // 高度 850：v3.1 卡片式布局（标题区 46px + 卡间距 14px）比旧 GroupBox 略高，
        // 850 让 4 张电源卡片 + 底部警告在默认尺寸下完整放下、无需滚动；
        // 更小的屏幕仍有 MinimumSize 760×560 + 自动滚动兜底。
        MinimumSize = new Size(760, 560);
        Font = Ui.FontBody;
        BackColor = Ui.WindowBg;
        DoubleBuffered = true;   // 减少整窗重绘闪烁

        // 图标源统一 = assets\app.ico（csproj 嵌入到 exe Win32 资源）。
        // 显式从 exe 嵌入图标读取，确保窗口左上角 / Alt-Tab / 任务栏 / 资源管理器
        // / 桌面 .lnk / 开始菜单 .lnk 全都显示同一张图标，不依赖 fallback。
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ico != null) Icon = (Icon)ico.Clone();
        }
        catch { /* fallback 到默认图标 */ }

        // 操作成功反馈计时器：到点把闪绿的值标签恢复主色
        _flashTimer.Tick += (s, e) =>
        {
            _flashTimer.Stop();
            if (_flashLabel != null) _flashLabel.ForeColor = Ui.Primary;
            _flashLabel = null;
        };

        BuildMenu();
        BuildTabs();
        BuildStatusBar();

        // MenuStrip 最后入栈（Z-order 最高），Dock=Top 反向 docking 让它抢到 y=0-24，
        // _tabBar 自然落到其下，TabControl 内容再往下。自绘卡片标题画在自己的内边距内，
        // 不存在旧 GroupBox 标题向上溢出 14px 被切的问题。
        Controls.Add(_menu);
    }

    // ================= 菜单 =================
    void BuildMenu()
    {
        _menu.Renderer = new ModernRenderer();
        _menu.BackColor = Color.White;
        _menu.ForeColor = Ui.BodyText;
        _menu.Font = Ui.FontBody;
        _menu.Padding = new Padding(8, 2, 0, 2);

        var file = new ToolStripMenuItem("文件(&F)");
        var miExport = new ToolStripMenuItem("导出配置(&E)");
        var miImport = new ToolStripMenuItem("导入配置(&I)");
        var miQuit = new ToolStripMenuItem("退出(&X)");
        miExport.Click += async (s, e) => await ExportConfigAsync();
        miImport.Click += async (s, e) => await ImportConfigAsync();
        miQuit.Click += (s, e) => Close();
        file.DropDownItems.Add(miExport);
        file.DropDownItems.Add(miImport);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(miQuit);

        var op = new ToolStripMenuItem("操作(&A)");
        var miNever = new ToolStripMenuItem("全部设为永不(&N)");
        var miDefaults = new ToolStripMenuItem("恢复系统默认(&D)");
        var miRefresh = new ToolStripMenuItem("刷新(&R)");
        miNever.Click += async (s, e) => await SetAllNeverAsync();
        miDefaults.Click += async (s, e) => await RestoreDefaultsAsync();
        miRefresh.Click += async (s, e) => await RefreshAllAsync();
        op.DropDownItems.Add(miNever);
        op.DropDownItems.Add(miDefaults);
        op.DropDownItems.Add(new ToolStripSeparator());
        op.DropDownItems.Add(miRefresh);

        var help = new ToolStripMenuItem("帮助(&H)");
        var miAbout = new ToolStripMenuItem("关于(&A)");
        miAbout.Click += (s, e) => MessageBox.Show(
            "锁屏设置（v3.1）\n\n" +
            "单文件 C# 程序，直接调用系统 powercfg 与注册表，\n" +
            "无后台进程、无端口、无需浏览器。\n\n" +
            "导出 / 导入配置不需要管理员权限，适合换电脑或重装系统后恢复。",
            "锁屏设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        help.DropDownItems.Add(miAbout);

        _menu.Items.Add(file);
        _menu.Items.Add(op);
        _menu.Items.Add(help);
        MainMenuStrip = _menu;
        // 注：_menu 不在这里 Controls.Add()，构造函数末尾追加，让 MenuStrip 在 Z-order 中最后入栈
        // → Dock=Top 反向 docking 规则下，MenuStrip 才能抢到 y=0 顶槽
    }

    // ================= 标签页 =================
    void BuildTabs()
    {
        // 自建 tab 头：Panel + 3 个 Ghost 圆角按钮（胶囊选中态），覆盖在 TabControl 头上方。
        // 原因：Win11 系统主题下 TabControl 任何 Appearance 的 tab 头都无法显示文字（实测 Normal/FlatButtons 均空白）。
        _tabBar.Dock = DockStyle.Top;
        _tabBar.Height = 48;
        _tabBar.BackColor = Ui.WindowBg;
        var barFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 8, 12, 0),
            BackColor = Ui.WindowBg
        };
        _tabBar.Controls.Add(barFlow);

        _tabPower = MakeTabBtn("电源设置", 0);
        _tabSaver = MakeTabBtn("屏幕保护", 1);
        _tabDynlock = MakeTabBtn("动态锁", 2);
        barFlow.Controls.AddRange(new Control[] { _tabPower, _tabSaver, _tabDynlock });

        // TabControl 自身头压成 1px（被上方 _tabBar 完全覆盖）
        _tabs.Dock = DockStyle.Fill;
        _tabs.Appearance = TabAppearance.FlatButtons;
        _tabs.Padding = new Point(0, 0);
        _tabs.ItemSize = new Size(0, 1);

        _tabs.TabPages.Add(BuildPowerTab());
        _tabs.TabPages.Add(BuildSaverTab());
        _tabs.TabPages.Add(BuildDynlockTab());

        _tabs.SelectedIndexChanged += (s, e) => UpdateTabBtns();

        // ItemSize=(0,1) 让 TabControl 头几乎不可见，但 Win11 系统主题仍会在 y=0 那一像素
        // 画一道 baseline 灰线（自建 _tabBar 与 TabControl 内容区之间露出"小灰条"）。
        // 用 Region 把 TabControl 顶部 1 像素切割掉，让那一像素根本不绘制 → 灰条消失。
        // HandleCreated：TabControl 创建窗口句柄后切一次（首次显示前生效，不闪）。
        // SizeChanged：用户拉窗口改变尺寸后重新切割（Region 不会随尺寸自动重设）。
        _tabs.HandleCreated += (s, e) => CutTabHeader();
        _tabs.SizeChanged += (s, e) => CutTabHeader();

        // Dock 顺序：TabControl (Fill) 加在先，_tabBar (Top) 后加也抢顶部 slot
        Controls.Add(_tabs);
        Controls.Add(_tabBar);
    }

    // 上一个 Region 缓存：Control.Region setter 会克隆传入的 Region，
    // 我们自己持有的这份要在替换时显式 Dispose，避免每次拉窗口尺寸都泄漏一个 GDI Region。
    Region? _cutRegion;

    void CutTabHeader()
    {
        if (!_tabs.IsHandleCreated) return;
        int w = _tabs.ClientSize.Width, h = _tabs.ClientSize.Height;
        if (w <= 0 || h <= 1) return;   // 高度必须 > 1，否则整区域都被切掉
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddRectangle(new Rectangle(0, 1, w, h - 1));
        _cutRegion?.Dispose();
        _cutRegion = new Region(path);
        _tabs.Region = _cutRegion;
    }

    RoundedButton MakeTabBtn(string text, int idx)
    {
        var b = new RoundedButton
        {
            Text = text,
            Kind = RoundedButton.Kinds.Ghost,
            Radius = 8F,
            Size = new Size(116, 32),
            Font = Ui.FontBody,
            Margin = new Padding(0, 0, 6, 0)
        };
        int i = idx;
        b.Click += (s, e) => _tabs.SelectedIndex = i;
        return b;
    }

    void UpdateTabBtns()
    {
        var btns = new[] { _tabPower, _tabSaver, _tabDynlock };
        for (int i = 0; i < 3; i++)
        {
            bool sel = _tabs.SelectedIndex == i;
            btns[i].Selected = sel;
            btns[i].Font = sel ? Ui.FontBold : Ui.FontBody;
            btns[i].Invalidate();
        }
    }

    TabPage BuildPowerTab()
    {
        var tab = new TabPage("电源设置") { BackColor = Ui.WindowBg };
        _powerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoScroll = true,
            ColumnCount = 1,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 14, 16, 12),
            BackColor = Ui.WindowBg
        };
        // 单列 Percent 100% 让列宽等于 TLP 宽度（受 Dock=Fill 约束为父容器宽度），
        // 打破卡片(FlowLayoutPanel) 嵌套 AutoSize 的循环依赖。
        _powerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tab.Controls.Add(_powerPanel);
        return tab;
    }

    TabPage BuildSaverTab()
    {
        var tab = new TabPage("屏幕保护") { BackColor = Ui.WindowBg };
        _saverPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 14, 16, 12), BackColor = Ui.WindowBg };

        var card = NewCard("屏幕保护程序");
        var flow = NewCardFlow();

        var desc = new Label { Text = "屏保恢复时若勾选「在恢复时显示登录屏幕」也会导致锁屏。", AutoSize = true, ForeColor = Ui.SubText };
        flow.Controls.Add(desc);

        _saverOn = new CheckBox { Text = "启用屏幕保护", AutoSize = true, ForeColor = Ui.BodyText, Margin = new Padding(0, 6, 0, 6) };
        _saverOn.CheckedChanged += async (s, e) => { if (_suppress) return; await SetSaverAsync(_saverOn.Checked, (int)_saverTimeout.Value); };
        flow.Controls.Add(_saverOn);

        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, BackColor = Ui.CardBg, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.Add(new Label { Text = "等待时间(分钟)：", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.BodyText, Margin = new Padding(0, 8, 6, 0) });
        _saverTimeout = NewNumeric(80);
        _saverTimeout.ValueChanged += async (s, e) => { if (_suppress) return; if (_saverOn.Checked) await SetSaverAsync(true, (int)_saverTimeout.Value); };
        row.Controls.Add(_saverTimeout);
        foreach (var p in SAVER_PRESETS)
        {
            var b = NewBtn(p.label, RoundedButton.Kinds.Neutral);
            b.Tag = p.min;
            b.Click += async (s, e) => { _saverTimeout.Value = (int)((RoundedButton)s!).Tag!; await SetSaverAsync(true, (int)((RoundedButton)s!).Tag!); };
            row.Controls.Add(b);
        }
        flow.Controls.Add(row);

        var note = new Label { Text = "屏保开关写在当前用户注册表，改完已通知系统刷新；个别情况需注销后完全生效。", AutoSize = true, ForeColor = Ui.SubText };
        flow.Controls.Add(note);

        card.Controls.Add(flow);
        _saverPanel.Controls.Add(card);
        tab.Controls.Add(_saverPanel);
        return tab;
    }

    TabPage BuildDynlockTab()
    {
        var tab = new TabPage("动态锁") { BackColor = Ui.WindowBg };
        _dynlockPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 14, 16, 12), BackColor = Ui.WindowBg };

        var card = NewCard("动态锁（离开自动锁屏）");
        var flow = NewCardFlow();

        var desc = new Label { Text = "利用已配对的手机蓝牙信号：你带着手机离开电脑后自动锁屏。", AutoSize = true, ForeColor = Ui.SubText };
        flow.Controls.Add(desc);

        _dynlockOn = new CheckBox { Text = "启用动态锁", AutoSize = true, ForeColor = Ui.BodyText, Margin = new Padding(0, 6, 0, 6) };
        _dynlockOn.CheckedChanged += async (s, e) => { if (_suppress) return; await SetDynlockAsync(_dynlockOn.Checked); };
        flow.Controls.Add(_dynlockOn);

        _dynlockDevice = new Label { Text = "信任设备：读取中…", AutoSize = true, ForeColor = Ui.BodyText };
        flow.Controls.Add(_dynlockDevice);

        _dynlockKey = new Label { Text = "蓝牙密钥权限：读取中…", AutoSize = true, ForeColor = Ui.BodyText };
        flow.Controls.Add(_dynlockKey);

        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, BackColor = Ui.CardBg, Margin = new Padding(0, 4, 0, 4) };
        var bBt = NewBtn("打开蓝牙设置", RoundedButton.Kinds.Neutral);
        bBt.Click += (s, e) => DynlockManager.OpenSettings("bluetooth");
        var bDl = NewBtn("打开动态锁设置", RoundedButton.Kinds.Neutral);
        bDl.Click += (s, e) => DynlockManager.OpenSettings("dynamiclock");
        btns.Controls.Add(bBt);
        btns.Controls.Add(bDl);
        flow.Controls.Add(btns);

        _dynlockWarn = new Label
        {
            AutoSize = true, ForeColor = Ui.Warning,
            Visible = false
        };
        flow.Controls.Add(_dynlockWarn);

        card.Controls.Add(flow);
        _dynlockPanel.Controls.Add(card);
        tab.Controls.Add(_dynlockPanel);
        return tab;
    }

    void BuildStatusBar()
    {
        _statusBar.Renderer = new ModernRenderer();
        _statusBar.BackColor = Color.White;
        _statusBar.ForeColor = Ui.SubText;
        _statusBar.Font = Ui.FontStatus;
        _statusBar.SizingGrip = true;

        _stScheme = new ToolStripStatusLabel { Text = "电源计划：读取中…", Spring = false, ForeColor = Ui.BodyText };
        _stHibernate = new ToolStripStatusLabel { Text = "休眠", ForeColor = Ui.SubText };
        var stVer = new ToolStripStatusLabel { Text = "v3.1", ForeColor = Ui.SubText };
        _statusBar.Items.Add(_stScheme);
        _statusBar.Items.Add(new ToolStripStatusLabel("    "));
        _statusBar.Items.Add(_stHibernate);
        _statusBar.Items.Add(new ToolStripStatusLabel("    "));
        _statusBar.Items.Add(stVer);
        Controls.Add(_statusBar);
    }

    // ================= 控件工厂 =================
    static CardPanel NewCard(string title) => new() { CardTitle = title, Dock = DockStyle.Top };

    // 卡片内竖排布局容器：白底（子 Label 环境继承白底），Fill+GrowAndShrink 打破循环依赖
    static FlowLayoutPanel NewCardFlow() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        BackColor = Ui.CardBg
    };

    static RoundedButton NewBtn(string text, RoundedButton.Kinds kind) =>
        new() { Text = text, Kind = kind, AutoSize = true };

    static NumericUpDown NewNumeric(int width) => new()
    {
        Minimum = 0, Maximum = 1440, Width = width,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 4, 8, 0)
    };

    // ================= 初始化 =================
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 窗口淡入：~80ms 轻量过渡，不拖节奏
        Opacity = 0;
        var fade = new System.Windows.Forms.Timer { Interval = 10 };
        fade.Tick += (s, ea) =>
        {
            Opacity += 0.15;
            if (Opacity >= 1) { Opacity = 1; fade.Stop(); fade.Dispose(); }
        };
        fade.Start();

        UseWaitCursor = true;
        try
        {
            await RefreshAllAsync();
            BuildPowerItems();
            UpdateTabBtns();
        }
        catch (Exception ex)
        {
            MessageBox.Show("初始化失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _flashTimer.Dispose();
    }

    // ================= 刷新 =================
    async Task RefreshAllAsync()
    {
        UseWaitCursor = true;
        try
        {
            var r = await Task.Run(() => new
            {
                scheme = PowerManager.ActiveScheme(),
                map = PowerManager.QueryAll(),
                saver = SaverManager.Get(),
                dynlock = DynlockManager.Get(),
                hasBattery = PowerManager.HasBattery(),
                hib = PowerManager.HibernateAvailable()
            });
            _hasBattery = r.hasBattery;
            _hibernateAvailable = r.hib;
            _powerMap = r.map;
            _dynlock = r.dynlock;

            _stScheme.Text = "电源计划：" + r.scheme.name;
            _stHibernate.Text = r.hib ? "休眠已开启" : "休眠未开启";
            _stHibernate.ForeColor = r.hib ? Ui.SubText : Ui.Warning;
            _stHibernate.ToolTipText = r.hib ? "" : "本机未开启休眠功能（未找到 hiberfil.sys），休眠设置不会生效";

            RefreshSaver(r.saver);
            RefreshDynlock(r.dynlock);

            // 电源 tab 的"当前："标签也随刷新更新（用户点菜单「刷新」时若系统设置被外部改过，
            // 这里让显示与实际一致；OnShown 首帧 _itemValueLabels 为空，由 BuildPowerItems 回填）。
            // 刷新走 SetLabel 静默更新，不触发闪绿（闪绿只留给用户主动操作）。
            foreach (var kv in _itemValueLabels)
            {
                int idx = kv.Key.IndexOf(':');
                if (idx <= 0) continue;
                string setting = GetSetting(kv.Key.Substring(0, idx));
                string mode = kv.Key.Substring(idx + 1);
                if (setting.Length == 0 || !_powerMap.TryGetValue(setting, out var st)) continue;
                int sec = mode == "ac" ? (st.ac ?? 0) : (st.dc ?? 0);
                kv.Value.Text = "当前：" + PowerManager.FormatSeconds(sec);
            }
        }
        finally { UseWaitCursor = false; }
    }

    void RefreshSaver(SaverManager.SaverState saver)
    {
        _suppress = true;
        try
        {
            _saverOn.Checked = saver.Active;
            // clamp：系统里可能存着超过 NumericUpDown 上限（720 分钟）的超时值，
            // 直接赋值会抛 ArgumentOutOfRangeException 导致初始化失败
            int minutes = saver.Timeout / 60;
            if (minutes < _saverTimeout.Minimum) minutes = (int)_saverTimeout.Minimum;
            if (minutes > _saverTimeout.Maximum) minutes = (int)_saverTimeout.Maximum;
            _saverTimeout.Value = minutes;
        }
        finally { _suppress = false; }
    }

    void RefreshDynlock(DynlockManager.DynlockState dl)
    {
        _suppress = true;
        _dynlockOn.Checked = dl.Enabled;
        _suppress = false;

        var name = dl.SelectedName ?? "";
        var mac = dl.SelectedMac ?? "";
        _dynlockDevice.Text = "信任设备：" + (dl.DeviceSelected
            ? (name.Length > 0 && name != mac ? name + " (" + mac + ")" : mac)
            : "未选择");

        _dynlockKey.Text = "蓝牙密钥权限：" + (dl.KeysHealthy == "healthy" ? "正常" : dl.KeysHealthy == "broken" ? "异常" : "无法检测");
        _dynlockKey.ForeColor = dl.KeysHealthy == "healthy" ? Ui.BodyText : Ui.Warning;

        var warns = new List<string>();
        if (dl.PairedCount == 0)
            warns.Add("⚠ 未检测到已配对的手机。请点「打开蓝牙设置」先配对手机，再回来勾选信任设备。");
        else if (!dl.DeviceSelected)
            warns.Add("⚠ 手机已配对，但还没勾选信任设备。请点「打开动态锁设置」勾选你的手机。");
        if (dl.KeysHealthy == "broken")
            warns.Add("⚠ 蓝牙密钥权限异常（可能被某些清理软件删掉），会导致动态锁失效。");
        if (dl.LockdownOnLeave)
            warns.Add("⚠ 检测到 Win11「离开时锁定」独立开关仍开启：它与动态锁互相独立，只关动态锁它仍会在离开时锁屏。把动态锁关一次（本工具会一并关掉它），或到 系统设置 > 帐户 > 登录选项 检查。");
        _dynlockWarn.Text = string.Join("\n\n", warns);
        _dynlockWarn.Visible = warns.Count > 0;
    }

    // ================= 电源设置 UI =================
    void BuildPowerItems()
    {
        _powerPanel.SuspendLayout();
        _powerPanel.Controls.Clear();
        _powerPanel.RowStyles.Clear();
        _itemValueLabels.Clear();
        foreach (var it in ITEMS)
        {
            // AutoSize 行高 = 控件 PreferredSize；Percent 100% 列宽 = TLP 宽度
            _powerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _powerPanel.Controls.Add(BuildItemCard(it.key, it.name, it.desc));
        }
        _powerPanel.ResumeLayout();
    }

    CardPanel BuildItemCard(string key, string name, string desc)
    {
        // 标题由 CardPanel 自绘在顶部 46px 内边距里，不会压住下面的描述文字
        //（旧 GroupBox 标题上溢裁切的坑在本控件中不存在）
        var card = NewCard(name);
        var flow = NewCardFlow();

        var d = new Label { Text = desc, AutoSize = true, ForeColor = Ui.SubText };
        flow.Controls.Add(d);

        flow.Controls.Add(BuildModePanel(key, "ac", "接通电源", _hasBattery));
        if (_hasBattery) flow.Controls.Add(BuildModePanel(key, "dc", "使用电池", false));

        if (key == "hibernate" && !_hibernateAvailable)
        {
            var warn = new Label
            {
                Text = "⚠ 本机未开启休眠功能（未找到 hiberfil.sys），此项设置不会生效。如需使用，请以管理员身份运行 powercfg /h on 开启休眠。",
                AutoSize = true, ForeColor = Ui.Warning
            };
            flow.Controls.Add(warn);
        }

        card.Controls.Add(flow);
        return card;
    }

    FlowLayoutPanel BuildModePanel(string key, string mode, string label, bool withSync)
    {
        // GrowAndShrink + MaximumSize.Width 约束：mode panel 最多 800px 宽，
        // 内容（≈750px）单行可放；flow 实际更窄时 mode panel 被裁剪，WrapContents=true 自动换行。
        var wrap = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(800, 0), Margin = new Padding(0, 6, 0, 0), BackColor = Ui.CardBg };

        wrap.Controls.Add(new Label { Text = label + "：", AutoSize = true, Font = Ui.FontBold, ForeColor = Ui.BodyText, Margin = new Padding(0, 8, 6, 0) });

        foreach (var p in PRESETS)
        {
            var b = NewBtn(p.label, RoundedButton.Kinds.Neutral);
            b.Tag = (key, mode, p.sec);
            b.Click += PresetButton_Click;
            wrap.Controls.Add(b);
        }

        var num = NewNumeric(72);
        var apply = NewBtn("应用", RoundedButton.Kinds.Primary);
        apply.Tag = (key, mode, num);
        apply.Click += CustomButton_Click;
        wrap.Controls.Add(num);
        wrap.Controls.Add(apply);

        if (withSync)
        {
            var sync = NewBtn("⇄ 同步两项", RoundedButton.Kinds.Neutral);
            sync.Tag = (key, mode, num);
            sync.Click += SyncButton_Click;
            wrap.Controls.Add(sync);
        }

        var val = new Label { Text = "当前：—", AutoSize = true, ForeColor = Ui.Primary, Font = Ui.FontBold, Margin = new Padding(4, 8, 0, 0) };
        wrap.Controls.Add(val);
        _itemValueLabels[key + ":" + mode] = val;

        // 从已加载的状态回填当前值
        if (_powerMap.TryGetValue(GetSetting(key), out var st))
        {
            int sec = mode == "ac" ? (st.ac ?? 0) : (st.dc ?? 0);
            val.Text = "当前：" + PowerManager.FormatSeconds(sec);
        }
        return wrap;
    }

    static string GetSetting(string key) => key switch
    {
        "lock" => "VIDEOCONLOCK",
        "display" => "VIDEOIDLE",
        "sleep" => "STANDBYIDLE",
        "hibernate" => "HIBERNATEIDLE",
        _ => ""
    };

    async void PresetButton_Click(object? sender, EventArgs e)
    {
        if (sender is RoundedButton b && b.Tag is (string key, string mode, int sec))
            await SetItemAsync(key, mode, sec);
    }

    async void CustomButton_Click(object? sender, EventArgs e)
    {
        if (sender is RoundedButton b && b.Tag is (string key, string mode, NumericUpDown num))
            await SetItemAsync(key, mode, (int)num.Value * 60);
    }

    async void SyncButton_Click(object? sender, EventArgs e)
    {
        if (sender is RoundedButton b && b.Tag is (string key, string mode, NumericUpDown num))
            await SetItemAsync(key, "both", (int)num.Value * 60);
    }

    async Task SetItemAsync(string key, string mode, int sec)
    {
        var (ok, errors) = await Task.Run(() => PowerManager.SetItem(key, sec, mode));
        if (!ok) { ShowError("设置失败：" + string.Join("；", errors)); return; }
        // 设置值就是已知结果，直接按 mode 回填标签（省一次全量 powercfg /q）；
        // 闪绿 ~0.9s 提示"已生效"，随后恢复主色
        if (mode is "both" or "ac") FlashItemLabel(key, "ac", sec);
        if (mode is "both" or "dc") FlashItemLabel(key, "dc", sec);
    }

    void FlashItemLabel(string key, string mode, int sec)
    {
        if (!_itemValueLabels.TryGetValue(key + ":" + mode, out var lbl)) return;
        lbl.Text = "当前：" + PowerManager.FormatSeconds(sec);
        lbl.ForeColor = Ui.Success;
        _flashLabel = lbl;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // ================= 屏幕保护 / 动态锁 =================
    async Task SetSaverAsync(bool on, int minutes)
    {
        var (ok, errors) = await Task.Run(() => SaverManager.Set(on, minutes * 60, null));
        if (!ok) ShowError("屏幕保护设置失败：" + string.Join("；", errors));
    }

    async Task SetDynlockAsync(bool on)
    {
        var r = await Task.Run(() => DynlockManager.Set(on));
        if (!r.ok) ShowError("动态锁设置失败：" + (r.error ?? "未知错误"));
        var dl = await Task.Run(() => DynlockManager.Get());
        _dynlock = dl;
        RefreshDynlock(dl);
        // Set 产生的提示（自动补选信任设备 / 未检测到配对手机）追加到警告区展示
        if (r.ok && r.warning != null)
        {
            _dynlockWarn.Text = (_dynlockWarn.Visible ? _dynlockWarn.Text + "\n\n" : "") + "ℹ " + r.warning;
            _dynlockWarn.Visible = true;
        }
    }

    // ================= 菜单动作 =================
    async Task ExportConfigAsync()
    {
        using var dlg = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "锁屏设置.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var cfg = await Task.Run(() => ConfigIO.Build());
            File.WriteAllText(dlg.FileName, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            MessageBox.Show("配置已导出到：\n" + dlg.FileName, "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("导出失败：" + ex.Message); }
    }

    async Task ImportConfigAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "JSON 文件|*.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var (_, cfg) = await Task.Run(() => ConfigIO.Read(dlg.FileName));
            var (ok, _, errors) = await Task.Run(() => ConfigIO.Apply(cfg));
            if (!ok) ShowError("导入部分失败：" + string.Join("；", errors));
            await RefreshAllAsync();
            BuildPowerItems();
            // 部分失败时不弹"成功"，避免给用户错误预期
            if (ok) MessageBox.Show("配置已导入并应用。", "导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("导入失败：" + ex.Message); }
    }

    async Task SetAllNeverAsync()
    {
        foreach (var it in ITEMS)
            await SetItemAsync(it.key, "both", 0);
        var (ok, errors) = await Task.Run(() => SaverManager.Set(false, null, null));
        if (!ok) ShowError("屏幕保护设置失败：" + string.Join("；", errors));
        await RefreshAllAsync();
        BuildPowerItems();
        MessageBox.Show("已全部设为「永不」。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task RestoreDefaultsAsync()
    {
        var ok = MessageBox.Show("将把电源计划恢复为系统默认值（当前所有自定义时间都会丢失），继续？",
            "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes) return;
        var (rst, err) = await Task.Run(() => PowerManager.RestoreDefaults());
        if (!rst) { ShowError("恢复默认失败：" + (err ?? "未知错误")); return; }
        await RefreshAllAsync();
        BuildPowerItems();
        MessageBox.Show("已恢复系统默认。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ShowError(string msg)
    {
        MessageBox.Show(msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
