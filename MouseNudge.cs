using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class MouseNudge
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int dx, dy; public uint data, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint type; public MouseInput mouse; }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInput { public uint size, time; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "MouseNudge";
    const string CupGlyph = "";
    const uint KeepSystemAwake = 0x80000001;
    const uint ReleaseAwake = 0x80000000;
    const int NudgeEveryMs = 60000;
    const int CoverAfterMs = 180000;
    const int CheckEveryMs = 500;

    static NotifyIcon tray;
    static Form cover;
    static bool nudging;
    static uint ownInputTime;
    static uint lastRealInput;
    static int lastNudge;

    static uint IdleMs(uint now, uint lastReal)
    {
        return unchecked(now - lastReal);
    }

    static bool ShouldCover(bool on, uint idleMs)
    {
        return on && idleMs >= CoverAfterMs;
    }

    static uint LastInputTime()
    {
        var info = new LastInput { size = (uint)Marshal.SizeOf(typeof(LastInput)) };
        GetLastInputInfo(ref info);
        return info.time;
    }

    static Input Step(int dx)
    {
        var input = new Input();
        input.mouse.dx = dx;
        input.mouse.flags = 1;
        return input;
    }

    static void Wiggle()
    {
        SendInput(2, new[] { Step(1), Step(-1) }, Marshal.SizeOf(typeof(Input)));
        ownInputTime = LastInputTime();
        lastNudge = Environment.TickCount;
    }

    static bool TaskbarIsLight()
    {
        var setting = Registry.GetValue(ThemeKey, "SystemUsesLightTheme", 1);
        return !(setting is int) || (int)setting == 1;
    }

    static FontFamily IconFont()
    {
        return FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? new FontFamily("Segoe MDL2 Assets");
    }

    // The font's own cup, so line weight and colour match Resolution Switch and the system icons; on only adds steam.
    static Bitmap DrawCup(bool on, int size, Color colour)
    {
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(IconFont(), size, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(colour))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(CupGlyph, font, brush, 0, size / 8, StringFormat.GenericTypographic);
            if (on)
            {
                // Whole-pixel wisps: anti-aliased curves this small turn grey and blurry next to the glyph's crisp lines.
                graphics.ScaleTransform(size / 16f, size / 16f);
                foreach (int x in new[] { 4, 8 }) graphics.FillRectangle(brush, x, 0, 1, 3);
            }
        }
        return bitmap;
    }

    static Icon DrawIcon(bool on)
    {
        using (var bitmap = DrawCup(on, SystemInformation.SmallIconSize.Width, TaskbarIsLight() ? Color.Black : Color.White))
            return Icon.FromHandle(bitmap.GetHicon());
    }

    static void ShowState()
    {
        var old = tray.Icon;
        tray.Icon = DrawIcon(nudging);
        if (old != null) DestroyIcon(old.Handle);
        tray.Text = nudging ? "Mouse Nudge: on, PC stays active" : "Mouse Nudge: off";
    }

    static void SetNudging(bool on)
    {
        nudging = on;
        SetThreadExecutionState(on ? KeepSystemAwake : ReleaseAwake);
        if (on) Wiggle();
        else HideCover();
        ShowState();
    }

    static Form MakeCover()
    {
        var form = new BlackCover
        {
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Color.Black,
            TopMost = true,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = SystemInformation.VirtualScreen
        };
        return form;
    }

    static void ShowCover()
    {
        if (cover != null) return;
        cover = MakeCover();
        cover.Show();
        Cursor.Hide();
    }

    static void HideCover()
    {
        if (cover == null) return;
        Cursor.Show();
        cover.Close();
        cover.Dispose();
        cover = null;
    }

    static void Check()
    {
        uint last = LastInputTime();
        if (last != ownInputTime) lastRealInput = last;
        if (!nudging) return;
        if (ShouldCover(nudging, IdleMs((uint)Environment.TickCount, lastRealInput))) ShowCover();
        else HideCover();
        if (unchecked(Environment.TickCount - lastNudge) >= NudgeEveryMs) Wiggle();
    }

    static string RunCommand()
    {
        return "\"" + Application.ExecutablePath + "\"";
    }

    static bool StartsWithWindows()
    {
        var value = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, RunValue, null) as string;
        return RunCommand().Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    static void ToggleStartWithWindows(object sender, EventArgs e)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartsWithWindows()) key.DeleteValue(RunValue, false);
            else key.SetValue(RunValue, RunCommand());
        }
    }

    static void FillMenu(object sender, EventArgs e)
    {
        var items = ((ContextMenu)sender).MenuItems;
        items.Clear();
        items.Add(nudging ? "Turn off" : "Turn on", (s, a) => SetNudging(!nudging));
        items.Add(new MenuItem("Start with Windows", ToggleStartWithWindows) { Checked = StartsWithWindows() });
        items.Add("-");
        items.Add("Exit", (s, a) => { tray.Visible = false; Application.Exit(); });
    }

    static int SelfTest()
    {
        var failures = new List<string>();
        Action<bool, string> expect = (ok, name) => { if (!ok) failures.Add(name); };
        expect(IdleMs(5000, 2000) == 3000, "idle time");
        expect(IdleMs(100, uint.MaxValue - 99) == 200, "idle time across the tick counter wrap");
        expect(!ShouldCover(false, 999999), "no cover while off");
        expect(!ShouldCover(true, CoverAfterMs - 1), "no cover before three minutes");
        expect(ShouldCover(true, CoverAfterMs), "cover at three minutes");
        foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
        Console.WriteLine("{0} of 5 self-test checks passed.", 5 - failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        bool firstCopy;
        using (new Mutex(true, "MouseNudgeTray", out firstCopy))
        {
            if (!firstCopy) return 0;
            SetProcessDPIAware();
            SetPreferredAppMode(1);
            FlushMenuThemes();
            Application.EnableVisualStyles();
            lastRealInput = LastInputTime();
            tray = new NotifyIcon { ContextMenu = new ContextMenu(), Visible = true };
            tray.ContextMenu.Popup += FillMenu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetNudging(!nudging); };
            SystemEvents.UserPreferenceChanged += (s, e) => ShowState();
            ShowState();
            var timer = new System.Windows.Forms.Timer { Interval = CheckEveryMs };
            timer.Tick += (s, e) => Check();
            timer.Start();
            Application.Run();
            HideCover();
            SetThreadExecutionState(ReleaseAwake);
            tray.Visible = false;
        }
        return 0;
    }
}

class BlackCover : Form
{
    const int NoActivate = 0x08000000;
    const int ToolWindow = 0x00000080;

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NoActivate | ToolWindow;
            return parameters;
        }
    }
}
