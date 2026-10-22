// ============================================================================
//  RefreshRateTray  —  按电源状态自动切换笔记本屏幕刷新率
//      · 使用电池      -> 该显示器支持的最低刷新率（可指定具体档位）
//      · 使用电源适配器 -> 该显示器支持的最高刷新率（可指定具体档位）
//  所有档位都在运行时从 Windows 枚举，程序里不写死任何刷新率数值。
//  支持多显示器：逐台按各自支持的档位应用。
//  常驻托盘，图标上用像素字显示当前刷新率。
//
//  编译（.NET Framework 自带 csc，无需安装 SDK）：
//     见同目录 build.ps1
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace RefreshRateTray
{
    // ------------------------------------------------------------------
    //  Win32
    // ------------------------------------------------------------------
    internal static class Native
    {
        public const int ENUM_CURRENT_SETTINGS = -1;

        public const int DM_DISPLAYFREQUENCY = 0x00400000;
        public const int CDS_UPDATEREGISTRY = 0x00000001;

        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

        public const int SM_CXSMICON = 49;
        public const int SM_CYSMICON = 50;

        public const int ATTACH_PARENT_PROCESS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        public static extern bool FreeConsole();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreateIconFromResourceEx(byte[] resourceBits, uint resourceSize,
            bool isIcon, uint version, int cxDesired, int cyDesired, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);
    }

    // ------------------------------------------------------------------
    //  电源状态
    // ------------------------------------------------------------------
    internal static class PowerState
    {
        public static bool? IsOnAc()
        {
            Native.SYSTEM_POWER_STATUS status;
            if (!Native.GetSystemPowerStatus(out status)) return null;
            if (status.ACLineStatus == 1) return true;
            if (status.ACLineStatus == 0) return false;
            return null;   // 255 = 未知
        }

        public static string Describe(bool onAc)
        {
            return onAc ? "交流电源" : "电池";
        }
    }

    // ------------------------------------------------------------------
    //  显示模式
    // ------------------------------------------------------------------
    internal static class Displays
    {
        public static string PrimaryDeviceName()
        {
            Native.DISPLAY_DEVICE dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));

            for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 &&
                    (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                {
                    return dd.DeviceName;
                }
                dd = new Native.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            }
            return null;
        }

        /// <summary>一台正在桌面上的显示器及其支持的刷新率。</summary>
        public sealed class DisplayInfo
        {
            public string Device;
            public string Monitor;
            public bool Primary;
            public int CurrentHz;
            public List<int> Rates = new List<int>();

            public string Label
            {
                get
                {
                    string name = string.IsNullOrEmpty(Monitor) ? Device : Monitor;
                    return Primary ? name + "（主）" : name;
                }
            }

            public int Highest { get { return Rates.Count > 0 ? Rates[Rates.Count - 1] : 0; } }
            public int Lowest { get { return Rates.Count > 0 ? Rates[0] : 0; } }
        }

        /// <summary>枚举所有挂在桌面上的显示器（各自的分辨率/刷新率可能不同）。</summary>
        public static List<DisplayInfo> EnumerateActive()
        {
            List<DisplayInfo> list = new List<DisplayInfo>();

            Native.DISPLAY_DEVICE dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));

            for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    DisplayInfo info = new DisplayInfo();
                    info.Device = dd.DeviceName;
                    info.Primary = (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                    info.Monitor = MonitorName(dd.DeviceName);
                    info.CurrentHz = CurrentFrequency(dd.DeviceName);
                    info.Rates = SupportedFrequencies(dd.DeviceName);
                    if (info.CurrentHz > 0 && !info.Rates.Contains(info.CurrentHz))
                    {
                        info.Rates.Add(info.CurrentHz);
                        info.Rates.Sort();
                    }
                    list.Add(info);
                }

                dd = new Native.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            }

            return list;
        }

        public static string MonitorName(string device)
        {
            Native.DISPLAY_DEVICE mon = new Native.DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            if (Native.EnumDisplayDevices(device, 0, ref mon, 0))
            {
                string s = mon.DeviceString;
                if (!string.IsNullOrEmpty(s)) return s.Trim();
            }
            return device;
        }

        /// <summary>所有显示器刷新率的并集（升序），用于菜单里的候选项。</summary>
        public static List<int> UnionRates(List<DisplayInfo> displays)
        {
            List<int> all = new List<int>();
            if (displays != null)
            {
                foreach (DisplayInfo d in displays)
                {
                    foreach (int r in d.Rates)
                    {
                        if (!all.Contains(r)) all.Add(r);
                    }
                }
            }
            all.Sort();
            return all;
        }

        /// <summary>
        /// 算出某台显示器该切到多少赫兹：desired &gt; 0 取最接近档；
        /// desired == 0 表示「自动」，插电取最高、电池取最低。
        /// </summary>
        public static int ResolveTarget(List<int> rates, int desired, bool preferHighest)
        {
            if (rates == null || rates.Count == 0) return desired;
            if (desired > 0) return PickFrequency(rates, desired);
            return preferHighest ? rates[rates.Count - 1] : rates[0];
        }

        public static Native.DEVMODE CurrentMode(string device)
        {
            Native.DEVMODE dm = new Native.DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            if (!Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm))
                return new Native.DEVMODE();   // dmDisplayFrequency == 0 表示读取失败
            return dm;
        }

        public static int CurrentFrequency(string device)
        {
            if (string.IsNullOrEmpty(device)) return -1;
            Native.DEVMODE dm = CurrentMode(device);
            return dm.dmDisplayFrequency > 0 ? dm.dmDisplayFrequency : -1;
        }

        /// <summary>当前分辨率、色深下所有可用的刷新率（升序）。</summary>
        public static List<int> SupportedFrequencies(string device)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(device)) return result;

            Native.DEVMODE current = CurrentMode(device);
            if (current.dmDisplayFrequency <= 0) return result;

            Native.DEVMODE dm = new Native.DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));

            for (int i = 0; Native.EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmPelsWidth == current.dmPelsWidth &&
                    dm.dmPelsHeight == current.dmPelsHeight &&
                    dm.dmBitsPerPel == current.dmBitsPerPel &&
                    dm.dmDisplayFrequency > 0 &&
                    !result.Contains(dm.dmDisplayFrequency))
                {
                    result.Add(dm.dmDisplayFrequency);
                }
                dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            }

            result.Sort();
            return result;
        }

        /// <summary>如果目标刷新率不可用，退到最接近的一档。</summary>
        public static int PickFrequency(List<int> supported, int desired)
        {
            if (supported == null || supported.Count == 0) return desired;
            if (supported.Contains(desired)) return desired;

            int best = supported[0];
            for (int i = 1; i < supported.Count; i++)
            {
                if (Math.Abs(supported[i] - desired) < Math.Abs(best - desired))
                    best = supported[i];
            }
            return best;
        }

        /// <summary>切换到指定刷新率，返回 0 表示成功，否则返回 Win32 错误码。</summary>
        public static int Apply(string device, int hz, out int actualHz)
        {
            actualHz = -1;
            if (string.IsNullOrEmpty(device)) return -1;

            Native.DEVMODE mode = CurrentMode(device);
            if (mode.dmDisplayFrequency <= 0) return -1;

            if (mode.dmDisplayFrequency != hz)
            {
                mode.dmDisplayFrequency = hz;
                mode.dmFields = mode.dmFields | Native.DM_DISPLAYFREQUENCY;

                int code = Native.ChangeDisplaySettingsEx(
                    device, ref mode, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);

                if (code != 0) return code;

                // 等驱动真正生效
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    Thread.Sleep(60);
                    actualHz = CurrentFrequency(device);
                    if (actualHz == hz) break;
                }
            }
            else
            {
                actualHz = mode.dmDisplayFrequency;
            }

            if (actualHz <= 0) actualHz = CurrentFrequency(device);
            return 0;
        }

        public static string DescribeResult(int code)
        {
            switch (code)
            {
                case 0: return "成功";
                case 1: return "需要重启才能生效";
                case 2: return "显卡驱动不支持该模式";
                case 3: return "未能更新显示模式";
                case -1: return "参数无效";
                case -2: return "标志无效";
                case -3: return "驱动无效";
                case -4: return "不支持双显示器";
                case -5: return "模式无效";
                default: return "未知错误 " + code;
            }
        }
    }

    // ------------------------------------------------------------------
    //  像素字图标（4x6 点阵，无字体依赖）
    // ------------------------------------------------------------------
    internal static class TrayIconFactory
    {
        private static readonly Dictionary<char, string[]> Glyphs = BuildGlyphs();

        private static Dictionary<char, string[]> BuildGlyphs()
        {
            Dictionary<char, string[]> d = new Dictionary<char, string[]>();
            d['0'] = new string[] { "0110", "1001", "1001", "1001", "1001", "0110" };
            d['1'] = new string[] { "0010", "0110", "0010", "0010", "0010", "0111" };
            d['2'] = new string[] { "1110", "0001", "0010", "0100", "1000", "1111" };
            d['3'] = new string[] { "1110", "0001", "0110", "0001", "0001", "1110" };
            d['4'] = new string[] { "0010", "0110", "1010", "1111", "0010", "0010" };
            d['5'] = new string[] { "1111", "1000", "1110", "0001", "0001", "1110" };
            d['6'] = new string[] { "0110", "1000", "1110", "1001", "1001", "0110" };
            d['7'] = new string[] { "1110", "0001", "0010", "0100", "0100", "0100" };
            d['8'] = new string[] { "0110", "1001", "0110", "1001", "1001", "0110" };
            d['9'] = new string[] { "0110", "1001", "1001", "0111", "0001", "0110" };
            d['-'] = new string[] { "0000", "0000", "0110", "0000", "0000", "0000" };
            d['?'] = new string[] { "0110", "1001", "0010", "0100", "0000", "0100" };
            d[' '] = new string[] { "0000", "0000", "0000", "0000", "0000", "0000" };
            return d;
        }

        public static Icon Render(string text, Color color, int size)
        {
            if (size < 8) size = 8;
            if (text == null || text.Length == 0) text = "?";

            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.None;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                    int n = text.Length;
                    int scale = 1;
                    for (int s = 2; s <= 8; s++)
                    {
                        int w = (n * 4 + (n - 1)) * s;
                        int h = 6 * s;
                        if (w <= size - 2 && h <= size - 2) scale = s;
                        else break;
                    }

                    int totalW = (n * 4 + (n - 1)) * scale;
                    int totalH = 6 * scale;
                    int x0 = (size - totalW) / 2;
                    int y0 = (size - totalH) / 2;
                    int shadow = Math.Max(1, scale / 2);

                    using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    using (SolidBrush mainBrush = new SolidBrush(color))
                    {
                        for (int pass = 0; pass < 2; pass++)
                        {
                            SolidBrush brush = (pass == 0) ? shadowBrush : mainBrush;
                            int x = x0 + (pass == 0 ? shadow : 0);
                            int y = y0 + (pass == 0 ? shadow : 0);

                            for (int i = 0; i < n; i++)
                            {
                                string[] rows;
                                if (!Glyphs.TryGetValue(text[i], out rows)) rows = Glyphs['?'];
                                for (int r = 0; r < 6; r++)
                                {
                                    for (int c = 0; c < 4; c++)
                                    {
                                        if (rows[r][c] == '1')
                                            g.FillRectangle(brush, x + c * scale, y + r * scale, scale, scale);
                                    }
                                }
                                x += 5 * scale;
                            }
                        }
                    }
                }
                return ToIcon(bmp);
            }
        }

        /// <summary>
        /// 把位图打包成图标资源（BITMAPINFOHEADER + BGRA 像素 + AND 掩码），
        /// 交给 CreateIconFromResourceEx 生成 HICON，再克隆成独立的 Icon。
        /// 这样既不依赖字体，也不需要托管的 MemoryStream 保持打开。
        /// </summary>
        private static Icon ToIcon(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            int maskSize = maskStride * h;

            byte[] blob;
            using (MemoryStream ms = new MemoryStream(40 + w * h * 4 + maskSize))
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    // BITMAPINFOHEADER（高度为 2 倍：XOR 图像 + AND 掩码）
                    bw.Write(40);
                    bw.Write(w);
                    bw.Write(h * 2);
                    bw.Write((short)1);
                    bw.Write((short)32);
                    bw.Write(0);
                    bw.Write(w * h * 4);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);
                    bw.Write(0);

                    for (int y = h - 1; y >= 0; y--)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            bw.Write(c.B);
                            bw.Write(c.G);
                            bw.Write(c.R);
                            bw.Write(c.A);
                        }
                    }
                    bw.Write(new byte[maskSize]);
                    bw.Flush();
                    blob = ms.ToArray();
                }
            }

            IntPtr handle = Native.CreateIconFromResourceEx(blob, (uint)blob.Length, true, 0x00030000u, w, h, 0);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateIconFromResourceEx 失败，错误码 " + Marshal.GetLastWin32Error());

            try
            {
                using (Icon temp = Icon.FromHandle(handle))
                {
                    // Clone() 通过 CopyImage 生成独立句柄，之后就能安全销毁临时 HICON
                    return (Icon)temp.Clone();
                }
            }
            finally
            {
                Native.DestroyIcon(handle);
            }
        }

        public static void SavePreview(string path, Color color, string label)
        {
            using (Bitmap b = new Bitmap(256, 96))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(32, 32, 32));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (string.IsNullOrEmpty(label)) label = "--";
                string[] samples = { label, "--" };
                int[] sizes = { 16, 32, 48 };
                int x = 8;
                for (int i = 0; i < samples.Length; i++)
                {
                    using (Icon ic = Render(samples[i], color, 32))
                    {
                        g.DrawIcon(ic, new Rectangle(x, 8, 32, 32));
                        g.DrawString(samples[i], SystemFonts.DefaultFont, Brushes.Gray, x, 44);
                        x += 40;
                    }
                }
                x = 8;
                for (int i = 0; i < sizes.Length; i++)
                {
                    using (Icon ic = Render(label, color, sizes[i]))
                    {
                        g.DrawIcon(ic, new Rectangle(x, 64, sizes[i], sizes[i]));
                    }
                    x += sizes[i] + 12;
                }
                b.Save(path, ImageFormat.Png);
            }
        }
    }

    // ------------------------------------------------------------------
    //  路径：普通版把设置写注册表；便携版（/define:POCKET_BUILD）全部放在 exe 同目录
    //     settings.ini   设置
    //     app.log        日志
    // ------------------------------------------------------------------
    internal static class AppPaths
    {
// 用运行时只读字段而不是 const：否则两个分支里必有一支被编译器判成「无法访问的代码」
        public static readonly bool Pocket = IsPocketBuild();

        private static bool IsPocketBuild()
        {
#if POCKET_BUILD
            return true;
#else
            return false;
#endif
        }

        public static string BaseDir
        {
            get
            {
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    if (!string.IsNullOrEmpty(dir))
                        return dir.TrimEnd(Path.DirectorySeparatorChar);
                }
                catch { }
                return Environment.CurrentDirectory;
            }
        }

        public static string SettingsFile { get { return Path.Combine(BaseDir, "settings.ini"); } }

        public static string LogFile { get { return Path.Combine(BaseDir, "app.log"); } }
    }

    // ------------------------------------------------------------------
    //  设置（普通版：注册表 HKCU；便携版：exe 同目录 settings.ini）
    // ------------------------------------------------------------------
    internal sealed class Settings
    {
        private const string RegPath = @"Software\RefreshRateTray";
        private const string IniHeader =
            "# RefreshRateTray 便携版设置（删掉本文件即恢复默认）\r\n" +
            "# AcHz      : 插电源时的目标刷新率，0 = 自动取该屏最高档\r\n" +
            "# BatteryHz : 用电池时的目标刷新率，0 = 自动取该屏最低档\r\n" +
            "# Auto      : 1 = 按电源自动切换，0 = 只显示不干预\r\n";

        // 0 表示「自动」：插电取该显示器最高档，电池取最低档
        public int AcHz = 0;
        public int BatteryHz = 0;
        public bool Auto = true;

        public static Settings Load()
        {
            Settings s = new Settings();
            if (AppPaths.Pocket)
            {
                s.LoadIni();
                return s;
            }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AcHz");
                        if (v is int && (int)v >= 0 && (int)v <= 480) s.AcHz = (int)v;

                        v = k.GetValue("BatteryHz");
                        if (v is int && (int)v >= 0 && (int)v <= 480) s.BatteryHz = (int)v;

                        v = k.GetValue("Auto");
                        if (v is int) s.Auto = ((int)v) != 0;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            if (AppPaths.Pocket)
            {
                SaveIni();
                return;
            }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    k.SetValue("AcHz", AcHz, RegistryValueKind.DWord);
                    k.SetValue("BatteryHz", BatteryHz, RegistryValueKind.DWord);
                    k.SetValue("Auto", Auto ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // ---------- 便携版：同目录 settings.ini（key=value，#/; 开头是注释） ----------
        private void LoadIni()
        {
            try
            {
                string path = AppPaths.SettingsFile;
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    if (!int.TryParse(val, out n)) continue;

                    if (string.Equals(key, "AcHz", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n >= 0 && n <= 480) AcHz = n;
                    }
                    else if (string.Equals(key, "BatteryHz", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n >= 0 && n <= 480) BatteryHz = n;
                    }
                    else if (string.Equals(key, "Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        Auto = n != 0;
                    }
                }
            }
            catch { }
        }

        private void SaveIni()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(IniHeader);
                sb.Append("AcHz=").Append(AcHz).Append("\r\n");
                sb.Append("BatteryHz=").Append(BatteryHz).Append("\r\n");
                sb.Append("Auto=").Append(Auto ? 1 : 0).Append("\r\n");
                File.WriteAllText(AppPaths.SettingsFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    //  开机自启动
    // ------------------------------------------------------------------
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // 便携版用独立的 Run 值名，避免和注册表版互相覆盖
        private static string ValueName
        {
            get { return AppPaths.Pocket ? "RefreshRateTray (Pocket)" : "RefreshRateTray"; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled)
                        k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(ValueName) != null)
                        k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    //  托盘主逻辑
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    //  轻量日志：普通版 %LOCALAPPDATA%\RefreshRateTray\app.log；便携版 exe 同目录 app.log
    // ------------------------------------------------------------------
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        private static string FilePath()
        {
            if (_path == null)
            {
                try
                {
                    if (AppPaths.Pocket)
                    {
                        _path = AppPaths.LogFile;   // 便携版：与 exe 同目录
                    }
                    else
                    {
                        string dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "RefreshRateTray");
                        Directory.CreateDirectory(dir);
                        _path = Path.Combine(dir, "app.log");
                    }
                }
                catch
                {
                    _path = string.Empty;
                }
            }
            return _path;
        }

        public static void Write(string message)
        {
            try
            {
                string p = FilePath();
                if (string.IsNullOrEmpty(p)) return;
                lock (Gate)
                {
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                    File.AppendAllText(p,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private static readonly Color AcColor = Color.FromArgb(255, 74, 222, 128);
        private static readonly Color BatteryColor = Color.FromArgb(255, 251, 191, 36);
        private static readonly Color PausedColor = Color.FromArgb(255, 148, 163, 184);

        private readonly Settings _cfg;
        private readonly NotifyIcon _notify;
        private readonly WinTimer _timer;
        private readonly ContextMenuStrip _menu;

        private ToolStripMenuItem _miHeader;
        private ToolStripMenuItem _miApply;
        private ToolStripMenuItem _miAuto;
        private ToolStripMenuItem _miPause;
        private ToolStripMenuItem _miAc;
        private ToolStripMenuItem _miBat;
        private ToolStripMenuItem _miStartup;
        private ToolStripMenuItem _miExit;

        private string _device;
        private DateTime _deviceResolvedAt = DateTime.MinValue;
        private bool _onAc = true;
        private int _currentHz = -1;
        private List<int> _supported = new List<int>();
        private List<Displays.DisplayInfo> _displays = new List<Displays.DisplayInfo>();
        private DateTime _pausedUntil = DateTime.MinValue;
        private DateTime _lastTick = DateTime.UtcNow;
        private int _iconSize;
        private string _iconKey = "";
        private bool _busy;

        public TrayApp(Settings cfg)
        {
            _cfg = cfg;
            _onAc = PowerState.IsOnAc() ?? true;
            RefreshDisplays();
            _iconSize = QueryIconSize();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Opening += delegate { RefreshMenu(); };
            BuildMenu();

            _notify = new NotifyIcon();
            _notify.ContextMenuStrip = _menu;
            _notify.Visible = true;
            _notify.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    RefreshMenu();
                    _menu.Show(Cursor.Position);
                }
            };

            _currentHz = Displays.CurrentFrequency(_device);
            if (_currentHz <= 0) _currentHz = -1;
            Log.Write(string.Format("启动 显示器={0} 台 主屏={1} 电源={2} 当前={3}Hz 目标(AC/电池)={4}/{5} 自动={6}",
                _displays.Count, _device, _onAc ? "交流" : "电池", _currentHz,
                HzText(_cfg.AcHz, true), HzText(_cfg.BatteryHz, false), _cfg.Auto));
            foreach (Displays.DisplayInfo d in _displays)
                Log.Write(string.Format("  屏幕 {0} 当前={1}Hz 支持={2}", d.Label, d.CurrentHz, RatesText(d.Rates)));
            if (_displays.Count == 0)
                Log.Write("警告：没有枚举到任何显示器");
            UpdateIcon(true);

            // 启动时按当前电源状态对齐一次
            ApplyTarget(false);

            _timer = new WinTimer();
            _timer.Interval = 2000;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private static int QueryIconSize()
        {
            int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
            if (size <= 0) size = 16;
            return size;
        }

        // ---------------- 显示器枚举 ----------------

        /// <summary>重新枚举所有显示器（拔插外接屏、改分辨率后都要重来）。</summary>
        private void RefreshDisplays()
        {
            _displays = Displays.EnumerateActive();
            _deviceResolvedAt = DateTime.UtcNow;

            string primary = Displays.PrimaryDeviceName();
            if (primary == null && _displays.Count > 0) primary = _displays[0].Device;
            _device = primary;

            _supported = Displays.UnionRates(_displays);

            int hz = Displays.CurrentFrequency(_device);
            if (hz > 0) _currentHz = hz;
        }

        private Displays.DisplayInfo PrimaryInfo()
        {
            foreach (Displays.DisplayInfo d in _displays)
                if (d.Primary) return d;
            return _displays.Count > 0 ? _displays[0] : null;
        }

        private static string RatesText(List<int> rates)
        {
            if (rates == null || rates.Count == 0) return "--";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < rates.Count; i++)
            {
                if (i > 0) sb.Append("/");
                sb.Append(rates[i]);
            }
            return sb.ToString();
        }

        /// <summary>配置值的可读形式：0 表示「自动」——插电取最高、电池取最低。</summary>
        private static string HzText(int configured, bool preferHighest)
        {
            if (configured > 0) return configured + "Hz";
            return preferHighest ? "自动(最高)" : "自动(最低)";
        }

        // ---------------- 菜单 ----------------

        private void BuildMenu()
        {
            _miHeader = new ToolStripMenuItem("正在读取…");
            _miHeader.Enabled = false;

            _miApply = new ToolStripMenuItem("立即切换");
            _miApply.DropDownOpening += delegate { FillApplyMenu(); };

            _miAuto = new ToolStripMenuItem("自动切换");
            _miAuto.Click += delegate
            {
                _cfg.Auto = !_cfg.Auto;
                _cfg.Save();
                if (_cfg.Auto) ApplyTarget(false);
                RefreshMenu();
            };

            _miPause = new ToolStripMenuItem("暂停自动切换");
            _miPause.DropDownOpening += delegate { FillPauseMenu(); };

            _miAc = new ToolStripMenuItem("适配器时使用");
            _miBat = new ToolStripMenuItem("电池时使用");

            _miStartup = new ToolStripMenuItem("开机自启动");
            _miStartup.DropDownOpening += delegate { FillStartupMenu(); };

            _miExit = new ToolStripMenuItem("退出");
            _miExit.Click += delegate { ExitThread(); };

            _menu.Items.Add(_miHeader);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miApply);
            _menu.Items.Add(_miAuto);
            _menu.Items.Add(_miPause);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miAc);
            _menu.Items.Add(_miBat);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miStartup);
            _menu.Items.Add(_miExit);

            // 先把子项填好，否则首次点击时 ToolStripMenuItem 不认为自己有下拉菜单（没有小箭头、点了没反应）
            FillApplyMenu();
            FillPauseMenu();
            FillStartupMenu();
        }

        private void RefreshMenu()
        {
            // 设备名/支持的刷新率可能随显示器插拔或分辨率变化
            if (_displays.Count == 0 || (DateTime.UtcNow - _deviceResolvedAt).TotalSeconds > 5)
                RefreshDisplays();

            bool paused = _pausedUntil > DateTime.UtcNow;
            int hz = _currentHz;

            string screens = _displays.Count > 1 ? string.Format(" · {0} 个屏幕", _displays.Count) : "";
            _miHeader.Text = string.Format(
                "当前 {0} Hz · {1}{2}{3}{4}",
                hz > 0 ? hz.ToString() : "--",
                PowerState.Describe(_onAc),
                screens,
                _cfg.Auto ? "" : " · 自动切换已关闭",
                paused ? " · 已暂停" : "");

            _miApply.Text = string.Format("立即切换（当前 {0} Hz）", hz > 0 ? hz.ToString() : "--");
            _miAuto.Checked = _cfg.Auto;
            _miPause.Text = paused ? "暂停自动切换（已暂停）" : "暂停自动切换";

            FillRateMenu(_miAc, _cfg.AcHz, true);
            FillRateMenu(_miBat, _cfg.BatteryHz, false);

            _miStartup.Text = Startup.IsEnabled() ? "开机自启动（已开启）" : "开机自启动（已关闭）";
        }

        /// <summary>「立即切换」列出所有显示器支持的刷新率并集，点哪个立刻全切到（不改写配置）。</summary>
        private void FillApplyMenu()
        {
            if (_miApply.DropDownItems.Count > 0) _miApply.DropDownItems.Clear();

            List<int> rates = new List<int>(_supported);

            Displays.DisplayInfo primary = PrimaryInfo();
            int now = primary != null ? primary.CurrentHz : _currentHz;
            if (now > 0)
            {
                _currentHz = now;
                if (!rates.Contains(now)) { rates.Add(now); rates.Sort(); }
            }

            if (rates.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（未读取到可用刷新率）");
                empty.Enabled = false;
                _miApply.DropDownItems.Add(empty);
            }

            foreach (int value in rates)
            {
                int v = value;
                ToolStripMenuItem item = new ToolStripMenuItem(v + " Hz");
                bool isNow = (v == now);
                item.Checked = isNow;
                item.Enabled = !isNow;
                item.Click += delegate { ApplyNow(v); };
                _miApply.DropDownItems.Add(item);
            }

            _miApply.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem manual = new ToolStripMenuItem("手动输入…");
            manual.Click += delegate { PromptManualRate(); };
            _miApply.DropDownItems.Add(manual);
        }

        /// <summary>「适配器/电池时使用」子菜单：自动（最高/最低）+ 并集档位 + 手动输入。</summary>
        private void FillRateMenu(ToolStripMenuItem parent, int selected, bool forAc)
        {
            parent.DropDownItems.Clear();

            bool ac = forAc;
            ToolStripMenuItem auto = new ToolStripMenuItem(forAc ? "自动（最高刷新率）" : "自动（最低刷新率）");
            auto.Checked = (selected <= 0);
            auto.Click += delegate { SetRate(ac, 0); };
            parent.DropDownItems.Add(auto);
            parent.DropDownItems.Add(new ToolStripSeparator());

            List<int> rates = new List<int>(_supported);
            if (selected > 0 && !rates.Contains(selected)) { rates.Add(selected); rates.Sort(); }

            if (rates.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（未读取到可用刷新率）");
                empty.Enabled = false;
                parent.DropDownItems.Add(empty);
            }

            foreach (int value in rates)
            {
                int v = value;
                ToolStripMenuItem item = new ToolStripMenuItem(v + " Hz");
                item.Checked = (v == selected);
                item.Click += delegate { SetRate(ac, v); };
                parent.DropDownItems.Add(item);
            }

            parent.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem custom = new ToolStripMenuItem("手动输入…");
            custom.Click += delegate { PromptCustomRate(ac); };
            parent.DropDownItems.Add(custom);
        }

        private static readonly int[] PauseMinutes = { 15, 30, 60, 120, 240 };

        private void FillPauseMenu()
        {
            _miPause.DropDownItems.Clear();

            bool paused = _pausedUntil > DateTime.UtcNow;
            if (paused)
            {
                ToolStripMenuItem resume = new ToolStripMenuItem("立即恢复自动切换" + RemainingText());
                resume.Click += delegate
                {
                    _pausedUntil = DateTime.MinValue;
                    Log.Write("手动恢复自动切换");
                    UpdateIcon(true);
                    RefreshMenu();
                };
                _miPause.DropDownItems.Add(resume);
                _miPause.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (int minutes in PauseMinutes)
            {
                int m = minutes;
                string label = m < 60 ? (m + " 分钟") : ((m / 60) + " 小时");
                ToolStripMenuItem item = new ToolStripMenuItem("暂停 " + label);
                bool active = paused && _pausedUntil != DateTime.MaxValue
                    && Math.Abs((_pausedUntil - DateTime.UtcNow).TotalMinutes - m) < 0.6;
                item.Checked = active;
                item.Click += delegate
                {
                    _pausedUntil = DateTime.UtcNow.AddMinutes(m);
                    Log.Write("暂停自动切换 " + m + " 分钟");
                    UpdateIcon(true);
                    RefreshMenu();
                };
                _miPause.DropDownItems.Add(item);
            }

            _miPause.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem forever = new ToolStripMenuItem("暂停直到手动恢复");
            forever.Checked = (paused && _pausedUntil == DateTime.MaxValue);
            forever.Click += delegate
            {
                _pausedUntil = DateTime.MaxValue;
                Log.Write("暂停自动切换（直到手动恢复）");
                UpdateIcon(true);
                RefreshMenu();
            };
            _miPause.DropDownItems.Add(forever);
        }

        private string RemainingText()
        {
            if (_pausedUntil == DateTime.MaxValue) return "（手动恢复）";
            int minutes = (int)Math.Ceiling((_pausedUntil - DateTime.UtcNow).TotalMinutes);
            if (minutes < 1) minutes = 1;
            return minutes >= 60
                ? string.Format("（剩余 {0} 小时 {1} 分）", minutes / 60, minutes % 60)
                : string.Format("（剩余 {0} 分钟）", minutes);
        }

        private void FillStartupMenu()
        {
            _miStartup.DropDownItems.Clear();

            bool enabled = Startup.IsEnabled();

            ToolStripMenuItem on = new ToolStripMenuItem("开启（登录后自动运行）");
            on.Checked = enabled;
            on.Click += delegate
            {
                Startup.Set(true);
                Log.Write("开机自启动：开启");
                RefreshMenu();
            };

            ToolStripMenuItem off = new ToolStripMenuItem("关闭");
            off.Checked = !enabled;
            off.Click += delegate
            {
                Startup.Set(false);
                Log.Write("开机自启动：关闭");
                RefreshMenu();
            };

            _miStartup.DropDownItems.Add(on);
            _miStartup.DropDownItems.Add(off);
        }

        private void SetRate(bool forAc, int hz)
        {
            if (forAc) _cfg.AcHz = hz;
            else _cfg.BatteryHz = hz;
            _cfg.Save();
            Log.Write(string.Format("配置 {0} = {1}", forAc ? "适配器" : "电池", HzText(hz, forAc)));

            if (_cfg.Auto && ((forAc && _onAc) || (!forAc && !_onAc)))
                ApplyTarget(true);

            RefreshMenu();
        }

        private void PromptManualRate()
        {
            // 预填当前值；读不到就用主屏真实支持的最高档，绝不写死数字
            int initial = _currentHz > 0 ? _currentHz : SuggestedRate(true);
            int? hz = InputBox.Ask("立即切换", "输入刷新率（Hz，1-480）：", initial);
            if (!hz.HasValue) return;
            ApplyNow(hz.Value);
        }

        private void PromptCustomRate(bool forAc)
        {
            int initial = forAc ? _cfg.AcHz : _cfg.BatteryHz;
            if (initial <= 0) initial = SuggestedRate(forAc);
            int? hz = InputBox.Ask(forAc ? "适配器时的刷新率" : "电池时的刷新率",
                "输入刷新率（Hz，1-480）：不支持时自动退到最接近的档位", initial);
            if (!hz.HasValue) return;
            SetRate(forAc, hz.Value);
        }

        /// <summary>取主显示器真实支持的最高/最低档，作为输入框预填值。</summary>
        private int SuggestedRate(bool preferHighest)
        {
            Displays.DisplayInfo d = PrimaryInfo();
            if (d == null || d.Rates.Count == 0) return 0;
            return preferHighest ? d.Rates[d.Rates.Count - 1] : d.Rates[0];
        }

        // ---------------- 核心 ----------------

        /// <summary>某台显示器在当前电源下应该用的刷新率（0 表示自动）。</summary>
        private int TargetForPower(Displays.DisplayInfo d)
        {
            if (d == null) return 0;
            return Displays.ResolveTarget(d.Rates, _onAc ? _cfg.AcHz : _cfg.BatteryHz, _onAc);
        }

        private void ApplyTarget(bool manual)
        {
            if (_busy) return;

            if (!manual)
            {
                if (!_cfg.Auto) return;
                if (_pausedUntil > DateTime.UtcNow) return;
            }

            if (_displays.Count == 0 || (DateTime.UtcNow - _deviceResolvedAt).TotalSeconds > 30)
                RefreshDisplays();
            if (_displays.Count == 0) return;

            List<int> targets = new List<int>();
            bool pending = false;
            foreach (Displays.DisplayInfo d in _displays)
            {
                int target = TargetForPower(d);
                targets.Add(target);
                if (target > 0 && d.CurrentHz != target) pending = true;
            }

            if (!pending) { UpdateIcon(false); return; }

            ApplyToDisplays(targets, manual ? "手动" : "自动", manual);
        }

        /// <summary>「立即切换」子菜单：把所有显示器切到指定刷新率，不改写适配器/电池目标。</summary>
        private void ApplyNow(int hz)
        {
            if (_busy) return;
            if (_displays.Count == 0) RefreshDisplays();
            if (_displays.Count == 0) return;

            List<int> targets = new List<int>();
            foreach (Displays.DisplayInfo d in _displays)
                targets.Add(Displays.ResolveTarget(d.Rates, hz, true));

            ApplyToDisplays(targets, "立即切换", true);
        }

        private void ApplyToDisplays(List<int> targets, string reason, bool balloonOnError)
        {
            _busy = true;
            bool failed = false;
            try
            {
                for (int i = 0; i < _displays.Count && i < targets.Count; i++)
                {
                    Displays.DisplayInfo d = _displays[i];
                    int target = targets[i];
                    if (target <= 0) continue;
                    if (d.CurrentHz == target) continue;

                    int before = d.CurrentHz;
                    int actual;
                    int code = Displays.Apply(d.Device, target, out actual);
                    if (actual > 0) d.CurrentHz = actual;
                    Log.Write(string.Format("切换 {0} {1}Hz -> {2}Hz 结果={3}({4}) 实际={5}Hz（{6}）",
                        d.Label, before, target, code, Displays.DescribeResult(code), actual, reason));
                    if (code != 0) failed = true;
                }

                Displays.DisplayInfo primary = PrimaryInfo();
                if (primary != null && primary.CurrentHz > 0) _currentHz = primary.CurrentHz;
            }
            finally
            {
                _busy = false;
            }

            if (failed && balloonOnError)
            {
                _notify.ShowBalloonTip(4000, "刷新率切换失败",
                    "部分显示器切换失败，详情见日志 app.log", ToolTipIcon.Warning);
            }

            UpdateIcon(true);
            if (_menu.Visible) RefreshMenu();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                double gap = (now - _lastTick).TotalSeconds;
                bool resumed = gap > 10;
                _lastTick = now;

                bool? ac = PowerState.IsOnAc();
                bool powerChanged = false;
                if (ac.HasValue && ac.Value != _onAc)
                {
                    _onAc = ac.Value;
                    powerChanged = true;
                    Log.Write("电源变化 -> " + (_onAc ? "交流电源" : "电池"));
                }
                if (resumed)
                    Log.Write(string.Format("tick 间隔 {0:F0}s，视为睡眠唤醒后重新应用", gap));

                // 显示器插拔/改分辨率后支持的档位会变，每 30 秒重枚举一次
                if (_displays.Count == 0 || (now - _deviceResolvedAt).TotalSeconds > 30)
                    RefreshDisplays();

                Displays.DisplayInfo primary = PrimaryInfo();
                if (primary != null)
                {
                    int hz = Displays.CurrentFrequency(primary.Device);
                    if (hz > 0)
                    {
                        primary.CurrentHz = hz;
                        if (hz != _currentHz) _currentHz = hz;
                    }
                }

                if (powerChanged || resumed)
                    ApplyTarget(false);

                if (QueryIconSize() != _iconSize) _iconSize = QueryIconSize();

                UpdateIcon(false);
            }
            catch
            {
                // 托盘程序不因偶发异常退出
            }
        }

        private void UpdateIcon(bool force)
        {
            bool paused = _pausedUntil > DateTime.UtcNow;
            string label = _currentHz > 0 ? _currentHz.ToString() : "--";
            string key = string.Format("{0}|{1}|{2}|{3}", label, _onAc, paused, _iconSize);
            if (!force && key == _iconKey) return;
            _iconKey = key;

            Color color = paused ? PausedColor : (_onAc ? AcColor : BatteryColor);
            int size = _iconSize > 0 ? _iconSize : 16;

            Icon old = _notify.Icon;
            try
            {
                _notify.Icon = TrayIconFactory.Render(label, color, size);
            }
            catch (Exception ex)
            {
                // 图标画不出来也不能让托盘的其它功能（含自动切换）一起瘫掉
                Log.Write("警告：生成托盘图标失败：" + ex.Message);
            }
            if (!ReferenceEquals(old, _notify.Icon) && old != null) old.Dispose();

            string text = string.Format("{0} Hz · {1}{2}{3}",
                label, PowerState.Describe(_onAc),
                _displays.Count > 1 ? string.Format(" · {0} 个屏幕", _displays.Count) : "",
                paused ? " · 已暂停" : "");
            if (text.Length > 63) text = text.Substring(0, 63);
            if (_notify.Text != text) _notify.Text = text;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
                if (_notify != null)
                {
                    _notify.Visible = false;
                    if (_notify.Icon != null) _notify.Icon.Dispose();
                    _notify.Dispose();
                }
                if (_menu != null) _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    //  手动输入刷新率的小对话框
    // ------------------------------------------------------------------
    internal sealed class InputBox : Form
    {
        private readonly TextBox _box;

        private InputBox(string title, string prompt, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(330, 118);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label label = new Label();
            label.Text = prompt;
            label.SetBounds(12, 12, 306, 22);
            Controls.Add(label);

            _box = new TextBox();
            _box.Text = initial;
            _box.SetBounds(12, 38, 306, 24);
            Controls.Add(_box);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(162, 76, 75, 26);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(243, 76, 75, 26);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>弹出输入框；用户取消或输入非法时返回 null。</summary>
        public static int? Ask(string title, string prompt, int initial)
        {
            using (InputBox box = new InputBox(title, prompt, initial > 0 ? initial.ToString() : ""))
            {
                if (box.ShowDialog() != DialogResult.OK) return null;
                int value;
                if (!int.TryParse(box._box.Text.Trim(), out value)) return null;
                if (value < 1 || value > 480) return null;
                return value;
            }
        }
    }

    // ------------------------------------------------------------------
    //  命令行
    // ------------------------------------------------------------------
    internal sealed class Options
    {
        public bool Tray = true;
        public bool Status;
        public bool Test;
        public bool List;
        public int SetHz;
        public int? AcHz;
        public int? BatteryHz;
        public string OutPath;
        public string IconPreview;

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--status":
                    case "-s": o.Status = true; o.Tray = false; break;
                    case "--test": o.Test = true; o.Tray = false; break;
                    case "--list": o.List = true; o.Tray = false; break;
                    case "--tray": o.Tray = true; break;
                    case "--set":
                        o.SetHz = int.Parse(args[++i]);
                        o.Tray = false;
                        break;
                    case "--ac":
                        o.AcHz = int.Parse(args[++i]);
                        o.Tray = false;
                        break;
                    case "--battery":
                        o.BatteryHz = int.Parse(args[++i]);
                        o.Tray = false;
                        break;
                    case "--out":
                        o.OutPath = args[++i];
                        break;
                    case "--icon-preview":
                        o.IconPreview = args[++i];
                        o.Tray = false;
                        break;
                    case "--help":
                    case "-h":
                    case "-?":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default:
                        break;
                }
            }
            return o;
        }

        public static void PrintHelp()
        {
            string text =
                "RefreshRateTray\r\n" +
                "\r\n" +
                "  无参数                常驻托盘，按电源状态自动切换刷新率\r\n" +
                "  --status              输出所有显示器的刷新率与电源状态\r\n" +
                "  --list                输出所有显示器支持的刷新率\r\n" +
                "  --set <Hz>            立即把所有显示器切到指定刷新率\r\n" +
                "  --test                自检：切到最低档 -> 目标档 -> 恢复，输出结果\r\n" +
                "  --ac <Hz>             设定适配器时的刷新率（0 = 自动取最高档）\r\n" +
                "  --battery <Hz>        设定电池时的刷新率（0 = 自动取最低档）\r\n" +
                "  --out <file>          把命令行输出同时写入文件\r\n" +
                "  --icon-preview <png>  把托盘图标渲染成预览图（排查图标问题用）\r\n";
            Console.WriteLine(text);
        }
    }

    internal static class Program
    {
        public static bool IsConsoleBuild = false;
        private static string _outPath;

        [STAThread]
        private static int Main(string[] args)
        {
#if CLI_BUILD
            IsConsoleBuild = true;
#endif
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("未处理异常: " + e.ExceptionObject);
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Write("界面线程异常: " + e.Exception);
            };
            Options opts;
            try
            {
                opts = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Say("参数错误：" + ex.Message);
                return 2;
            }

            _outPath = opts.OutPath;

            if (!opts.Tray)
            {
                if (!IsConsoleBuild) ConsoleHelper.Attach();

                if (opts.AcHz.HasValue || opts.BatteryHz.HasValue)
                {
                    Settings s = Settings.Load();
                    if (opts.AcHz.HasValue) s.AcHz = opts.AcHz.Value;
                    if (opts.BatteryHz.HasValue) s.BatteryHz = opts.BatteryHz.Value;
                    s.Save();
                    Say(string.Format("已保存：适配器 {0} / 电池 {1}", HzText(s.AcHz, true), HzText(s.BatteryHz, false)));
                }

                if (opts.List)
                {
                    List<Displays.DisplayInfo> displays = Displays.EnumerateActive();
                    if (displays.Count == 0) Say("没有枚举到显示器");
                    foreach (Displays.DisplayInfo d in displays)
                    {
                        Say(string.Format("{0}  当前 {1} Hz  支持 {2} Hz",
                            d.Label, d.CurrentHz, string.Join(", ", d.Rates.ConvertAll(x => x.ToString()).ToArray())));
                    }
                    return 0;
                }

                if (opts.Status)
                {
                    Say(StatusText());
                    return 0;
                }

                if (opts.IconPreview != null)
                {
                    return DoIconPreview(opts.IconPreview);
                }

                if (opts.SetHz > 0)
                {
                    return DoSet(opts.SetHz);
                }

                if (opts.Test)
                {
                    return DoTest();
                }

                // 只改配置（--ac / --battery）也算成功
                bool onlyConfig = opts.AcHz.HasValue || opts.BatteryHz.HasValue;
                if (onlyConfig && !opts.Status && !opts.List && !opts.Test && opts.SetHz <= 0 && opts.IconPreview == null)
                    return 0;

                return 1;
            }

            // ---- 托盘模式 ----
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\RefreshRateTray.SingleInstance", out created))
            {
                if (!created) return 0;

                Log.Write("===== 托盘模式启动 pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                    " 版本=" + (AppPaths.Pocket ? "便携版" : "标准版") +
                    " 目录=" + AppPaths.BaseDir + " =====");

                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Settings cfg = Settings.Load();
                if (opts.AcHz.HasValue) cfg.AcHz = opts.AcHz.Value;
                if (opts.BatteryHz.HasValue) cfg.BatteryHz = opts.BatteryHz.Value;

                TrayApp app = new TrayApp(cfg);
                Application.Run(app);

                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>配置值的可读形式：0 表示「自动」——插电取最高、电池取最低。</summary>
        private static string HzText(int configured, bool preferHighest)
        {
            if (configured > 0) return configured + "Hz";
            return preferHighest ? "自动(最高)" : "自动(最低)";
        }

        private static string StatusText()
        {
            bool? ac = PowerState.IsOnAc();
            Settings s = Settings.Load();
            bool preferHighest = ac != false;
            int desired = ac == false ? s.BatteryHz : s.AcHz;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("电源: " + (ac.HasValue ? (ac.Value ? "交流电源" : "电池") : "未知"));
            sb.AppendLine("目标: " + HzText(desired, preferHighest));
            sb.AppendLine("自动切换: " + (s.Auto ? "开启" : "关闭"));
            sb.AppendLine("版本: " + (AppPaths.Pocket ? "便携版" : "标准版") + "（设置：" +
                (AppPaths.Pocket ? "同目录 settings.ini" : @"HKCU\Software\RefreshRateTray") + "）");

            List<Displays.DisplayInfo> displays = Displays.EnumerateActive();
            if (displays.Count == 0) sb.AppendLine("没有枚举到显示器");
            foreach (Displays.DisplayInfo d in displays)
            {
                int target = Displays.ResolveTarget(d.Rates, desired, preferHighest);
                sb.AppendLine(string.Format("{0}: 当前 {1} Hz -> 目标 {2} Hz，支持 {3} Hz",
                    d.Label, d.CurrentHz, target,
                    string.Join(", ", d.Rates.ConvertAll(x => x.ToString()).ToArray())));
            }

            return sb.ToString().TrimEnd();
        }

        private static int DoIconPreview(string path)
        {
            try
            {
                // 用真实当前刷新率当样例，避免预览图里出现写死的数字
                string label = "--";
                List<Displays.DisplayInfo> displays = Displays.EnumerateActive();
                if (displays.Count > 0)
                {
                    Displays.DisplayInfo primary = null;
                    foreach (Displays.DisplayInfo d in displays)
                        if (d.Primary) primary = d;
                    if (primary == null) primary = displays[0];
                    if (primary.CurrentHz > 0) label = primary.CurrentHz.ToString();
                }
                TrayIconFactory.SavePreview(path, Color.FromArgb(74, 222, 128), label);
                Say("图标预览已写入: " + path);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("图标预览失败: " + ex);
                Say("图标预览失败: " + ex.Message);
                return 2;
            }
        }

        private static int DoSet(int hz)
        {
            List<Displays.DisplayInfo> displays = Displays.EnumerateActive();
            if (displays.Count == 0)
            {
                Say("找不到显示器");
                return 2;
            }

            int okCount = 0;
            int firstBad = -1;
            string firstBadReason = null;
            foreach (Displays.DisplayInfo d in displays)
            {
                int target = Displays.ResolveTarget(d.Rates, hz, true);
                int actual;
                int code = Displays.Apply(d.Device, target, out actual);
                Log.Write(string.Format("CLI --set {0} @ {1}: 目标={2} 结果={3}({4}) 实际={5}Hz",
                    hz, d.Label, target, code, Displays.DescribeResult(code), actual));
                Say(string.Format("{0}: 目标 {1} Hz -> 实际 {2} Hz{3}",
                    d.Label, target, actual,
                    code == 0 ? "" : "（" + Displays.DescribeResult(code) + "）"));
                if (code == 0) okCount++;
                else if (firstBad < 0) { firstBad = code; firstBadReason = Displays.DescribeResult(code); }
            }

            if (firstBad < 0)
            {
                Say(string.Format("已切换到 {0} Hz（{1} 台显示器）", hz, okCount));
                return 0;
            }

            Say(string.Format("部分显示器切换失败：{0}", firstBadReason));
            return 2;
        }

        private static int DoTest()
        {
            string dev = Displays.PrimaryDeviceName();
            if (dev == null) { Say("找不到主显示器"); return 2; }

            List<int> rates = Displays.SupportedFrequencies(dev);
            Say("主显示器: " + dev);
            Say("支持刷新率: " + string.Join(", ", rates.ConvertAll(x => x.ToString()).ToArray()));

            int original = Displays.CurrentFrequency(dev);
            Say("当前刷新率: " + original + " Hz");

            bool ok = true;
            // 只用这台显示器真实支持的档位做探测：最低档 <-> 最高档
            List<int> probes = new List<int>();
            if (rates.Count > 0) probes.Add(rates[0]);
            if (rates.Count > 1) probes.Add(rates[rates.Count - 1]);
            foreach (int hz in probes)
            {
                if (!rates.Contains(hz))
                {
                    Say(string.Format("跳过 {0} Hz（不支持）", hz));
                    continue;
                }
                int actual;
                int code = Displays.Apply(dev, hz, out actual);
                bool pass = (code == 0 && actual == hz);
                ok = ok && pass;
                Say(string.Format("切换到 {0} Hz -> {1}（实际 {2} Hz）", hz, pass ? "通过" : "失败", actual));
            }

            if (original > 0)
            {
                int actual2;
                int code2 = Displays.Apply(dev, original, out actual2);
                Say(string.Format("恢复到 {0} Hz -> {1}（实际 {2} Hz）", original, code2 == 0 ? "通过" : "失败", actual2));
            }

            Say(ok ? "自检结果: 通过" : "自检结果: 存在失败项");
            return ok ? 0 : 2;
        }

        private static void Say(string message)
        {
            if (IsConsoleBuild)
            {
                Console.WriteLine(message);
                return;
            }

            if (_outPath != null)
            {
                try
                {
                    File.WriteAllText(_outPath, message + Environment.NewLine);
                }
                catch { }
            }

            try { Console.WriteLine(message); Console.Out.Flush(); }
            catch { }

            if (!IsConsoleBuild && !ConsoleHelper.HasConsole && _outPath == null)
            {
                MessageBox.Show(message, "RefreshRateTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    internal static class ConsoleHelper
    {
        public static bool HasConsole;

        public static void Attach()
        {
            if (Native.AttachConsole(Native.ATTACH_PARENT_PROCESS))
            {
                HasConsole = true;
                StreamWriter writer = new StreamWriter(Console.OpenStandardOutput());
                writer.AutoFlush = true;
                Console.SetOut(writer);
            }
        }
    }
}