using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BuckshotOverlay
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
                File.AppendAllText(p, $"[{DateTime.Now}] START OF MAIN\r\n");
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Generate BuckshotHook.dll in current directory on startup
            try
            {
                DllGenerator.EnsureDllExists(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch
            {
                // Ignore if in read-only environment
            }

            // Command-line injection argument support: --inject
            if (args.Length > 0 && args[0].Equals("--inject", StringComparison.OrdinalIgnoreCase))
            {
                var target = InjectorEngine.FindBuckshotProcess();
                if (target != null)
                {
                    string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BuckshotHook.dll");
                    InjectorEngine.InjectDll(target, dllPath, Console.WriteLine);
                }
                return;
            }

            // Command-line memory diagnostic: --scan-dump (writes scan_dump.txt next to the exe)
            if (args.Length > 0 && args[0].Equals("--scan-dump", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder();
                var target = InjectorEngine.FindBuckshotProcess();
                if (target == null)
                {
                    sb.AppendLine("Buckshot Roulette is not running.");
                }
                else
                {
                    var cands = MemoryScanner.ScanAllCandidates(target);
                    sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] PID {target.Id}: {cands.Count} candidate shell array(s)");
                    var holders = MemoryScanner.FindPointerHolders(target, cands.Select(c => c.Result.FoundAddress).ToList());
                    foreach (var c in cands.OrderBy(c => c.Result.FoundAddress))
                    {
                        sb.AppendLine($"  0x{c.Result.FoundAddress:X} stride={c.Stride} ptrOff={c.PtrOffset} {c.Result.LiveCount}L/{c.Result.BlankCount}B [{string.Join(",", c.Result.Sequence)}]");
                        if (!holders.TryGetValue(c.Result.FoundAddress, out var addrs)) continue;
                        foreach (var ha in addrs)
                        {
                            var st = MemoryScanner.ReadViaHolder(target, ha, c.Stride, c.PtrOffset, out var r);
                            sb.AppendLine($"      holder 0x{ha:X} -> {st}{(r != null ? $" [{string.Join(",", r.Sequence)}]" : "")}");
                        }
                    }
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan_dump.txt"), sb.ToString());
                return;
            }

            // Command-line render test argument: --render-test
            if (args.Length > 0 && args[0].Equals("--render-test", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var form = new OverlayForm();
                    form.SetClickThrough(true);
                    using var bmpDefault = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmpDefault, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    string defPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rendered_hud_default.png");
                    bmpDefault.Save(defPath, System.Drawing.Imaging.ImageFormat.Png);

                    form.SetRound(2, 2);
                    using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rendered_hud.png");
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log"), $"[{DateTime.Now}] Rendered {outPath} and {defPath} successfully\r\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log"), $"[{DateTime.Now}] Render error: {ex}\r\n");
                }
                return;
            }

            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] Program.Main started. BaseDir={AppDomain.CurrentDomain.BaseDirectory}\r\n");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                string msg = ev.ExceptionObject?.ToString() ?? "Unknown exception";
                File.AppendAllText(logPath, $"[{DateTime.Now}] Unhandled AppDomain exception:\r\n{msg}\r\n\r\n");
                MessageBox.Show($"Unhandled Error:\n{msg}", "Buckshot Overlay Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.ThreadException += (s, ev) =>
            {
                string msg = ev.Exception?.ToString() ?? "Unknown thread exception";
                File.AppendAllText(logPath, $"[{DateTime.Now}] Thread exception:\r\n{msg}\r\n\r\n");
                MessageBox.Show($"Application Error:\n{msg}", "Buckshot Overlay Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] Calling Application.Run(new MainWindow())...\r\n");
                Application.Run(new MainWindow());
                File.AppendAllText(logPath, $"[{DateTime.Now}] Application.Run returned normally.\r\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] Fatal exception in Application.Run:\r\n{ex}\r\n\r\n");
                MessageBox.Show($"Startup Error:\n{ex}", "Buckshot Overlay Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
