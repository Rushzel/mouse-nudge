using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    static GraphicsPath Cup(float left, float top, float right, float bottom, float radius)
    {
        var path = new GraphicsPath();
        path.AddLine(left, top, right, top);
        path.AddLine(right, top, right, bottom - radius);
        path.AddArc(right - 2 * radius, bottom - 2 * radius, 2 * radius, 2 * radius, 0, 90);
        path.AddArc(left, bottom - 2 * radius, 2 * radius, 2 * radius, 90, 90);
        path.CloseFigure();
        return path;
    }

    static GraphicsPath Handle(float x, float top, float radius)
    {
        var path = new GraphicsPath();
        path.AddLine(x, top, x + radius, top);
        path.AddArc(x, top, 2 * radius, 2 * radius, 270, 180);
        path.AddLine(x + radius, top + 2 * radius, x, top + 2 * radius);
        return path;
    }

    static Icon DrawIcon(bool on)
    {
        bool light = TaskbarIsLight();
        var colour = on ? (light ? Color.FromArgb(0x9a, 0x6b, 0x00) : Color.FromArgb(0xff, 0xd7, 0x5e))
                        : (light ? Color.Black : Color.FromArgb(0xec, 0xe7, 0xd8));
        int size = SystemInformation.SmallIconSize.Width;
        using (var bitmap = new Bitmap(size, size))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(colour))
        using (var pen = new Pen(colour, 2f))
        using (var steam = new Pen(colour, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 16f, size / 16f);
            if (on)
            {
                using (var cup = Cup(1, 6, 12, 15, 4)) graphics.FillPath(brush, cup);
                using (var handle = Handle(12, 7.5f, 1.6f)) graphics.DrawPath(pen, handle);
                foreach (float x in new[] { 4.5f, 8.5f }) graphics.DrawBezier(steam, x, 0.8f, x - 1f, 2f, x + 1f, 3f, x, 4.4f);
            }
            else
            {
                using (var cup = Cup(2, 7, 11, 14, 3)) graphics.DrawPath(pen, cup);
                using (var handle = Handle(11, 8, 1.5f)) graphics.DrawPath(pen, handle);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }
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
