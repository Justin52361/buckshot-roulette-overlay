using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace BuckshotOverlay
{
    /// <summary>
    /// Automatic Round and Shot Tracker for Buckshot Roulette.
    /// Utilizes Windows 10/11 Media OCR on the dialogue area to detect new rounds (e.g. '2 LIVE ROUNDS. 2 BLANK.')
    /// and combines Godot 4 heap tracking and muzzle flash detection to automatically deduct rounds as the game progresses.
    /// </summary>
    public class AutoTrackerEngine : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private const int SRCCOPY = 0x00CC0020;

        private readonly OverlayForm _overlay;
        private readonly Action<string> _logger;
        private OcrEngine? _ocrEngine;
        private CancellationTokenSource? _cts;
        private Task? _trackerTask;

        private Process? _targetProcess;
        private IntPtr _targetHWnd = IntPtr.Zero;

        // Round state tracking
        private string _lastAnnouncedText = string.Empty;
        private DateTime _lastAnnounceTime = DateTime.MinValue;
        private int _lastTrackedTotal = -1;
        private DateTime _lastShotRegisteredTime = DateTime.MinValue;
        private ulong _lastTrackedAddress = 0;
        private DateTime _lastRoundResetTime = DateTime.MinValue;
        private int _consecutiveEmptyScans = 0;

        // Reference tracking of the game's live 'sequenceArray' (see CheckMemorySequence)
        private sealed class TrackedHolder
        {
            public ulong Address;
            public int Stride;
            public int PtrOffset;
            public List<string> LastSequence = new List<string>();
            public List<string>? PendingInversion;
            public int InvalidTicks;
        }
        private readonly List<TrackedHolder> _holders = new List<TrackedHolder>();
        private TrackedHolder? _confirmedHolder;
        private readonly HashSet<string> _staleSignatures = new HashSet<string>();
        private DateTime _lastFullScanTime = DateTime.MinValue;
        private bool _awaitingNewRound = false;
        private string? _pendingNewRoundSignature;
        private int _failedEstablishAttempts = 0;
        // Last sequence seen on a reference before it was lost; used to re-acquire after missed shots
        private List<string> _lastKnownSequence = new List<string>();
        // False until a round start has been observed since Start(); the first detection may land mid-round
        private bool _seenRoundSinceStart = false;
        private DateTime _lastNewLoadCheckTime = DateTime.MinValue;
        private string? _pendingNewLoadSignature;

        // Luminance history for muzzle flash detection
        private readonly Queue<double> _luminanceHistory = new Queue<double>();
        private DateTime _lastFlashTime = DateTime.MinValue;

        public bool IsRunning { get; private set; }

        public void ResetRoundState()
        {
            _lastAnnouncedText = string.Empty;
            _lastAnnounceTime = DateTime.MinValue;
            _lastRoundResetTime = DateTime.MinValue;
            _lastShotRegisteredTime = DateTime.MinValue;
            _lastTrackedAddress = 0;
            _consecutiveEmptyScans = 0;
            _holders.Clear();
            _confirmedHolder = null;
            _awaitingNewRound = false;
            _pendingNewRoundSignature = null;
            _failedEstablishAttempts = 0;
            _lastFullScanTime = DateTime.MinValue;
            _lastKnownSequence.Clear();
            _seenRoundSinceStart = false;
            _lastNewLoadCheckTime = DateTime.MinValue;
            _pendingNewLoadSignature = null;
            MemoryScanner.ClearLock();
        }

        public AutoTrackerEngine(OverlayForm overlay, Action<string> logger)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_ocrEngine == null)
                {
                    _logger("[AUTO-TRACK] Windows OCR initialized with default English fallback.");
                }
                else
                {
                    _logger($"[AUTO-TRACK] Windows OCR Engine ready ({_ocrEngine.RecognizerLanguage.DisplayName}).");
                }
            }
            catch (Exception ex)
            {
                _logger($"[AUTO-TRACK WARNING] OCR initialization notice: {ex.Message}");
            }
        }

        public void Start(Process process, IntPtr hWnd)
        {
            Stop();

            _targetProcess = process;
            _targetHWnd = hWnd != IntPtr.Zero ? hWnd : process.MainWindowHandle;
            _seenRoundSinceStart = false;
            _cts = new CancellationTokenSource();
            IsRunning = true;

            _logger($"[AUTO-TRACK] Engine started for PID {process.Id} (HWND: 0x{_targetHWnd.ToInt64():X})");
            _trackerTask = Task.Run(() => TrackerLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            try
            {
                _cts?.Cancel();
                _trackerTask?.Wait(500);
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _trackerTask = null;
            }

            _logger("[AUTO-TRACK] Engine stopped.");
        }

        private async Task TrackerLoopAsync(CancellationToken token)
        {
            int ocrTickCounter = 0;
            int memoryTickCounter = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_targetProcess == null || _targetProcess.HasExited)
                    {
                        break;
                    }

                    // Update HWnd if necessary
                    if (_targetHWnd == IntPtr.Zero || !IsWindowVisible(_targetHWnd) || IsIconic(_targetHWnd))
                    {
                        IntPtr newHwnd = InjectorEngine.FindProcessMainWindow(_targetProcess.Id);
                        if (newHwnd != IntPtr.Zero)
                        {
                            _targetHWnd = newHwnd;
                        }
                    }

                    if (_targetHWnd != IntPtr.Zero && !IsIconic(_targetHWnd))
                    {
                        // 1. Memory State Check (every ~100ms = 2 iterations of 50ms) - primary authoritative detection
                        if (memoryTickCounter++ >= 2)
                        {
                            memoryTickCounter = 0;
                            CheckMemorySequence();
                        }

                        // 2. OCR Subtitle Check (every ~300ms = 6 iterations of 50ms) - secondary fallback for story mode
                        if (ocrTickCounter++ >= 6)
                        {
                            ocrTickCounter = 0;
                            await CheckDialogueSubtitleAsync();
                        }

                        // 3. Visual Muzzle Flash Check (every 50ms)
                        CheckMuzzleFlash();
                    }
                }
                catch (Exception ex)
                {
                    // Suppress transient loop errors to keep engine alive
                    _logger($"[AUTO-TRACK DEBUG] Loop error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Captures the dialogue banner area at the bottom-center of the Buckshot Roulette window
        /// and runs Windows OCR to detect announcements such as '2 LIVE ROUNDS. 2 BLANK.'
        /// </summary>
        private async Task CheckDialogueSubtitleAsync()
        {
            if (_ocrEngine == null || _targetHWnd == IntPtr.Zero) return;

            if (!GetClientRect(_targetHWnd, out RECT clientRect) || clientRect.Width < 200 || clientRect.Height < 150)
            {
                return;
            }

            POINT pt = new POINT { X = 0, Y = 0 };
            ClientToScreen(_targetHWnd, ref pt);

            // Subtitle appears centered horizontally in the lower third of the game window
            int cropX = pt.X + (int)(clientRect.Width * 0.18);
            int cropY = pt.Y + (int)(clientRect.Height * 0.65);
            int cropW = (int)(clientRect.Width * 0.64);
            int cropH = (int)(clientRect.Height * 0.22);

            if (cropW <= 20 || cropH <= 10) return;

            using var bmp = new Bitmap(cropW, cropH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr screenDC = GetDC(IntPtr.Zero);
                if (screenDC != IntPtr.Zero)
                {
                    IntPtr memDC = g.GetHdc();
                    BitBlt(memDC, 0, 0, cropW, cropH, screenDC, cropX, cropY, SRCCOPY);
                    g.ReleaseHdc(memDC);
                    ReleaseDC(IntPtr.Zero, screenDC);
                }
                else
                {
                    return;
                }
            }

            // Convert to SoftwareBitmap directly
            var bmpData = bmp.LockBits(new Rectangle(0, 0, cropW, cropH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixelBytes = new byte[bmpData.Stride * cropH];
            Marshal.Copy(bmpData.Scan0, pixelBytes, 0, pixelBytes.Length);
            bmp.UnlockBits(bmpData);

            var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, cropW, cropH, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixelBytes.AsBuffer());

            var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);
            string recognizedText = ocrResult.Text?.Trim().ToUpperInvariant() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(recognizedText) || recognizedText.Length < 6)
            {
                return;
            }

            // Check if text announces Live and Blank rounds
            ParseAndApplyAnnouncement(recognizedText);
        }

        private void ParseAndApplyAnnouncement(string text)
        {
            // CRITICAL: An announcement can ONLY set a new round if the gun is currently EMPTY (currentTotal == 0).
            // If shells are currently in the gun, a round is actively in progress
            // and lingering or repeated dialogue subtitles MUST NOT reset the round!
            if (!_awaitingNewRound && _overlay.LiveCount + _overlay.BlankCount > 0)
            {
                return;
            }

            // Cooldown check (prevent re-triggering the same announcement within 8 seconds)
            if (text == _lastAnnouncedText && (DateTime.Now - _lastAnnounceTime).TotalSeconds < 8.0)
            {
                return;
            }

            int live = -1;
            int blank = -1;

            // Pattern 1: 'X LIVE ROUND(S). Y BLANK(S).' or 'X LIVE, Y BLANK'
            var match1 = Regex.Match(text, @"(\d+)\s+LIVE[^\d]*(\d+)\s+BLANK");
            if (match1.Success)
            {
                int.TryParse(match1.Groups[1].Value, out live);
                int.TryParse(match1.Groups[2].Value, out blank);
            }
            else
            {
                // Pattern 2: 'Y BLANK(S). X LIVE ROUND(S).'
                var match2 = Regex.Match(text, @"(\d+)\s+BLANK[^\d]*(\d+)\s+LIVE");
                if (match2.Success)
                {
                    int.TryParse(match2.Groups[2].Value, out live);
                    int.TryParse(match2.Groups[1].Value, out blank);
                }
                else
                {
                    // Pattern 3: Compact format e.g. '2L 2B' or '2 LIVE 2 BLANK'
                    var match3 = Regex.Match(text, @"(\d+)\s*L(?:IVE)?[^\d]+(\d+)\s*B(?:LANK)?");
                    if (match3.Success)
                    {
                        int.TryParse(match3.Groups[1].Value, out live);
                        int.TryParse(match3.Groups[2].Value, out blank);
                    }
                }
            }

            if (live >= 0 && blank >= 0 && (live + blank) >= 2 && (live + blank) <= 8)
            {
                _lastAnnouncedText = text;
                _lastAnnounceTime = DateTime.Now;
                _lastTrackedTotal = live + blank;
                _lastRoundResetTime = DateTime.Now;
                _lastShotRegisteredTime = DateTime.Now;
                _lastTrackedAddress = 0;
                _awaitingNewRound = false;
                _holders.Clear();
                _pendingNewRoundSignature = null;
                _lastKnownSequence.Clear();
                _seenRoundSinceStart = true;

                _logger($"[AUTO-DETECT] New Round revealed (OCR): {live} LIVE, {blank} BLANK! (Text: '{text}')");

                // Execute round setup on OverlayForm UI thread
                _overlay.BeginInvoke(new Action(() =>
                {
                    _overlay.SetRound(live, blank);
                }));

                // Reset memory scanner target so next sequence is located fresh with expected counts
                MemoryScanner.SetExpectedInitialCounts(live, blank);
            }
        }

        /// <summary>
        /// Tracks the game's live shotgun 'sequenceArray' by REFERENCE rather than by guessing among heap buffers.
        ///
        /// Why: the heap is full of look-alike arrays (the never-modified 'tempSequence', freed buffers left behind
        /// by Godot's CowData reallocation, previous rounds). Picking "any array with fewer shells" produced wrong
        /// counts. Instead we locate the pointer slot inside the owning Array object and re-read it every tick, so
        /// we see exactly what the game sees, across reallocations, until it becomes null (chamber empty).
        ///
        /// A deduction is accepted ONLY when the tracked array becomes exactly its previous contents minus the
        /// front shell (Godot's remove_at(0)), and the popped shell's type is what gets deducted.
        /// </summary>
        private void CheckMemorySequence()
        {
            if (_targetProcess == null || _targetProcess.HasExited) return;

            int currentLive = _overlay.LiveCount;
            int currentBlank = _overlay.BlankCount;
            int currentTotal = currentLive + currentBlank;

            if (_awaitingNewRound || (currentTotal == 0 && (DateTime.Now - _lastRoundResetTime).TotalMilliseconds > 1500))
            {
                DetectNewRound();
                return;
            }

            if (currentTotal == 0) return; // Overlay hasn't applied the round we just started yet

            if (_holders.Count == 0)
            {
                EstablishHolders(currentLive, currentBlank);
            }
            else
            {
                PollHolders();
            }

            // A new load can happen while the HUD still shows shells (missed final shot, wrong array, mid-round start)
            if (!_awaitingNewRound && _overlay.LiveCount + _overlay.BlankCount > 0) CheckForNewLoad();
        }

        /// <summary>
        /// True when 'seq' could be the 'known' array after zero or more remove_at(0) calls and an inverter flip of
        /// its front shell, i.e. it is still the same round.
        /// </summary>
        private static bool IsContinuationOf(List<string> seq, List<string> known)
        {
            if (known.Count == 0 || seq.Count == 0 || seq.Count > known.Count) return false;
            return seq.Skip(1).SequenceEqual(known.Skip(known.Count - seq.Count + 1));
        }

        /// <summary>
        /// Returns the first candidate (in the given order) that the game still references through a pointer slot,
        /// which rules out freed buffers left behind by reallocation.
        /// </summary>
        private MemoryScanner.CandidateInfo? FirstReferenced(IEnumerable<MemoryScanner.CandidateInfo> ordered)
        {
            var list = ordered.ToList();
            if (list.Count == 0) return null;
            var holderMap = MemoryScanner.FindPointerHolders(_targetProcess!, list.Select(c => c.Result.FoundAddress).ToList());
            foreach (var c in list)
            {
                if (!holderMap.TryGetValue(c.Result.FoundAddress, out var addrs)) continue;
                foreach (var ha in addrs)
                {
                    var st = MemoryScanner.ReadViaHolder(_targetProcess!, ha, c.Stride, c.PtrOffset, out var r);
                    if (st == MemoryScanner.HolderReadStatus.Ok && r != null && r.Sequence.SequenceEqual(c.Result.Sequence)) return c;
                }
            }
            return null;
        }

        /// <summary>
        /// While a round is being tracked, periodically looks for a freshly loaded shell array that is not a
        /// continuation of the current round. Shell arrays only shrink within a round, so such an array means
        /// the game loaded the shotgun again without us seeing the chamber empty.
        /// </summary>
        private void CheckForNewLoad()
        {
            if ((DateTime.Now - _lastNewLoadCheckTime).TotalMilliseconds < 1500) return;
            _lastNewLoadCheckTime = DateTime.Now;

            var known = _holders.Select(h => h.LastSequence).Where(s => s.Count > 0).ToList();
            if (_lastKnownSequence.Count > 0) known.Add(_lastKnownSequence);

            var fresh = MemoryScanner.ScanAllCandidates(_targetProcess!)
                .Where(c =>
                {
                    int t = c.Result.Sequence.Count;
                    return t >= 2 && t <= 8 && !_staleSignatures.Contains(c.Signature) &&
                           !known.Any(k => IsContinuationOf(c.Result.Sequence, k));
                })
                .OrderBy(c => c.Result.Sequence.Count)
                .ThenByDescending(c => c.Result.FoundAddress);

            var best = FirstReferenced(fresh);
            if (best == null)
            {
                _pendingNewLoadSignature = null;
                return;
            }

            // Require the same array on two consecutive checks (avoids catching the game mid-construction)
            if (_pendingNewLoadSignature != best.Signature)
            {
                _pendingNewLoadSignature = best.Signature;
                return;
            }

            _logger($"[AUTO-DETECT] New load found while HUD still showed shells.");
            StartRound(best.Result.LiveCount, best.Result.BlankCount, best.Result.Sequence);
        }

        private void DetectNewRound()
        {
            // 1. The game may reuse the same Array object for the next round; check our confirmed reference first.
            if (_confirmedHolder != null)
            {
                var st = MemoryScanner.ReadViaHolder(_targetProcess!, _confirmedHolder.Address, _confirmedHolder.Stride, _confirmedHolder.PtrOffset, out var res);
                if (st == MemoryScanner.HolderReadStatus.Ok && res != null && res.Sequence.Count >= 2 && res.Sequence.Count <= 8)
                {
                    string sig = $"H:{string.Join(",", res.Sequence)}";
                    if (_pendingNewRoundSignature == sig)
                    {
                        _pendingNewRoundSignature = null;
                        _confirmedHolder.LastSequence = new List<string>(res.Sequence);
                        _confirmedHolder.InvalidTicks = 0;
                        StartRound(res.LiveCount, res.BlankCount, res.Sequence);
                        _holders.Add(_confirmedHolder);
                        return;
                    }
                    _pendingNewRoundSignature = sig;
                    return;
                }
            }

            // 2. Full scan for a freshly allocated shell array not seen during previous rounds (throttled).
            if ((DateTime.Now - _lastFullScanTime).TotalMilliseconds < 400) return;
            _lastFullScanTime = DateTime.Now;

            // Before any round start has been seen we may be attaching mid-round: the real chamber can hold a single
            // shell, and it is always shorter than the untouched 'tempSequence' copy, so prefer the fewest shells.
            int minTotal = _seenRoundSinceStart ? 2 : 1;
            var fresh = MemoryScanner.ScanAllCandidates(_targetProcess!)
                .Where(c =>
                {
                    int t = c.Result.LiveCount + c.Result.BlankCount;
                    return t >= minTotal && t <= 8 && !_staleSignatures.Contains(c.Signature);
                });
            var best = FirstReferenced(_seenRoundSinceStart
                ? fresh.OrderByDescending(c => c.Result.FoundAddress)
                : fresh.OrderBy(c => c.Result.Sequence.Count).ThenByDescending(c => c.Result.FoundAddress));

            if (best == null)
            {
                _pendingNewRoundSignature = null;
                return;
            }

            // Require the same fresh array on two consecutive scans (avoids catching the game mid-construction)
            if (_pendingNewRoundSignature != best.Signature)
            {
                _pendingNewRoundSignature = best.Signature;
                return;
            }

            _pendingNewRoundSignature = null;
            StartRound(best.Result.LiveCount, best.Result.BlankCount, best.Result.Sequence);
        }

        private void StartRound(int live, int blank, List<string> sequence)
        {
            _awaitingNewRound = false;
            _holders.Clear();
            _failedEstablishAttempts = 0;
            _lastKnownSequence.Clear();
            _seenRoundSinceStart = true;
            _pendingNewLoadSignature = null;
            _lastNewLoadCheckTime = DateTime.Now;
            _lastRoundResetTime = DateTime.Now;
            _lastShotRegisteredTime = DateTime.Now;
            _lastFullScanTime = DateTime.MinValue;

            _logger($"[AUTO-DETECT] New Round chambered: {live} LIVE, {blank} BLANK! (Sequence: {string.Join(", ", sequence)})");
            _overlay.BeginInvoke(new Action(() => _overlay.SetRound(live, blank)));
        }

        private void EstablishHolders(int live, int blank)
        {
            if ((DateTime.Now - _lastFullScanTime).TotalMilliseconds < 500) return;
            _lastFullScanTime = DateTime.Now;

            var cands = MemoryScanner.ScanAllCandidates(_targetProcess!);

            var matching = cands.Where(c => c.Result.LiveCount == live && c.Result.BlankCount == blank).ToList();
            if (matching.Count > 0)
            {
                var holderMap = MemoryScanner.FindPointerHolders(_targetProcess!, matching.Select(c => c.Result.FoundAddress).ToList());
                foreach (var c in matching)
                {
                    if (!holderMap.TryGetValue(c.Result.FoundAddress, out var addrs)) continue;
                    foreach (var ha in addrs)
                    {
                        if (_holders.Count >= 24) break;
                        var st = MemoryScanner.ReadViaHolder(_targetProcess!, ha, c.Stride, c.PtrOffset, out var r);
                        if (st == MemoryScanner.HolderReadStatus.Ok && r != null && r.LiveCount == live && r.BlankCount == blank)
                        {
                            _holders.Add(new TrackedHolder
                            {
                                Address = ha,
                                Stride = c.Stride,
                                PtrOffset = c.PtrOffset,
                                LastSequence = new List<string>(r.Sequence)
                            });
                        }
                    }
                }
            }

            if (_holders.Count > 0)
            {
                // Everything in memory now belongs to this round or earlier ones; never treat it as a new load
                MarkStale(cands);
                _failedEstablishAttempts = 0;
                _logger($"[AUTO-TRACK] Tracking {_holders.Count} shell array reference(s) for {live}L/{blank}B.");
                return;
            }

            if (TryRecoverAfterMissedShots(cands))
            {
                MarkStale(cands);
                return;
            }

            _failedEstablishAttempts++;
            if (_failedEstablishAttempts >= 20) // ~10s with no matching live array: likely main menu / out of game
            {
                _failedEstablishAttempts = 0;
                _logger("[AUTO-TRACK] No live shotgun sequence matches the HUD. Clearing shells (Main Menu / Out of Game).");
                _overlay.BeginInvoke(new Action(() => _overlay.SetRound(0, 0)));
                _awaitingNewRound = true;
            }
        }

        /// <summary>
        /// After a reference is lost, shots may be fired before we re-acquire, so the live array no longer matches
        /// the HUD counts. Shells only ever leave from the front, so the real array must be a strict suffix of the
        /// last sequence we saw; re-attach to the longest such array that is reachable through a holder and deduct
        /// the shells that were missed.
        /// </summary>
        private bool TryRecoverAfterMissedShots(List<MemoryScanner.CandidateInfo> cands)
        {
            var known = _lastKnownSequence;
            if (known.Count < 2) return false;

            var suffixes = cands
                .Where(c => c.Result.Sequence.Count > 0 && c.Result.Sequence.Count < known.Count &&
                            c.Result.Sequence.SequenceEqual(known.Skip(known.Count - c.Result.Sequence.Count)))
                .ToList();
            if (suffixes.Count == 0) return false;

            var holderMap = MemoryScanner.FindPointerHolders(_targetProcess!, suffixes.Select(c => c.Result.FoundAddress).ToList());
            foreach (var c in suffixes.OrderByDescending(c => c.Result.Sequence.Count))
            {
                if (!holderMap.TryGetValue(c.Result.FoundAddress, out var addrs)) continue;
                foreach (var ha in addrs)
                {
                    if (_holders.Count >= 24) break;
                    var st = MemoryScanner.ReadViaHolder(_targetProcess!, ha, c.Stride, c.PtrOffset, out var r);
                    if (st == MemoryScanner.HolderReadStatus.Ok && r != null && r.Sequence.SequenceEqual(c.Result.Sequence))
                    {
                        _holders.Add(new TrackedHolder
                        {
                            Address = ha,
                            Stride = c.Stride,
                            PtrOffset = c.PtrOffset,
                            LastSequence = new List<string>(r.Sequence)
                        });
                    }
                }
                if (_holders.Count == 0) continue;

                var remaining = new List<string>(c.Result.Sequence);
                var missed = known.Take(known.Count - remaining.Count).ToList();
                int newLive = remaining.Count(s => s == "LIVE");
                int newBlank = remaining.Count - newLive;

                _failedEstablishAttempts = 0;
                _lastKnownSequence = new List<string>(remaining);
                _lastShotRegisteredTime = DateTime.Now;
                _logger($"[AUTO-TRACK] Re-acquired shell array after {missed.Count} missed shell(s) ({string.Join(", ", missed)}). Remaining: {newLive}L, {newBlank}B");
                _overlay.BeginInvoke(new Action(() =>
                {
                    foreach (var s in missed)
                    {
                        if (s == "LIVE") _overlay.RegisterLiveFired();
                        else _overlay.RegisterBlankFired();
                    }
                    _overlay.UpdateCurrentCounts(newLive, newBlank);
                }));
                return true;
            }
            return false;
        }

        private void PollHolders()
        {
            foreach (var h in _holders.ToList())
            {
                var st = MemoryScanner.ReadViaHolder(_targetProcess!, h.Address, h.Stride, h.PtrOffset, out var r);
                if (st == MemoryScanner.HolderReadStatus.Invalid)
                {
                    RegisterInvalid(h, "unreadable/invalid array");
                    continue;
                }

                if (r != null) _staleSignatures.Add($"{r.FoundAddress:X}:{string.Join(",", r.Sequence)}");

                var newSeq = st == MemoryScanner.HolderReadStatus.Empty ? new List<string>() : r!.Sequence;
                var prev = h.LastSequence;

                if (newSeq.SequenceEqual(prev))
                {
                    h.InvalidTicks = 0;
                    h.PendingInversion = null;
                    continue;
                }

                // DEDUCTION: exactly the previous contents minus the front shell (remove_at(0))
                if (prev.Count > 0 && newSeq.Count == prev.Count - 1 && newSeq.SequenceEqual(prev.Skip(1)))
                {
                    // An unconfirmed reference going null is ambiguous (could be an unrelated freed object)
                    if (newSeq.Count == 0 && h != _confirmedHolder && _holders.Count > 1)
                    {
                        RegisterInvalid(h, "unconfirmed reference went empty");
                        continue;
                    }

                    string popped = prev[0];
                    ConfirmHolder(h);
                    h.LastSequence = new List<string>(newSeq);
                    h.PendingInversion = null;
                    ApplyDeduction(popped, newSeq);
                    return;
                }

                // INVERTER: same shells except the chambered (front) one flipped. Require 2 identical reads.
                if (newSeq.Count == prev.Count && newSeq.Count > 0 && newSeq[0] != prev[0] && newSeq.Skip(1).SequenceEqual(prev.Skip(1)))
                {
                    if (h.PendingInversion != null && h.PendingInversion.SequenceEqual(newSeq))
                    {
                        ConfirmHolder(h);
                        h.LastSequence = new List<string>(newSeq);
                        h.PendingInversion = null;
                        int nl = newSeq.Count(s => s == "LIVE");
                        int nb = newSeq.Count - nl;
                        _lastShotRegisteredTime = DateTime.Now;
                        _logger($"[AUTO-INVERT] Inverter used: chambered shell is now {newSeq[0]}. Chamber: {nl}L, {nb}B");
                        _overlay.BeginInvoke(new Action(() => _overlay.UpdateCurrentCounts(nl, nb)));
                        return;
                    }
                    h.PendingInversion = new List<string>(newSeq);
                    continue;
                }

                // SHUFFLE (game reorders before loading): same multiset, just accept the new order silently
                if (newSeq.Count == prev.Count && newSeq.Count > 0 &&
                    newSeq.Count(s => s == "LIVE") == prev.Count(s => s == "LIVE"))
                {
                    h.LastSequence = new List<string>(newSeq);
                    h.InvalidTicks = 0;
                    continue;
                }

                // Anything else is not a legal game transition for this reference (stale/unrelated memory)
                RegisterInvalid(h, $"illegal transition to [{string.Join(",", newSeq)}]");
            }
        }

        private void ApplyDeduction(string popped, List<string> remaining)
        {
            int newLive = remaining.Count(s => s == "LIVE");
            int newBlank = remaining.Count - newLive;
            bool wasLive = popped == "LIVE";

            _lastShotRegisteredTime = DateTime.Now;
            _logger($"[AUTO-DEDUCT] {(wasLive ? "LIVE" : "BLANK")} shell fired/racked. Remaining: {newLive}L, {newBlank}B");

            _overlay.BeginInvoke(new Action(() =>
            {
                if (wasLive) _overlay.RegisterLiveFired();
                else _overlay.RegisterBlankFired();
                _overlay.UpdateCurrentCounts(newLive, newBlank); // memory is authoritative
            }));

            if (remaining.Count == 0)
            {
                _logger("[AUTO-TRACK] Chamber empty. Waiting for next load...");
                _awaitingNewRound = true;
                _pendingNewRoundSignature = null;
                _holders.Clear(); // _confirmedHolder retained in case the game reuses the same Array

                // Remember every shell array currently in memory so leftovers are never mistaken for the next round
                MarkStale(MemoryScanner.ScanAllCandidates(_targetProcess!));
                _lastFullScanTime = DateTime.Now;
            }
        }

        private void ConfirmHolder(TrackedHolder h)
        {
            if (_confirmedHolder == h) return;
            _confirmedHolder = h;
            // Only one reference can be the real sequenceArray; drop the rest (e.g. tempSequence)
            _holders.RemoveAll(x => x != h);
        }

        private void RegisterInvalid(TrackedHolder h, string detail)
        {
            h.InvalidTicks++;
            if (h.InvalidTicks == 1 && h == _confirmedHolder)
            {
                _logger($"[AUTO-TRACK DEBUG] Confirmed reference rejected: {detail} (last: [{string.Join(",", h.LastSequence)}])");
            }
            if (h.InvalidTicks <= 30) return; // ~3s of illegal/unreadable state

            _holders.Remove(h);
            if (_confirmedHolder == h || _holders.Count == 0)
            {
                if (h.LastSequence.Count > 0) _lastKnownSequence = new List<string>(h.LastSequence);
            }
            if (_confirmedHolder == h)
            {
                _confirmedHolder = null;
                _logger("[AUTO-TRACK] Lost shell array reference; re-acquiring...");
            }
        }

        private void MarkStale(IEnumerable<MemoryScanner.CandidateInfo> cands)
        {
            if (_staleSignatures.Count > 20000) _staleSignatures.Clear();
            foreach (var c in cands) _staleSignatures.Add(c.Signature);
        }

        /// <summary>
        /// Monitors center screen luminance for muzzle flash to provide instant live shot deduction.
        /// </summary>
        private void CheckMuzzleFlash()
        {
            if (_targetHWnd == IntPtr.Zero) return;
            if (_overlay.LiveCount <= 0) return;
            if (_holders.Count > 0 || _confirmedHolder != null) return; // memory tracking is authoritative

            if (!GetClientRect(_targetHWnd, out RECT clientRect) || clientRect.Width < 200 || clientRect.Height < 150)
            {
                return;
            }

            POINT pt = new POINT { X = 0, Y = 0 };
            ClientToScreen(_targetHWnd, ref pt);

            // Center-screen box (where muzzle flash occurs in Buckshot Roulette)
            int boxSize = 64;
            int boxX = pt.X + (clientRect.Width - boxSize) / 2;
            int boxY = pt.Y + (clientRect.Height - boxSize) / 2;

            double avgLuma = 0;
            using (var bmp = new Bitmap(boxSize, boxSize, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr screenDC = GetDC(IntPtr.Zero);
                    if (screenDC != IntPtr.Zero)
                    {
                        IntPtr memDC = g.GetHdc();
                        BitBlt(memDC, 0, 0, boxSize, boxSize, screenDC, boxX, boxY, SRCCOPY);
                        g.ReleaseHdc(memDC);
                        ReleaseDC(IntPtr.Zero, screenDC);
                    }
                    else
                    {
                        return;
                    }
                }

                var bmpData = bmp.LockBits(new Rectangle(0, 0, boxSize, boxSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = bmpData.Stride;
                IntPtr scan0 = bmpData.Scan0;

                byte[] pixelBytes = new byte[stride * boxSize];
                Marshal.Copy(scan0, pixelBytes, 0, pixelBytes.Length);
                bmp.UnlockBits(bmpData);

                long totalLuma = 0;
                for (int y = 0; y < boxSize; y += 4)
                {
                    for (int x = 0; x < boxSize; x += 4)
                    {
                        int idx = y * stride + x * 4;
                        byte b = pixelBytes[idx];
                        byte gVal = pixelBytes[idx + 1];
                        byte r = pixelBytes[idx + 2];
                        totalLuma += (r * 299 + gVal * 587 + b * 114) / 1000;
                    }
                }
                avgLuma = (double)totalLuma / ((boxSize / 4) * (boxSize / 4));
            }

            _luminanceHistory.Enqueue(avgLuma);
            if (_luminanceHistory.Count > 10) _luminanceHistory.Dequeue();

            // Muzzle flash signature: sudden jump over 190 luminance from baseline < 70
            if (avgLuma > 185 && (DateTime.Now - _lastFlashTime).TotalMilliseconds > 1200 && (DateTime.Now - _lastShotRegisteredTime).TotalMilliseconds > 800)
            {
                _lastFlashTime = DateTime.Now;
                _lastShotRegisteredTime = DateTime.Now;
                _logger($"[AUTO-DEDUCT] Muzzle flash detected (Luma: {avgLuma:F0}) -> LIVE shell fired!");
                _overlay.BeginInvoke(new Action(() => _overlay.RegisterLiveFired()));
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
