using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Snapline.Core;
using Snapline.Interop;
using Line = Snapline.Core.Line;

namespace Snapline.UI;

/// <summary>Everything you can change, on one page. Changes apply as you make them.</summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private static int _activeWindows;
    private bool _active;
    public static bool AnyActive => _activeWindows != 0;
    internal static bool AnyOpen => _open?.IsVisible == true;
    private readonly Action _apply;
    private readonly Line? _line;
    private readonly TextBlock _shortcutStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly List<(string name, TextBox box, Func<string> get, Action<string> set, bool global)> _bindings = new();
    private TextBlock _inbox = null!;
    private TextBox _offset = null!;
    private bool _building;

    public static void Open(Action apply, Line? line = null)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new SettingsWindow(apply, line);
        _open.Closed += (_, _) => { _open = null; Preview.RefreshPinShortcut(); };
        _open.Show();
        Preview.RefreshPinShortcut();
    }

    /// <summary>After a language change the page is rebuilt in place.</summary>
    public static void Rebuild()
    {
        if (_open is null) return;
        var apply = _open._apply;
        var line = _open._line;
        var left = _open.Left; var top = _open.Top;
        _open.Close();
        Open(apply, line);
        if (_open is not null) { _open.Left = left; _open.Top = top; }
    }

    /// <summary>The settings page as a plain element, for the film.</summary>
    public static FrameworkElement ForDemo(Action apply)
    {
        var w = new SettingsWindow(apply);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        if (content is ScrollViewer sv) { sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; sv.MaxHeight = double.PositiveInfinity; }
        var border = new Border { Width = 500, Background = w.Background, Child = content };
        System.Windows.Documents.TextElement.SetForeground(border, w.Foreground);
        return border;
    }

    private SettingsWindow(Action apply, Line? line = null)
    {
        _apply = apply;
        _line = line;
        Title = $"{Strings.AppName} · {Strings.SettingsTitle}";
        Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Icon = Brand.Icon;
        FlowDirection = Strings.Flow;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Paint();
        Theme.Changed += Paint;
        Closed += (_, _) => Theme.Changed -= Paint;
        Content = Build();
        Activated += (_, _) => { SetActive(true); Preview.ClearPinShortcut(); PictureWindow.ClearShortcutTarget(); };
        Deactivated += (_, _) => SetActive(false);
        Closed += (_, _) => SetActive(false);
    }

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active; _activeWindows += active ? 1 : -1;
    }

    private void Paint()
    {
        Background = (Brush)Application.Current.Resources["MenuBg"];
        Foreground = (Brush)Application.Current.Resources["MenuFg"];
    }

    private UIElement Build()
    {
        _building = true;
        var s = Settings.Current;
        var stack = new StackPanel { Margin = new Thickness(24, 16, 24, 18) };
        var header = Brand.Lockup(26, Theme.AppsDark);
        header.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(header);

        // The line
        stack.Children.Add(Heading(Strings.SectionLine));
        stack.Children.Add(new TextBlock { Text = "晾衣绳持续显示，通过快捷键手动隐藏。滚轮可翻页查看所有图片。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        BuildShortcutSection(stack);

        _offset = new TextBox { Text = ((int)s.LineOffset).ToString(), Width = 70, Padding = new Thickness(8, 4, 8, 4), ToolTip = Strings.LineDistanceTip, HorizontalAlignment = HorizontalAlignment.Left };
        _offset.LostFocus += (_, _) => { if (double.TryParse(_offset.Text, out var v)) { s.LineOffset = Math.Max(0, v); Changed(); } };
        _offset.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Keyboard.ClearFocus(); if (double.TryParse(_offset.Text, out var v)) { s.LineOffset = Math.Max(0, v); Changed(); } } };
        stack.Children.Add(Row(Strings.LineDistance, _offset, Strings.LineDistanceTip));

        // Look
        stack.Children.Add(Heading(Strings.SectionLook));
        var rope = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in Pegs.RopeColors) rope.Items.Add(ColorItem(key));
        rope.SelectedIndex = Math.Max(0, Array.IndexOf(Pegs.RopeColors, s.RopeColor));
        rope.SelectionChanged += (_, _) => { if (rope.SelectedIndex >= 0) { s.RopeColor = Pegs.RopeColors[rope.SelectedIndex]; Changed(); } };
        stack.Children.Add(Row(Strings.RopeColor, rope));

        var pegs = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in Pegs.Styles) pegs.Items.Add(PegItem(key));
        pegs.SelectedIndex = Math.Max(0, Array.IndexOf(Pegs.Styles, s.PegStyle));
        pegs.SelectionChanged += (_, _) => { if (pegs.SelectedIndex >= 0) { s.PegStyle = Pegs.Styles[pegs.SelectedIndex]; Changed(); } };
        stack.Children.Add(Row(Strings.PegStyle, pegs));

        stack.Children.Add(Check(Strings.Bows, null, s.Bows, v => s.Bows = v));
        stack.Children.Add(Check(Strings.AutoArrange, Strings.AutoArrangeTip, s.AutoArrange, v => s.AutoArrange = v));

        var appearance = Choice(new[] { ("auto", Strings.Auto), ("light", Strings.Light), ("dark", Strings.Dark) }, s.Appearance, v => s.Appearance = v);
        stack.Children.Add(Row(Strings.Appearance, appearance));

        var language = Choice(new[] { ("zh", "简体中文"), ("en", "English"), ("ar", "العربية") }, s.Language, v => { s.Language = v; Dispatcher.BeginInvoke(Rebuild); });
        stack.Children.Add(Row(Strings.LanguageLabel, language));

        // Captures
        stack.Children.Add(Heading(Strings.SectionCaptures));
        stack.Children.Add(new TextBlock { Text = "框选后可以移动选区和标注，Enter 确认复制。截图时按上杆快捷键可直接钉到桌面并上杆；平时用同一快捷键加入已复制的外部图片。单击桌面贴图打开编辑条。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 6) });

        var inboxRow = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
        var change = new Button { Content = Strings.Change, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(change, Dock.Right);
        change.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.OpenInboxFolder, InitialDirectory = Inbox.Folder };
            if (dialog.ShowDialog(this) != true) return;
            if (_line is null) return;
            try
            {
                Library.ChangeFolder(dialog.FolderName, _line);
                Changed(); _inbox.Text = Inbox.Folder;
            }
            catch (Exception ex) { MessageBox.Show(this, $"更改保存路径失败：{ex.Message}\n原有图片会保留。", Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        inboxRow.Children.Add(change);
        _inbox = new TextBlock { Text = Inbox.Folder, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = Strings.IsRtl ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var inboxLabel = new StackPanel();
        inboxLabel.Children.Add(new TextBlock { Text = Strings.CaughtCapturesFolder });
        inboxLabel.Children.Add(_inbox);
        inboxRow.Children.Add(inboxLabel);
        stack.Children.Add(inboxRow);

        // General
        stack.Children.Add(Heading(Strings.SectionGeneral));
        stack.Children.Add(Check(Strings.Sounds, null, s.SoundOn, v => { s.SoundOn = v; Sounds.Enabled = v; }));
        stack.Children.Add(Check(Strings.StartWithWindows, null, Shell.StartsWithWindows, v =>
        {
            try { Shell.StartsWithWindows = v; } catch (Exception e) { Log.Error($"Could not change the startup setting: {e.Message}"); }
        }));

        stack.Children.Add(new TextBlock
        {
            Text = string.Format(Strings.SettingsFooter, System.IO.Path.Combine(Settings.Folder, "settings.json")),
            Margin = new Thickness(0, 16, 0, 0), Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        _building = false;
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80 };
    }

    private TextBlock Heading(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 6),
    };

    private static DockPanel Row(string label, UIElement control, string? tip = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 2), LastChildFill = false };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 230, TextWrapping = TextWrapping.Wrap, ToolTip = tip };
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    private CheckBox Check(string label, string? tip, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 4), ToolTip = tip, Foreground = Foreground };
        box.Checked += (_, _) => { set(true); Changed(); };
        box.Unchecked += (_, _) => { set(false); Changed(); };
        return box;
    }

    private ComboBox Choice((string key, string label)[] options, string current, Action<string> set)
    {
        var box = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (_, label) in options) box.Items.Add(label);
        int index = Array.FindIndex(options, o => o.key == current);
        box.SelectedIndex = index < 0 ? 0 : index;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(options[box.SelectedIndex].key); Changed(); } };
        return box;
    }

    private static StackPanel ColorItem(string key)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Pegs.RopeColorOf(key)), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 0.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Strings.ColorName(key), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static StackPanel PegItem(string key)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var peg = Pegs.Create(key, seed: 3);
        peg.Margin = new Thickness(0, 0, 8, 0);
        var box = new Grid { Width = 14, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        peg.Margin = new Thickness(1, -2, 0, 0);
        peg.LayoutTransform = new ScaleTransform(0.85, 0.85);
        box.Children.Add(peg);
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = Strings.PegName(key), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void BuildShortcutSection(StackPanel stack)
    {
        var s = Settings.Current;
        stack.Children.Add(Heading("自定义快捷键"));
        stack.Children.Add(new TextBlock { Text = "点击输入框后直接按键，可使用单个字母、数字、功能键或组合键。复制图片上杆与钉图可共用同一键，复制的外部图片优先上杆。旋转、镜像仅在鼠标悬停桌面贴图时生效。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        Add("区域截图", () => s.CaptureHotKey, v => s.CaptureHotKey = v, true);
        Add("显示／隐藏晾衣绳", () => s.HotKey, v => s.HotKey = v, true);
        Add("复制图片上晾衣绳", () => s.ClipboardHangKey, v => s.ClipboardHangKey = v, true);
        Add("顺时针旋转 90°", () => s.RotateClockwiseKey, v => s.RotateClockwiseKey = v, false);
        Add("逆时针旋转 90°", () => s.RotateCounterclockwiseKey, v => s.RotateCounterclockwiseKey = v, false);
        Add("左右镜像", () => s.MirrorHorizontalKey, v => s.MirrorHorizontalKey = v, false);
        Add("上下镜像", () => s.MirrorVerticalKey, v => s.MirrorVerticalKey = v, false);
        Add("截图／悬停预览钉图", () => s.PinPreviewKey, v => s.PinPreviewKey = v, false);
        var reset = new Button { Content = "恢复默认快捷键", Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) =>
        {
            string[] defaults = { "F1", "F2", "F3", "1", "2", "3", "4", "Q" };
            PictureWindow.ClearShortcutTarget();
            Preview.ClearPinShortcut();
            var currentGlobals = _bindings.Where(b => b.global).Select(b => Signature(b.get())).ToHashSet();
            using var probeWindow = new MessageWindow();
            foreach (var text in defaults)
            {
                if (currentGlobals.Contains(Signature(text))) continue;
                using var probe = new HotKey(probeWindow, text, () => { });
                if (!probe.Registered) { Status($"无法恢复：{text} 已被其他软件占用。", true); return; }
            }
            for (int i = 0; i < defaults.Length; i++) { _bindings[i].set(defaults[i]); _bindings[i].box.Text = defaults[i]; }
            Changed(); Status("已恢复默认快捷键，立即生效。", false);
        };
        stack.Children.Add(reset); stack.Children.Add(_shortcutStatus);
        stack.Children.Add(new TextBlock { Text = "贴图操作：滚轮平滑缩放整张图片，Ctrl＋滚轮调整透明度；左键拖动移动，拖动边缘缩放；中键恢复原始大小及透明度。单击立即打开工具条，选中工具后标注；双击关闭贴图并保留图片。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 4) });
        void Add(string name, Func<string> get, Action<string> set, bool global)
        {
            var box = new TextBox { Text = get(), IsReadOnly = true, Width = 190, Padding = new Thickness(8, 5, 8, 5), ToolTip = "点击后按下新的快捷键；Esc 取消" };
            _bindings.Add((name, box, get, set, global));
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape) return;
                var mods = Keyboard.Modifiers;
                var parts = new List<string>();
                if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                int vk = KeyInterop.VirtualKeyFromKey(key);
                parts.Add(vk >= 0x30 && vk <= 0x39 ? ((char)vk).ToString() : key.ToString());
                var text = string.Join("+", parts);
                if (!HotKey.Parse(text, out var candidateMods, out var candidateKey)) { Status("这个按键暂不支持，请换一个。", true); return; }
                bool SharedPin(string other) => (name == "复制图片上晾衣绳" && other == "截图／悬停预览钉图")
                    || (other == "复制图片上晾衣绳" && name == "截图／悬停预览钉图");
                var duplicate = _bindings.FirstOrDefault(b => b.name != name && !SharedPin(b.name) && Signature(b.get()) == Signature(text));
                if (duplicate.name is not null) { Status($"快捷键重复：{text} 已用于“{duplicate.name}”。", true); return; }
                if (Signature(text) != Signature(get()))
                {
                    PictureWindow.ClearShortcutTarget();
                    using var probeWindow = new MessageWindow();
                    using var probe = new HotKey(probeWindow, text, () => { });
                    if (!probe.Registered) { Status($"快捷键 {text} 已被其他软件占用，请换一个。", true); return; }
                }
                set(text); box.Text = text; Changed(); Status($"“{name}”已设为 {text}，立即生效。", false);
            };
            stack.Children.Add(Row(name, box));
        }
    }

    private static string Signature(string text) => HotKey.Parse(text, out var mods, out var vk) ? $"{mods}:{vk}" : text;
    private void Status(string text, bool error) { _shortcutStatus.Text = text; _shortcutStatus.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen; }

    private void Changed()
    {
        if (_building) return;
        Settings.Current.Save();
        _apply();
    }
}

