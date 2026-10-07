using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BuckshotOverlay
{
    public class MainWindow : Form
    {
        private Process? _buckshotProcess;
        private OverlayForm? _overlay;
        private AutoTrackerEngine? _autoTracker;
        private GlobalKeyboardHook? _keyboardHook;
        private System.Windows.Forms.Timer? _processPollTimer;

        // UI Controls
        private Label _lblGameStatus = null!;
        private Label _lblProcessInfo = null!;
        private Button _btnInject = null!;
        private Button _btnLaunchOverlay = null!;
        private Button _btnToggleClickThrough = null!;
        private Button _btnResetRound = null!;
        private TextBox _txtLog = null!;
        private CheckBox _chkAutoScan = null!;
        private CheckBox _chkSound = null!;


        // Colors
        private readonly Color _colorBg = Color.FromArgb(24, 27, 33);
        private readonly Color _colorPanel = Color.FromArgb(32, 36, 45);
        private readonly Color _colorLive = Color.FromArgb(235, 60, 70);
        private readonly Color _colorBlank = Color.FromArgb(50, 180, 245);
        private readonly Color _colorAccent = Color.FromArgb(255, 195, 50);
        private readonly Color _colorText = Color.FromArgb(240, 240, 245);
        private readonly Color _colorDim = Color.FromArgb(160, 165, 180);

        public MainWindow()
        {
            InitializeComponent();
            FormClosing += (s, e) =>
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log"), $"[{DateTime.Now}] MainWindow FormClosing: CloseReason={e.CloseReason}, Cancel={e.Cancel}\r\n");
            };
            FormClosed += (s, e) =>
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log"), $"[{DateTime.Now}] MainWindow FormClosed: CloseReason={e.CloseReason}\r\n");
            };
            SetupKeyboardHook();
            StartGamePolling();
        }

        private void InitializeComponent()
        {
            Text = "Buckshot Roulette // Tactical Overlay & Injector";
            ClientSize = new Size(720, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = _colorBg;
            ForeColor = _colorText;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // --- HEADER TITLE PANEL ---
            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                BackColor = Color.FromArgb(18, 20, 25),
                Padding = new Padding(20, 10, 20, 10)
            };
            Label lblTitle = new Label
            {
                Text = "BUCKSHOT ROULETTE TACTICAL ASSISTANT",
                Font = new Font("Consolas", 15f, FontStyle.Bold),
                ForeColor = _colorAccent,
                AutoSize = true,
                Location = new Point(18, 12)
            };
            Label lblSubtitle = new Label
            {
                Text = "Singleplayer Shell Tracking Overlay & DLL Injection System",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = _colorDim,
                AutoSize = true,
                Location = new Point(20, 38)
            };
            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(lblSubtitle);
            Controls.Add(headerPanel);

            // --- MAIN CONTENT CONTAINER ---
            int y = 78;

            // 1. GAME STATUS GROUP
            GroupBox grpStatus = CreateGroupBox(" GAME PROCESS MONITOR ", 20, y, 680, 85);
            _lblGameStatus = new Label
            {
                Text = "SEARCHING FOR BUCKSHOT ROULETTE.EXE...",
                Font = new Font("Consolas", 10.5f, FontStyle.Bold),
                ForeColor = Color.Orange,
                Location = new Point(16, 26),
                AutoSize = true
            };
            _lblProcessInfo = new Label
            {
                Text = "PID: -  |  Window: -  |  Architecture: x64",
                Font = new Font("Consolas", 9f, FontStyle.Regular),
                ForeColor = _colorDim,
                Location = new Point(16, 52),
                AutoSize = true
            };
            grpStatus.Controls.Add(_lblGameStatus);
            grpStatus.Controls.Add(_lblProcessInfo);
            Controls.Add(grpStatus);

            y += 95;

            // 2. INJECTOR & OVERLAY CONTROLS GROUP
            GroupBox grpInject = CreateGroupBox(" INJECTION & OVERLAY CONTROL ", 20, y, 680, 115);

            _btnInject = CreateStyledButton("INJECT DLL INTO GAME", 16, 26, 210, 40, Color.FromArgb(180, 40, 50));
            _btnInject.Click += OnInjectClicked;

            _btnLaunchOverlay = CreateStyledButton("SHOW OVERLAY", 236, 26, 170, 40, Color.FromArgb(40, 120, 190));
            _btnLaunchOverlay.Click += OnToggleOverlayClicked;

            _btnToggleClickThrough = CreateStyledButton("CLICK-THROUGH (F4)", 416, 26, 170, 40, Color.FromArgb(50, 60, 75));
            _btnToggleClickThrough.Click += (_, _) => _overlay?.ToggleClickThrough();

            _btnResetRound = CreateStyledButton("RESET (F8)", 596, 26, 70, 40, Color.FromArgb(70, 75, 90));
            _btnResetRound.Click += (_, _) =>
            {
                _autoTracker?.ResetRoundState();
                _overlay?.ResetCurrentRound();
            };

            _chkAutoScan = new CheckBox
            {
                Text = "Automatic Round & Shot Tracking (Zero Input)",
                Location = new Point(18, 76),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.LightGreen,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _chkAutoScan.CheckedChanged += OnAutoScanChanged;

            _chkSound = new CheckBox
            {
                Text = "Audio Cue on Fired Shells",
                Location = new Point(418, 76),
                AutoSize = true,
                Checked = true,
                ForeColor = _colorDim
            };
            _chkSound.CheckedChanged += (_, _) => SoundPlayerHelper.SoundEnabled = _chkSound.Checked;

            grpInject.Controls.Add(_btnInject);
            grpInject.Controls.Add(_btnLaunchOverlay);
            grpInject.Controls.Add(_btnToggleClickThrough);
            grpInject.Controls.Add(_btnResetRound);
            grpInject.Controls.Add(_chkAutoScan);
            grpInject.Controls.Add(_chkSound);
            Controls.Add(grpInject);

            y += 125;

            // 3. DIAGNOSTIC INJECTION LOG
            GroupBox grpLog = CreateGroupBox(" DIAGNOSTIC & INJECTION LOG ", 20, y, 680, 235);
            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(16, 18, 22),
                ForeColor = Color.LightGreen,
                Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                Location = new Point(14, 24),
                Size = new Size(652, 195),
                BorderStyle = BorderStyle.None
            };
            grpLog.Controls.Add(_txtLog);
            Controls.Add(grpLog);

            // Log startup
            Log("Buckshot Roulette Tactical Overlay initialized.");
            Log("Global hotkeys active: F4 = Click-Through, F8 = Reset, F9 = Hide/Show HUD.");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            EnsureOverlayCreated();
            if (_autoTracker == null && _overlay != null)
            {
                _autoTracker = new AutoTrackerEngine(_overlay, msg => Log(msg));
                if (_buckshotProcess != null && _chkAutoScan.Checked)
                {
                    IntPtr hWnd = _buckshotProcess.MainWindowHandle != IntPtr.Zero ? _buckshotProcess.MainWindowHandle : InjectorEngine.FindProcessMainWindow(_buckshotProcess.Id);
                    _autoTracker.Start(_buckshotProcess, hWnd);
                }
            }
            Activate();
            BringToFront();
        }

        private void EnsureOverlayCreated()
        {
            if (_overlay == null || _overlay.IsDisposed)
            {
                _overlay = new OverlayForm();
                _overlay.TargetProcess = _buckshotProcess;
                _overlay.RoundResetRequested += () => _autoTracker?.ResetRoundState();
                _overlay.Show();
            }
        }

        private void SetupKeyboardHook()
        {
            try
            {
                _keyboardHook = new GlobalKeyboardHook();
                _keyboardHook.KeyPressed += key =>
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            EnsureOverlayCreated();
                            if (key == Keys.F4)
                            {
                                _overlay?.ToggleClickThrough();
                            }
                            else if (key == Keys.F8)
                            {
                                _autoTracker?.ResetRoundState();
                                _overlay?.ResetCurrentRound();
                            }
                            else if (key == Keys.F9)
                            {
                                if (_overlay != null)
                                {
                                    if (_overlay.Visible) _overlay.Hide();
                                    else _overlay.Show();
                                }
                            }
                        }));
                    }
                };
            }
            catch (Exception ex)
            {
                Log($"[WARNING] Could not initialize global keyboard hook: {ex.Message}");
            }
        }

        private void StartGamePolling()
        {
            _processPollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _processPollTimer.Tick += (_, _) =>
            {
                try
                {
                    Process? p = InjectorEngine.FindBuckshotProcess();
                    bool isAlive = InjectorEngine.IsProcessAlive(p);

                    if (p != null && isAlive && (p.Id != _buckshotProcess?.Id || _buckshotProcess == null))
                    {
                        _buckshotProcess = p;
                        _lblGameStatus.Text = $"BUCKSHOT ROULETTE RUNNING! (PID: {p.Id})";
                        _lblGameStatus.ForeColor = Color.LightGreen;
                        IntPtr hWnd = p.MainWindowHandle != IntPtr.Zero ? p.MainWindowHandle : InjectorEngine.FindProcessMainWindow(p.Id);
                        _lblProcessInfo.Text = $"PID: {p.Id}  |  Process: {p.ProcessName}  |  Window: 0x{hWnd.ToInt64():X}";
                        EnsureOverlayCreated();
                        if (_overlay != null)
                        {
                            _overlay.TargetProcess = p;
                        }
                        if (_chkAutoScan.Checked && _autoTracker != null && !_autoTracker.IsRunning)
                        {
                            _autoTracker.Start(p, hWnd);
                        }
                    }
                    else if ((p == null || !isAlive) && _buckshotProcess != null)
                    {
                        _autoTracker?.Stop();
                        _autoTracker?.ResetRoundState();
                        _buckshotProcess = null;
                        _lblGameStatus.Text = "WAITING FOR BUCKSHOT ROULETTE TO LAUNCH...";
                        _lblGameStatus.ForeColor = Color.Orange;
                        _lblProcessInfo.Text = "PID: -  |  Window: -  |  Architecture: x64";
                        _overlay?.SetRound(0, 0);
                    }
                }
                catch
                {
                    // Silently ignore polling exceptions
                }
            };
            _processPollTimer.Start();
        }

        private void OnAutoScanChanged(object? sender, EventArgs e)
        {
            if (_chkAutoScan.Checked)
            {
                _chkAutoScan.ForeColor = Color.LightGreen;
                if (_overlay != null) _overlay.AutoTrackingActive = true;
                if (_buckshotProcess != null && !_buckshotProcess.HasExited)
                {
                    IntPtr hWnd = _buckshotProcess.MainWindowHandle != IntPtr.Zero ? _buckshotProcess.MainWindowHandle : InjectorEngine.FindProcessMainWindow(_buckshotProcess.Id);
                    _autoTracker?.Start(_buckshotProcess, hWnd);
                }
                Log("[AUTO-TRACK] Automatic tracker activated (dialogue OCR + memory & flash detection).");
            }
            else
            {
                _chkAutoScan.ForeColor = _colorDim;
                if (_overlay != null) _overlay.AutoTrackingActive = false;
                _autoTracker?.Stop();
                Log("[AUTO-TRACK] Automatic tracker deactivated (manual hotkey mode).");
            }
        }


        private void OnInjectClicked(object? sender, EventArgs e)
        {
            if (_buckshotProcess == null || _buckshotProcess.HasExited)
            {
                _buckshotProcess = InjectorEngine.FindBuckshotProcess();
            }

            if (_buckshotProcess == null || _buckshotProcess.HasExited)
            {
                MessageBox.Show("Buckshot Roulette is not running!\nPlease launch Buckshot Roulette and try again.", "Process Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log("[INJECT] Target process Buckshot Roulette.exe is not running.");
                return;
            }

            try
            {
                Log($"[INJECT] Target detected: {_buckshotProcess.ProcessName} (PID: {_buckshotProcess.Id})");

                // Ensure BuckshotHook.dll exists in app directory
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllPath = DllGenerator.EnsureDllExists(appDir);
                Log($"[INJECT] Native DLL prepared: {dllPath}");

                // Perform injection
                var result = InjectorEngine.InjectDll(_buckshotProcess, dllPath, msg => Log(msg));
                if (result.Success)
                {
                    MessageBox.Show(result.Message, "Injection Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    EnsureOverlayCreated();
                }
                else
                {
                    MessageBox.Show(result.Message, "Injection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log($"[INJECT ERROR] Exception during injection: {ex.Message}");
                MessageBox.Show($"Injection error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnToggleOverlayClicked(object? sender, EventArgs e)
        {
            EnsureOverlayCreated();
            if (_overlay != null)
            {
                if (!_overlay.Visible)
                {
                    _overlay.Show();
                    _btnLaunchOverlay.Text = "HIDE OVERLAY";
                }
                else
                {
                    _overlay.Hide();
                    _btnLaunchOverlay.Text = "SHOW OVERLAY";
                }
            }
        }

        public void Log(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Log(msg)));
                return;
            }
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _txtLog.AppendText($"[{timestamp}] {msg}\r\n");
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }

        private GroupBox CreateGroupBox(string title, int x, int y, int w, int h)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(x, y),
                Size = new Size(w, h),
                ForeColor = _colorAccent,
                BackColor = _colorPanel,
                Font = new Font("Consolas", 8.5f, FontStyle.Bold)
            };
        }

        private Button CreateStyledButton(string text, int x, int y, int w, int h, Color bgColor)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bgColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }


        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _autoTracker?.Dispose();
                _processPollTimer?.Dispose();
                _keyboardHook?.Dispose();
                _overlay?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
