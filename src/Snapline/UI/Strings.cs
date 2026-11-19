using System.Globalization;
using System.Windows;
using Snapline.Core;

namespace Snapline.UI;

/// <summary>
/// Every word the app shows, in English and Arabic. The language follows
/// Windows unless chosen in Settings.
/// </summary>
public static class Strings
{
    public static event Action? LanguageChanged;

    /// <summary>"en" or "ar".</summary>
    public static string Language { get; private set; } = Resolve();

    public static bool IsRtl => Language == "ar";
    public static FlowDirection Flow => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static void Refresh()
    {
        var was = Language;
        Language = Resolve();
        if (was != Language) LanguageChanged?.Invoke();
    }

    private static string Resolve()
    {
        var chosen = Core.Settings.Current.Language;
        if (chosen is "zh" or "en" or "ar") return chosen;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch { "ar" => "ar", "en" => "en", _ => "zh" };
    }

    private static string L(string en) => Language == "zh" && Zh.TryGetValue(en, out var zh) ? zh : Language == "ar" && Ar.TryGetValue(en, out var ar) ? ar : en;

    public const string AppName = "Snapline";
    public static string Tagline => L("Screenshots, hung out to dry.");

    public static string ShowLine => L("Show line");
    public static string HideLine => L("Hide line");
    public static string TakeEverythingDown => L("Take everything down");
    public static string NewCapture => L("New capture");
    public static string NewCaptureTip => L("Click to take a new screenshot (Win+Shift+S)");
    public static string CatchClipboard => L("Catch clipboard captures");
    public static string CatchClipboardTip => L("Win+Shift+S and PrtScn captures hang even when no file is saved");
    public static string StayDownWhileUnused => L("Stay down until each capture is used");
    public static string StayDownWhileUnusedTip => L("The line waits while a capture has not been copied, dragged or opened");
    public static string TakeDownAfterDrag => L("Take down after dragging into an app");
    public static string TakeDownAfterDragTip => L("A photo dropped into a chat or an app leaves the line, like one saved to a folder");
    public static string RevealAtTopEdge => L("Bring the line down when the pointer rests at the top edge");
    public static string RevealAtTopEdgeTip => L("Hold the pointer still against the top of the screen for half a second. Off, the shortcut and the tray icon show the line.");
    public static string OpenScreenshotsFolder => L("Open Screenshots folder");
    public static string OpenInboxFolder => L("Open caught captures folder");
    public static string Sounds => L("Sounds");
    public static string StartWithWindows => L("Start with Windows");
    public static string Settings => L("Settings…");
    public static string About => L("About Snapline");
    public static string Quit => L("Quit Snapline");

    public static string Copy => L("Copy");
    public static string CopyText => L("Copy text");
    public static string Open => L("Open");
    public static string Edit => L("Edit");
    public static string MarkUp => L("Mark up");
    public static string EditInPaint => L("Edit in Paint");
    public static string Pen => L("Pen");
    public static string Highlighter => L("Highlighter");
    public static string Circle => L("Circle");
    public static string Box => L("Box");
    public static string Arrow => L("Arrow");
    public static string TextTool => L("Text");
    public static string Blur => L("Blur");
    public static string NewNote => L("New note");
    public static string Menu => L("Menu");
    public static string EditNote => L("Edit note");
    public static string NoteColour => L("Note colour");
    public static string NotePlaceholder => L("Write a note…");
    public static string NoteName(string key) => L(key switch { "yellow" => "Yellow", "pink" => "Pink", "blue" => "Blue", "green" => "Green", "orange" => "Orange", _ => key });
    public static string Colour => L("Colour");
    public static string Thickness => L("Thickness");
    public static string Undo => L("Undo");
    public static string Redo => L("Redo");
    public static string Revert => L("Revert to original");
    public static string Done => L("Done");
    public static string Saved => L("Saved");
    public static string Reverted => L("Back to the original");
    public static string CouldNotSave => L("Could not save");
    public static string ShowInExplorer => L("Show in Explorer");
    public static string KeepOnLine => L("Keep on the line");
    public static string SaveToDesktop => L("Save to Desktop");
    public static string SaveToScreenshots => L("Save to Pictures\\Screenshots");
    public static string Discard => L("Discard");
    public static string TakeDown => L("Take down");
    public static string MoveToRecycleBin => L("Move to Recycle Bin");

    public static string Hint => L("Take a screenshot and it will hang here");
    public static string Copied => L("Copied");
    public static string TextCopied => L("Text copied");
    public static string JustNow => L("just now");
    public static string MinutesAgo => L("{0} min ago");
    public static string HoursAgo => L("{0} h ago");

