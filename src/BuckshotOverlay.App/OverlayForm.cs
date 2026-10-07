using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BuckshotOverlay
{
    public class OverlayForm : Form
    {
        // Win32 APIs for Window Style & Click-Through
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // Game State Variables
        public int LiveCount { get; private set; } = 0;
        public int BlankCount { get; private set; } = 0;
        public int InitialLive { get; private set; } = 0;
        public int InitialBlank { get; private set; } = 0;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsClickThrough { get; private set; } = false;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool AutoAttach { get; set; } = true;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool AutoTrackingActive { get; set; } = true;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Process? TargetProcess { get; set; }

        public event Action? RoundResetRequested;

        private readonly System.Windows.Forms.Timer _syncTimer;
        private Point _dragStartPoint;
        private bool _isDragging = false;

        // Visual Colors (Buckshot Roulette CRT Cyberpunk Industrial Theme)
        private readonly Color _colorBg = Color.FromArgb(16, 18, 24);
        private readonly Color _colorHeader = Color.FromArgb(24, 28, 36);
        private readonly Color _colorBorder = Color.FromArgb(60, 65, 75);
        private readonly Color _colorLive = Color.FromArgb(235, 45, 55);
        private readonly Color _colorLiveBg = Color.FromArgb(200, 30, 40);
        private readonly Color _colorBlank = Color.FromArgb(45, 175, 245);
        private readonly Color _colorBlankBg = Color.FromArgb(20, 130, 210);
        private readonly Color _colorText = Color.FromArgb(240, 240, 245);
        private readonly Color _colorDimText = Color.FromArgb(160, 165, 180);
        private readonly Color _colorAccent = Color.FromArgb(255, 200, 50);

        // Clickable Button Regions for Interactive Mode
        private Rectangle _btnLiveMinus;
        private Rectangle _btnLivePlus;
        private Rectangle _btnBlankMinus;
        private Rectangle _btnBlankPlus;
        private Rectangle _btnToggleClickThrough;
        private Rectangle _btnReset;
        private Rectangle _btnClose;

        public OverlayForm()
        {
            Text = "Buckshot Overlay HUD";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Opacity = 0.94;
            Width = 440;
            Height = 282;
            BackColor = _colorBg;

            // Set initial screen position (top right corner with padding)
            Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(screen.Right - Width - 30, screen.Top + 40);

            _syncTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _syncTimer.Tick += OnSyncTimerTick;
            _syncTimer.Start();

            MouseDown += OnMouseDownHandler;
            MouseMove += OnMouseMoveHandler;
            MouseUp += OnMouseUpHandler;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateWindowStyles();
        }

        public void SetClickThrough(bool enabled)
        {
            IsClickThrough = enabled;
            UpdateWindowStyles();
            SoundPlayerHelper.PlayToggle();
            Invalidate();
        }

        public void ToggleClickThrough()
        {
            SetClickThrough(!IsClickThrough);
        }

        private void UpdateWindowStyles()
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            exStyle |= WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW;

            if (IsClickThrough)
            {
                exStyle |= WS_EX_TRANSPARENT;
            }
            else
            {
                exStyle &= ~WS_EX_TRANSPARENT;
            }

            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void SetRound(int live, int blank)
        {
            InitialLive = Math.Max(0, live);
            InitialBlank = Math.Max(0, blank);
            LiveCount = InitialLive;
            BlankCount = InitialBlank;
            SoundPlayerHelper.PlayReload();
            Invalidate();
        }

        public void ResetCurrentRound()
        {
            LiveCount = InitialLive;
            BlankCount = InitialBlank;
            MemoryScanner.ClearLock();
            SoundPlayerHelper.PlayReload();
            RoundResetRequested?.Invoke();
            Invalidate();
        }

        public void RegisterLiveFired()
        {
            if (LiveCount > 0)
            {
                LiveCount--;
                SoundPlayerHelper.PlayLiveFired();
                Invalidate();
            }
        }

        public void RegisterBlankFired()
        {
            if (BlankCount > 0)
            {
                BlankCount--;
                SoundPlayerHelper.PlayBlankFired();
                Invalidate();
            }
        }

        public void AdjustLive(int delta)
        {
            int newCount = Math.Max(0, Math.Min(8, LiveCount + delta));
            if (newCount != LiveCount)
            {
                LiveCount = newCount;
                Invalidate();
            }
        }

        public void AdjustBlank(int delta)
        {
            int newCount = Math.Max(0, Math.Min(8, BlankCount + delta));
            if (newCount != BlankCount)
            {
                BlankCount = newCount;
                Invalidate();
            }
        }

        public void UpdateCurrentCounts(int live, int blank)
        {
            LiveCount = Math.Max(0, live);
            BlankCount = Math.Max(0, blank);
            Invalidate();
        }

        private void OnSyncTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (AutoAttach && TargetProcess != null && InjectorEngine.IsProcessAlive(TargetProcess))
                {
                    IntPtr hWnd = TargetProcess.MainWindowHandle;
                    if (hWnd == IntPtr.Zero)
                    {
                        hWnd = InjectorEngine.FindProcessMainWindow(TargetProcess.Id);
                    }
                    if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect))
                    {
                        int gameWidth = rect.Right - rect.Left;
                        int gameHeight = rect.Bottom - rect.Top;

                        if (gameWidth > 200 && gameHeight > 200)
                        {
                            // Anchor overlay at the top-right inside game window border
                            int targetX = rect.Right - Width - 20;
                            int targetY = rect.Top + 35;

                            if (Math.Abs(Location.X - targetX) > 2 || Math.Abs(Location.Y - targetY) > 2)
                            {
                                Location = new Point(targetX, targetY);
                            }
                        }
                    }
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    // Ensure window stays topmost over the game
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            catch
            {
                // Silently ignore sync errors during window repositioning
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Background & Border
            using (var brush = new SolidBrush(_colorBg))
                g.FillRectangle(brush, ClientRectangle);

            using (var pen = new Pen(_colorBorder, 1.5f))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            // --- HEADER BAR ---
            int headerHeight = 44;
            using (var headerBrush = new SolidBrush(_colorHeader))
                g.FillRectangle(headerBrush, 0, 0, Width, headerHeight);

            using (var headerLine = new Pen(_colorBorder, 1f))
                g.DrawLine(headerLine, 0, headerHeight, Width, headerHeight);

            // Title & Status Indicators
            using (var fontTitle = new Font("Consolas", 10.5f, FontStyle.Bold))
            using (var fontSub = new Font("Consolas", 8.5f, FontStyle.Regular))
            using (var fontStatus = new Font("Consolas", 8.5f, FontStyle.Bold))
            using (var textBrush = new SolidBrush(_colorText))
            using (var dimBrush = new SolidBrush(_colorDimText))
            {
                // Top Row: Title & Subtitle
                g.DrawString("BUCKSHOT ROULETTE", fontTitle, textBrush, 12, 6);
                g.DrawString("// OVERLAY", fontSub, dimBrush, 175, 8);

                // Row 2: Status Indicators (moved down so they do not overlap top text)
                string autoStatus = AutoTrackingActive ? "[AUTO-TRACK]" : "[MANUAL]";
                Color autoColor = AutoTrackingActive ? Color.Cyan : _colorDimText;

                string ctStatus = IsClickThrough ? "[LOCKED: F4]" : "[INTERACTIVE]";
                Color ctColor = IsClickThrough ? _colorAccent : Color.LightGreen;

                int statusY = 24;
                float ctWidth = g.MeasureString(ctStatus, fontStatus).Width;
                float autoWidth = g.MeasureString(autoStatus, fontStatus).Width;

                float ctX = Width - 14 - ctWidth;
                float autoX = ctX - 8 - autoWidth;

                using (var autoBrush = new SolidBrush(autoColor))
                    g.DrawString(autoStatus, fontStatus, autoBrush, autoX, statusY);

                using (var ctBrush = new SolidBrush(ctColor))
                    g.DrawString(ctStatus, fontStatus, ctBrush, ctX, statusY);
            }

            // Close button (X) on header
            _btnClose = new Rectangle(Width - 28, 6, 20, 20);
            using (var pen = new Pen(_colorDimText, 1.5f))
            {
                g.DrawLine(pen, _btnClose.Left + 4, _btnClose.Top + 4, _btnClose.Right - 4, _btnClose.Bottom - 4);
                g.DrawLine(pen, _btnClose.Right - 4, _btnClose.Top + 4, _btnClose.Left + 4, _btnClose.Bottom - 4);
            }

            // --- ROUND COUNTER BOXES (LIVE vs BLANK) ---
            int boxY = 54;
            int boxWidth = (Width - 36) / 2;
            int boxHeight = 120;

            // LIVE BOX
            Rectangle liveBox = new Rectangle(12, boxY, boxWidth, boxHeight);
            DrawShellBox(g, liveBox, "LIVE ROUNDS", LiveCount, _colorLive, _colorLiveBg, out _btnLiveMinus, out _btnLivePlus);

            // BLANK BOX
            Rectangle blankBox = new Rectangle(Width / 2 + 6, boxY, boxWidth, boxHeight);
            DrawShellBox(g, blankBox, "BLANK ROUNDS", BlankCount, _colorBlank, _colorBlankBg, out _btnBlankMinus, out _btnBlankPlus);

            // --- PROBABILITY BAR & STATS ---
            int total = LiveCount + BlankCount;
            float livePct = total > 0 ? (float)LiveCount / total * 100f : 0f;
            float blankPct = total > 0 ? (float)BlankCount / total * 100f : 0f;

            int probY = boxY + boxHeight + 12;
            Rectangle probBarRect = new Rectangle(12, probY, Width - 24, 18);

            // Dual-color progress bar
            using (var bgBrush = new SolidBrush(Color.FromArgb(40, 45, 55)))
                g.FillRectangle(bgBrush, probBarRect);

            if (total > 0)
            {
                int liveWidth = (int)(probBarRect.Width * (livePct / 100f));
                if (liveWidth > 0)
                {
                    using (var liveBrush = new SolidBrush(_colorLive))
                        g.FillRectangle(liveBrush, probBarRect.X, probBarRect.Y, liveWidth, probBarRect.Height);
                }
                int blankWidth = probBarRect.Width - liveWidth;
                if (blankWidth > 0)
                {
                    using (var blankBrush = new SolidBrush(_colorBlank))
                        g.FillRectangle(blankBrush, probBarRect.X + liveWidth, probBarRect.Y, blankWidth, probBarRect.Height);
                }
            }
            using (var borderPen = new Pen(_colorBorder, 1f))
                g.DrawRectangle(borderPen, probBarRect);

            // Probability Label Text
            using (var fontStats = new Font("Consolas", 9.5f, FontStyle.Bold))
            using (var liveBrush = new SolidBrush(_colorLive))
            using (var blankBrush = new SolidBrush(_colorBlank))
            using (var dimBrush = new SolidBrush(_colorDimText))
            {
                g.DrawString($"LIVE: {livePct:F1}%", fontStats, liveBrush, 12, probY + 22);
                g.DrawString($"BLANK: {blankPct:F1}%", fontStats, blankBrush, Width - 140, probY + 22);
                g.DrawString($"REMAINING: {total} SHELLS", fontStats, dimBrush, Width / 2 - 80, probY + 22);
            }

            // --- FOOTER CONTROLS & HOTKEYS ---
            int footerY = Height - 46;
            using (var linePen = new Pen(_colorBorder, 1f))
                g.DrawLine(linePen, 0, footerY, Width, footerY);

            // Reset Button
            _btnReset = new Rectangle(12, footerY + 8, 85, 30);
            DrawButton(g, _btnReset, "RESET (F8)", Color.FromArgb(60, 65, 80));

            // Click-Through Toggle Button
            _btnToggleClickThrough = new Rectangle(105, footerY + 8, 140, 30);
            string toggleText = IsClickThrough ? "CLICK-THRU: ON" : "CLICK-THRU: OFF";
            Color toggleColor = IsClickThrough ? Color.FromArgb(140, 100, 20) : Color.FromArgb(40, 50, 70);
            DrawButton(g, _btnToggleClickThrough, $"{toggleText} (F4)", toggleColor);

            // Hotkey guide text
            using (var fontHotkeys = new Font("Consolas", 8.5f, FontStyle.Regular))
            using (var dimBrush = new SolidBrush(_colorDimText))
            {
                g.DrawString("F8: Reset  |  F9: Hide HUD", fontHotkeys, dimBrush, 255, footerY + 16);
            }
        }

        private void DrawShellBox(Graphics g, Rectangle box, string title, int count, Color mainColor, Color bgColor, out Rectangle btnMinus, out Rectangle btnPlus)
        {
            // Box container
            using (var bgBrush = new SolidBrush(Color.FromArgb(20, 24, 30)))
                g.FillRectangle(bgBrush, box);

            using (var pen = new Pen(Color.FromArgb(140, mainColor), 1.2f))
                g.DrawRectangle(pen, box);

            // Box title
            using (var fontTitle = new Font("Consolas", 9f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(mainColor))
                g.DrawString(title, fontTitle, titleBrush, box.X + 8, box.Y + 8);

            // Big Count Number
            using (var fontCount = new Font("Consolas", 28f, FontStyle.Bold))
            using (var countBrush = new SolidBrush(mainColor))
                g.DrawString(count.ToString(), fontCount, countBrush, box.X + 8, box.Y + 26);

            // Shell icons
            int shellX = box.X + 68;
            int shellY = box.Y + 34;
            int maxIcons = Math.Min(8, count);
            for (int i = 0; i < maxIcons; i++)
            {
                Rectangle shellRect = new Rectangle(shellX + (i % 4) * 16, shellY + (i / 4) * 20, 12, 16);
                using (var sBrush = new SolidBrush(mainColor))
                    g.FillRectangle(sBrush, shellRect);
                using (var sPen = new Pen(Color.White, 1f))
                    g.DrawRectangle(sPen, shellRect);
            }

            // Minus button
            btnMinus = new Rectangle(box.Right - 58, box.Bottom - 30, 24, 22);
            DrawButton(g, btnMinus, "-", Color.FromArgb(40, 45, 55));

            // Plus button
            btnPlus = new Rectangle(box.Right - 30, box.Bottom - 30, 24, 22);
            DrawButton(g, btnPlus, "+", Color.FromArgb(40, 45, 55));
        }

        private void DrawButton(Graphics g, Rectangle rect, string text, Color bg)
        {
            using (var brush = new SolidBrush(bg))
                g.FillRectangle(brush, rect);

            using (var pen = new Pen(_colorBorder, 1f))
                g.DrawRectangle(pen, rect);

            using (var font = new Font("Consolas", 8.5f, FontStyle.Bold))
            using (var textBrush = new SolidBrush(_colorText))
            {
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, textBrush, rect, sf);
            }
        }

        // --- MOUSE HANDLERS (DRAG & BUTTON CLICKS) ---
        private void OnMouseDownHandler(object? sender, MouseEventArgs e)
        {
            if (IsClickThrough) return;

            if (e.Button == MouseButtons.Left)
            {
                // Check Header Drag
                if (e.Y <= 44)
                {
                    if (_btnClose.Contains(e.Location))
                    {
                        Hide();
                        return;
                    }
                    _isDragging = true;
                    _dragStartPoint = e.Location;
                    return;
                }

                // Check Buttons
                if (_btnLiveMinus.Contains(e.Location)) AdjustLive(-1);
                else if (_btnLivePlus.Contains(e.Location)) AdjustLive(1);
                else if (_btnBlankMinus.Contains(e.Location)) AdjustBlank(-1);
                else if (_btnBlankPlus.Contains(e.Location)) AdjustBlank(1);
                else if (_btnToggleClickThrough.Contains(e.Location)) ToggleClickThrough();
                else if (_btnReset.Contains(e.Location)) ResetCurrentRound();
            }
        }

        private void OnMouseMoveHandler(object? sender, MouseEventArgs e)
        {
            if (_isDragging && !IsClickThrough)
            {
                AutoAttach = false; // Disable auto-snap while user is manually positioning
                Point currentScreen = PointToScreen(e.Location);
                Location = new Point(currentScreen.X - _dragStartPoint.X, currentScreen.Y - _dragStartPoint.Y);
            }
        }

        private void OnMouseUpHandler(object? sender, MouseEventArgs e)
        {
            _isDragging = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _syncTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