    public static string SettingsTitle => L("Settings");
    public static string SectionLine => L("The line");
    public static string SectionLook => L("Look");
    public static string SectionCaptures => L("Captures");
    public static string SectionGeneral => L("General");
    public static string Shortcut => L("Show or hide the line");
    public static string ShortcutTip => L("Click here and press the keys you want, like Ctrl+Alt+T");
    public static string LineDistance => L("Distance from the top of the screen");
    public static string LineDistanceTip => L("You can also drag the line itself up or down");
    public static string RopeColor => L("Line colour");
    public static string PegStyle => L("Clothespins");
    public static string Bows => L("Bows at the ends of the line");
    public static string AutoArrange => L("Arrange photos evenly along the line");
    public static string AutoArrangeTip => L("Off, each photo stays where you put it");
    public static string Appearance => L("Appearance");
    public static string LanguageLabel => L("Language");
    public static string Auto => L("Follow Windows");
    public static string Light => L("Light");
    public static string Dark => L("Dark");
    public static string CaughtCapturesFolder => L("Caught captures are saved to");
    public static string Change => L("Change…");
    public static string InboxFolderRefused => L("Caught captures need a folder of their own, because discarding one sends it to the Recycle Bin. Pick a folder that is not your Desktop, Pictures, Screenshots or a watched folder, and not a parent of them.");
    public static string WatchFoldersLabel => L("Folders watched for new screenshots");
    public static string AddFolder => L("Add folder…");
    public static string RemoveFolder => L("Remove");
    public static string SettingsFooter => L("Changes apply right away. Everything lives in {0}.");

    public static string WelcomeTitle => L("Snapline is on the line");
    public static string WelcomeBody => L("Take a screenshot and it hangs at the top of the screen. Rest the pointer against the top edge to bring the line down, or press {0}.");

    public static string AboutBody => L(
        "Click a photo to copy it. Press and hold to edit it. Double click to open it.\n" +
        "Drag it along the line to reorder, into an app to send a copy, into a folder to keep it, or to the Recycle Bin to let it go.\n" +
        "Rest the pointer against the top edge of the screen, or press {0}, to bring the line down.");

    // Colour and peg names
    public static string ColorName(string key) => L(key switch
    {
        "bronze" => "Bronze", "gray" => "Gray", "black" => "Black", "white" => "White", "red" => "Red",
        "blue" => "Blue", "green" => "Green", "gold" => "Gold", "pink" => "Pink", "purple" => "Purple", _ => key,
    });

    public static string PegName(string key) => L(key switch
    {
        "wood" => "Wooden", "metal" => "Aluminium", "mixed" => "Coloured plastic, mixed", "red" => "Red plastic",
        "blue" => "Blue plastic", "green" => "Green plastic", "yellow" => "Yellow plastic", _ => key,
    });

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["Screenshots, hung out to dry."] = "把截图挂在晾衣绳上。",
        ["Show line"] = "显示晾衣绳",
        ["Hide line"] = "隐藏晾衣绳",
        ["Take everything down"] = "取下所有图片",
        ["New capture"] = "区域截图",
        ["Click to take a new screenshot (Win+Shift+S)"] = "点击开始区域截图，也可以按截图快捷键",
        ["Catch clipboard captures"] = "接收其他截图软件的剪贴板图片",
        ["Win+Shift+S and PrtScn captures hang even when no file is saved"] = "兼容其他截图软件；截图自动保存并挂到晾衣绳",
        ["Stay down until each capture is used"] = "保持晾衣绳显示",
        ["The line waits while a capture has not been copied, dragged or opened"] = "使用快捷键手动控制显示与隐藏",
        ["Take down after dragging into an app"] = "拖入应用后取下图片",
        ["A photo dropped into a chat or an app leaves the line, like one saved to a folder"] = "拖入应用后从晾衣绳取下图片",
        ["Bring the line down when the pointer rests at the top edge"] = "鼠标移至屏幕顶部时显示",
        ["Hold the pointer still against the top of the screen for half a second. Off, the shortcut and the tray icon show the line."] = "通过快捷键或托盘菜单显示晾衣绳",
        ["Open Screenshots folder"] = "打开系统截图文件夹",
        ["Open caught captures folder"] = "打开图片保存文件夹",
        ["Sounds"] = "提示音",
        ["Start with Windows"] = "开机自动启动",
        ["Settings…"] = "设置…",
        ["About Snapline"] = "关于 Snapline",
        ["Quit Snapline"] = "退出 Snapline",
        ["Copy"] = "复制图片",
        ["Copy text"] = "识别并复制文字",
        ["Open"] = "打开图片",
        ["Edit"] = "编辑",
        ["Mark up"] = "标注图片",
        ["Edit in Paint"] = "使用画图编辑",
        ["Pen"] = "画笔",
        ["Highlighter"] = "荧光笔",
        ["Circle"] = "圆圈",
        ["Box"] = "方框",
        ["Arrow"] = "箭头",
        ["Text"] = "文字",
        ["Blur"] = "模糊处理",
        ["New note"] = "新建便签",
        ["Menu"] = "菜单",
        ["Edit note"] = "编辑便签",
        ["Note colour"] = "便签颜色",
        ["Write a note…"] = "在这里输入便签内容…",
        ["Yellow"] = "黄色",
        ["Pink"] = "粉色",
        ["Blue"] = "蓝色",
        ["Green"] = "绿色",
        ["Orange"] = "橙色",
        ["Colour"] = "颜色",
        ["Thickness"] = "粗细",
        ["Undo"] = "撤销",
        ["Redo"] = "重做",
        ["Revert to original"] = "恢复原图",
        ["Done"] = "完成",
        ["Saved"] = "已保存",
        ["Back to the original"] = "已恢复原图",
        ["Could not save"] = "保存失败",
        ["Show in Explorer"] = "在文件夹中显示",
        ["Keep on the line"] = "固定在晾衣绳上",
        ["Save to Desktop"] = "另存到桌面",
        ["Save to Pictures\\Screenshots"] = "另存到系统截图文件夹",
        ["Discard"] = "销毁图片",
        ["Take down"] = "销毁图片",
        ["Move to Recycle Bin"] = "销毁图片（移入回收站）",
        ["Take a screenshot and it will hang here"] = "按截图快捷键，图片将自动挂在这里",
        ["Copied"] = "已复制",
        ["Text copied"] = "文字已复制",
        ["just now"] = "刚刚",
        ["{0} min ago"] = "{0} 分钟前",
        ["{0} h ago"] = "{0} 小时前",
        ["Settings"] = "设置",
        ["The line"] = "晾衣绳",
        ["Look"] = "外观",
        ["Captures"] = "截图与保存",
        ["General"] = "常规",
        ["Show or hide the line"] = "显示／隐藏晾衣绳",
        ["Click here and press the keys you want, like Ctrl+Alt+T"] = "点击后按下要设置的快捷键",
        ["Distance from the top of the screen"] = "距屏幕顶部的距离",
        ["You can also drag the line itself up or down"] = "也可以直接上下拖动晾衣绳",
        ["Line colour"] = "晾衣绳颜色",
        ["Clothespins"] = "夹子样式",
        ["Bows at the ends of the line"] = "显示绳子两端的蝴蝶结",
        ["Arrange photos evenly along the line"] = "均匀排列图片",
        ["Off, each photo stays where you put it"] = "关闭后保留手动排列的位置",
        ["Appearance"] = "显示主题",
        ["Language"] = "界面语言",
        ["Follow Windows"] = "跟随系统",
        ["Light"] = "浅色",
        ["Dark"] = "深色",
        ["Caught captures are saved to"] = "图片保存路径（退出或关机后保留）",
        ["Change…"] = "更改…",
        ["Folders watched for new screenshots"] = "监视其他截图软件的保存文件夹",
        ["Add folder…"] = "添加文件夹…",
        ["Remove"] = "移除",
        ["Changes apply right away. Everything lives in {0}."] = "设置立即生效。设置文件：{0}",
        ["Snapline is on the line"] = "Snapline 已启动",
        ["Take a screenshot and it hangs at the top of the screen. Rest the pointer against the top edge to bring the line down, or press {0}."] = "截图会自动保存并挂到晾衣绳。按 {0} 显示或隐藏晾衣绳。",
        ["Bronze"] = "古铜色",
        ["Gray"] = "灰色",
        ["Black"] = "黑色",
        ["White"] = "白色",
        ["Red"] = "红色",
        ["Gold"] = "金色",
        ["Purple"] = "紫色",
        ["Wooden"] = "木夹子",
        ["Aluminium"] = "铝制夹子",
        ["Coloured plastic, mixed"] = "彩色塑料夹子",
        ["Red plastic"] = "红色塑料夹子",
        ["Blue plastic"] = "蓝色塑料夹子",
        ["Green plastic"] = "绿色塑料夹子",
        ["Yellow plastic"] = "黄色塑料夹子",
        ["Click a photo to copy it. Press and hold to edit it. Double click to open it.\nDrag it along the line to reorder, into an app to send a copy, into a folder to keep it, or to the Recycle Bin to let it go.\nRest the pointer against the top edge of the screen, or press {0}, to bring the line down."] = "单击复制，长按标注，双击销毁。悬停显示原尺寸预览，可钉到桌面。\n左键拖动贴图，拖动边缘缩放；Ctrl＋滚轮调整透明度；中键恢复大小与透明度。\n按 {0} 显示或隐藏晾衣绳。图片自动保存，重启后恢复。",
    };

    private static readonly Dictionary<string, string> Ar = new()
    {
        ["Screenshots, hung out to dry."] = "لقطات الشاشة، معلّقة على الحبل.",
        ["Show line"] = "إظهار الحبل",
        ["Hide line"] = "إخفاء الحبل",
        ["Take everything down"] = "إنزال كل اللقطات",
        ["New capture"] = "لقطة جديدة",
        ["Click to take a new screenshot (Win+Shift+S)"] = "انقر لالتقاط لقطة شاشة جديدة (Win+Shift+S)",
        ["Catch clipboard captures"] = "التقاط صور الحافظة",
        ["Win+Shift+S and PrtScn captures hang even when no file is saved"] = "لقطات Win+Shift+S وPrtScn تُعلَّق حتى لو لم يُحفظ ملف",
        ["Stay down until each capture is used"] = "يبقى الحبل ظاهرًا حتى تُستخدم كل لقطة",
        ["The line waits while a capture has not been copied, dragged or opened"] = "ينتظر الحبل ما دامت هناك لقطة لم تُنسخ أو تُسحب أو تُفتح",
        ["Take down after dragging into an app"] = "إنزال اللقطة بعد سحبها إلى تطبيق",
        ["A photo dropped into a chat or an app leaves the line, like one saved to a folder"] = "الصورة التي تُفلت في محادثة أو تطبيق تغادر الحبل، كالتي تُحفظ في مجلد",
        ["Bring the line down when the pointer rests at the top edge"] = "إنزال الحبل عندما يستقر المؤشر عند الحافة العلوية",
        ["Hold the pointer still against the top of the screen for half a second. Off, the shortcut and the tray icon show the line."] = "ثبّت المؤشر عند أعلى الشاشة نصف ثانية. عند الإيقاف، يُظهر الحبلَ الاختصارُ وأيقونةُ شريط المهام.",
        ["Open Screenshots folder"] = "فتح مجلد لقطات الشاشة",
        ["Open caught captures folder"] = "فتح مجلد اللقطات الملتقطة",
        ["Caught captures need a folder of their own, because discarding one sends it to the Recycle Bin. Pick a folder that is not your Desktop, Pictures, Screenshots or a watched folder, and not a parent of them."] = "تحتاج اللقطات الملتقطة إلى مجلد خاص بها، لأن التخلص من لقطة يرسلها إلى سلة المحذوفات. اختر مجلدًا ليس سطح المكتب ولا الصور ولا لقطات الشاشة ولا مجلدًا مراقبًا، وليس مجلدًا يحتويها.",
        ["Sounds"] = "الأصوات",
        ["Start with Windows"] = "التشغيل مع Windows",
        ["Settings…"] = "الإعدادات…",
        ["About Snapline"] = "حول Snapline",
        ["Quit Snapline"] = "إنهاء Snapline",
        ["Copy"] = "نسخ",
        ["Copy text"] = "نسخ النص",
        ["Open"] = "فتح",
        ["Edit"] = "تحرير",
        ["Mark up"] = "تعليم",
        ["Edit in Paint"] = "تحرير في الرسام",
        ["Pen"] = "قلم",
        ["Highlighter"] = "قلم تمييز",
        ["Circle"] = "دائرة",
        ["Box"] = "مربع",
        ["Arrow"] = "سهم",
        ["Text"] = "نص",
        ["Blur"] = "تمويه",
        ["New note"] = "ملاحظة جديدة",
        ["Menu"] = "القائمة",
        ["Edit note"] = "تحرير الملاحظة",
        ["Note colour"] = "لون الملاحظة",
        ["Write a note…"] = "اكتب ملاحظة…",
        ["Yellow"] = "أصفر", ["Orange"] = "برتقالي",
        ["Colour"] = "اللون",
        ["Thickness"] = "السُّمك",
        ["Undo"] = "تراجع",
        ["Redo"] = "إعادة",
        ["Revert to original"] = "العودة إلى الأصل",
        ["Done"] = "تم",
        ["Saved"] = "تم الحفظ",
        ["Back to the original"] = "عادت إلى الأصل",
        ["Could not save"] = "تعذّر الحفظ",
        ["Show in Explorer"] = "إظهار في مستكشف الملفات",
        ["Keep on the line"] = "إبقاؤها على الحبل",
        ["Save to Desktop"] = "حفظ على سطح المكتب",
        ["Save to Pictures\\Screenshots"] = "حفظ في الصور\\لقطات الشاشة",
        ["Discard"] = "تجاهل",
        ["Take down"] = "إنزال",
        ["Move to Recycle Bin"] = "نقل إلى سلة المحذوفات",
        ["Take a screenshot and it will hang here"] = "التقط لقطة شاشة وستُعلَّق هنا",
        ["Copied"] = "تم النسخ",
        ["Text copied"] = "تم نسخ النص",
        ["just now"] = "الآن",
        ["{0} min ago"] = "قبل {0} د",
        ["{0} h ago"] = "قبل {0} س",
        ["Settings"] = "الإعدادات",
        ["The line"] = "الحبل",
        ["Look"] = "المظهر",
        ["Captures"] = "اللقطات",
        ["General"] = "عام",
        ["Show or hide the line"] = "إظهار الحبل أو إخفاؤه",
        ["Click here and press the keys you want, like Ctrl+Alt+T"] = "انقر هنا واضغط المفاتيح التي تريدها، مثل Ctrl+Alt+T",
        ["Distance from the top of the screen"] = "المسافة من أعلى الشاشة",
        ["You can also drag the line itself up or down"] = "يمكنك أيضًا سحب الحبل نفسه للأعلى أو للأسفل",
        ["Line colour"] = "لون الحبل",
        ["Clothespins"] = "المشابك",
        ["Bows at the ends of the line"] = "عُقد على طرفَي الحبل",
        ["Arrange photos evenly along the line"] = "ترتيب الصور بالتساوي على طول الحبل",
        ["Off, each photo stays where you put it"] = "عند الإيقاف، تبقى كل صورة حيث وضعتها",
        ["Appearance"] = "السمة",
        ["Language"] = "اللغة",
        ["Follow Windows"] = "حسب Windows",
        ["Light"] = "فاتح",
        ["Dark"] = "داكن",
        ["Caught captures are saved to"] = "تُحفظ اللقطات الملتقطة في",
        ["Change…"] = "تغيير…",
        ["Folders watched for new screenshots"] = "المجلدات المراقَبة للقطات الجديدة",
        ["Add folder…"] = "إضافة مجلد…",
        ["Remove"] = "إزالة",
        ["Changes apply right away. Everything lives in {0}."] = "تُطبَّق التغييرات فورًا. كل شيء محفوظ في {0}.",
        ["Snapline is on the line"] = "Snapline جاهز",
        ["Take a screenshot and it hangs at the top of the screen. Rest the pointer against the top edge to bring the line down, or press {0}."] = "التقط لقطة شاشة وستُعلَّق أعلى الشاشة. ثبّت المؤشر عند الحافة العلوية لإنزال الحبل، أو اضغط {0}.",
        ["Click a photo to copy it. Press and hold to edit it. Double click to open it.\nDrag it along the line to reorder, into an app to send a copy, into a folder to keep it, or to the Recycle Bin to let it go.\nRest the pointer against the top edge of the screen, or press {0}, to bring the line down."] =
            "انقر على صورة لنسخها. اضغط مطوّلًا لتحريرها. انقر نقرًا مزدوجًا لفتحها.\nاسحبها على طول الحبل لإعادة الترتيب، أو إلى تطبيق لإرسال نسخة، أو إلى مجلد للاحتفاظ بها، أو إلى سلة المحذوفات للتخلص منها.\nثبّت المؤشر عند الحافة العلوية للشاشة، أو اضغط {0}، لإنزال الحبل.",
        ["Bronze"] = "برونزي", ["Gray"] = "رمادي", ["Black"] = "أسود", ["White"] = "أبيض", ["Red"] = "أحمر",
        ["Blue"] = "أزرق", ["Green"] = "أخضر", ["Gold"] = "ذهبي", ["Pink"] = "وردي", ["Purple"] = "بنفسجي",
        ["Wooden"] = "خشبي", ["Aluminium"] = "ألومنيوم", ["Coloured plastic, mixed"] = "بلاستيك ملوّن متنوّع",
        ["Red plastic"] = "بلاستيك أحمر", ["Blue plastic"] = "بلاستيك أزرق", ["Green plastic"] = "بلاستيك أخضر", ["Yellow plastic"] = "بلاستيك أصفر",
    };
}
