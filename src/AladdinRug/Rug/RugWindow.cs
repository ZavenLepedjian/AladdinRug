using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AladdinRug
{
    /// <summary>
    /// One rug, behaving like real cloth. Press anywhere on it and your mouse becomes a hand that takes a handful
    /// of rug: lift it, drag it, fold a corner or an edge over, and let go. The rug drops, bunches and stays where
    /// it landed. Double-click (or Ctrl+Alt+R) smooths it flat and rolls it up; the roll stays on the desktop and
    /// a click unrolls it.
    ///
    /// It is a transparent window kept directly above the desktop in the stacking order, so apps stay on top.
    /// While lying flat the window is just the size of the rug; once you pick it up the window grows to the whole
    /// monitor (its transparent parts let clicks through) so the rug can go anywhere.
    /// </summary>
    internal sealed class RugWindow : Window
    {
        public enum RugState { Down, Rising, Up, Falling }   // Up = rolled up, still lying on the desktop

        private const double RollThickness = 6.0;      // DIPs of "pile" per turn; sets how fat the roll gets
        private const double RollSeconds = 0.9, UnrollSeconds = 1.2, FlattenSeconds = 0.45;
        private const double RollPortion = 0.82;       // share of the roll animation spent rolling; the rest ties the straps
        private const int DragThreshold = 4;           // pixels before a click on the roll becomes a drag
        private const float ClothGap = 20f;            // spacing of the cloth's points, in DIPs
        private const double CrewRollUpSeconds = 2.8, CrewUnrollSeconds = 2.6;   // the rolling itself, once the merchant has hold of it

        // ---- visuals
        private readonly Canvas _stage = new Canvas();
        private readonly Canvas _content = new Canvas();
        private readonly Image _front = new Image();                        // the flat rug, with its shadow
        private readonly Image _clothImage = new Image();                   // the cloth rug, drawn by ClothRenderer
        private readonly Grid _roll = new Grid();
        private readonly Border _rollShadow = new Border(), _strapA = new Border(), _strapB = new Border();

        private readonly DispatcherTimer _frames = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        private readonly DispatcherTimer _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _dragWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };

        // ---- letting icons be dropped "under" the rug
        private bool _passThrough;                       // the rug is see-through to the mouse and to drag and drop
        private int _downPolls;                          // how long a button press that isn't ours has lasted
        private long _upSince;                           // when the button came up while we were see-through

        // ---- placement
        private IntPtr _hwnd;
        private readonly DeskGeometry _geo;                 // the monitor's usable area
        private readonly int _rugW, _rugH;                  // the rug's window size in pixels when lying flat
        private int _rolledW;                               // window width in pixels while rolled up
        private int _rugX, _rugY;                           // flat window's top-left in screen pixels
        private bool _compact;                              // flat window is currently only as wide as the roll
        private bool _startRolled;
        private double _wpf = 1, _widthDip, _heightDip;     // _widthDip/_heightDip: the flat rug's window, in DIPs
        private bool _quitting, _initializing = true;
        private RugArt.Images _art;
        private readonly StyleSpec _spec;

        // ---- the broom: sweeping the desktop's icons under the rug, and the lumps they make
        private SweepJob _sweep;
        private TidyJob _tidy;
        private bool _pendingSweep, _pendingTidy, _quietJobs;
        private readonly System.Collections.Generic.List<double> _jobGaps = new System.Collections.Generic.List<double>();   // frame gaps while the merchant works (for the log)
        private readonly Image _bumpImage = new Image { IsHitTestVisible = false };
        private float[] _bumpShade;                      // light and shade of the lumps, for the whole monitor (null = no lumps)

        // ---- the merchant who rolls the rug
        private Roller _roller;
        private bool _pushStarted;
        private double _rollTarget;                      // 1 = rolling up, 0 = rolling out

        // ---- dust kicked up when the flat rug lands (the cloth has its own, inside the engine)
        private readonly Dust _flatDust;
        private readonly DustView _dustView = new DustView();
        private readonly Image _dustImage = new Image { IsHitTestVisible = false };
        private WriteableBitmap _dustBitmap;
        private byte[] _dustBuf;
        private Int32Rect _dustShown = new Int32Rect(0, 0, 0, 0);
        private readonly Random _dustRandom = new Random(3);

        // ---- the cloth (exists only once you've picked the rug up)
        private ClothEngine _engine;
        private WriteableBitmap _clothBitmap;
        private bool _holding, _flattening, _pendingRoll;
        private double _flattenT;
        private long _lastTick;

        internal bool TestMode;                         // self-test: never touches the saved rug

        // ---- animation
        private readonly Anim _rollAnim = new Anim(1, Anim.InOutCubic);     // 0 = flat, 1 = rolled up

        // ---- clicking on the roll
        private bool _rollPressed, _rollDragging;
        private Native.POINT _pressCursor;
        private int _pressX, _pressY;

        public RugState State { get; private set; } = RugState.Up;
        public bool IsRolled => State == RugState.Up || State == RugState.Rising;

        public event Action ContextRequested;

        /// <summary>Something to tell the user (a sweep that couldn't be done).</summary>
        public event Action<string> Notice;

        // Margins inside the rug's window, in DIPs of this window (the art is drawn at the monitor's scale).
        private double PadDip => RugArt.PadDip * _geo.Scale / _wpf;
        private double FringeDip => RugArt.FringeDip * _geo.Scale / _wpf;

        public RugWindow(DeskGeometry geo) : this(geo, RugStyles.Current, null) { }

        /// <param name="centre">Where the old rug's middle was (screen pixels), when this one is replacing it.</param>
        public RugWindow(DeskGeometry geo, RugStyle style, (int x, int y)? centre)
        {
            _geo = geo;
            _spec = RugStyles.Get(style);
            _flatDust = new Dust((float)geo.Scale);
            _rugW = Math.Max(400, (int)(geo.W * RugArt.DefaultWidthFraction * _spec.WidthFactor));
            _rugH = Math.Max(300, (int)(geo.H * RugArt.DefaultHeightFraction * _spec.HeightFactor));
            if (RugPositions.TryGet(geo, out int dx, out int dy, out bool rolled)) { _rugX = geo.X + dx; _rugY = geo.Y + dy; _startRolled = rolled; }
            else { _rugX = geo.X + (geo.W - _rugW) / 2; _rugY = geo.Y + (geo.H - _rugH) / 2; }
            if (centre.HasValue && !_startRolled) { _rugX = centre.Value.x - _rugW / 2; _rugY = centre.Value.y - _rugH / 2; }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 200; Height = 200;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Title = "Aladdin Rug";
            Cursor = HandCursors.Open;
            ToolTipService.SetInitialShowDelay(this, 1500);

            Content = _stage;
            _stage.Children.Add(_content);

            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += (s, e) => OnMouseUp(true);
            LostMouseCapture += (s, e) => OnMouseUp(false);
            MouseRightButtonUp += (s, e) => { ContextRequested?.Invoke(); e.Handled = true; };

            SweptStore.Changed += OnSweptChanged;
            _frames.Tick += (s, e) => OnFrame();
            _watch.Tick += (s, e) => Tick();
            _dragWatch.Tick += (s, e) => WatchDrag();
        }

        // ================================================================== public controls

        /// <summary>Show the rug where it was left: rolled up, or unrolling onto the floor.</summary>
        public void Begin()
        {
            State = RugState.Up;
            _compact = true;
            _rollAnim.Set(1);
            ApplyProgress(1);
            ClampToMonitor();
            ApplyBounds(zOrder: true, show: true);
            UpdateToolTip();
            _watch.Start();
            _dragWatch.Start();
            if (!_startRolled) Unroll();
        }

        public void Toggle()
        {
            if (IsRolled) Unroll(); else RollUp();
        }

        // ================================================================== the merchant

        private bool UseCrew => RugStyles.Roller && !TestMode && _hwnd != IntPtr.Zero;
        private bool CrewBusy => _roller != null && _roller.Active && (State == RugState.Rising || State == RugState.Falling);

        // Where the roll is when the rug is t of the way to rolled up, in screen pixels.
        private RollView ViewAt(double t)
        {
            double p = Math.Min(1, t / RollPortion);
            double bodyLeft = PadDip + FringeDip, right = _widthDip - PadDip;
            double length = RollLength(), finalRadius = RollRadius(length);
            double edge = right - (right - (bodyLeft + finalRadius)) * p;
            return new RollView
            {
                EdgeX = _rugX + edge * _wpf,
                Radius = RollRadius(length * p) * _wpf,
                Top = _rugY + (PadDip - 2) * _wpf,
                Height = (_heightDip - 2 * PadDip + 4) * _wpf,
            };
        }

        private void StartCrew(bool rollingUp)
        {
            if (_roller == null) _roller = new Roller(_geo, _hwnd);
            _rollTarget = rollingUp ? 1 : 0;
            _pushStarted = false;
            _roller.Start(rollingUp, ViewAt(rollingUp ? 0 : 1));
            EnsureFrames();
        }

        // The merchant is mid-job and someone wants it done: finish at once.
        private void SkipCrew()
        {
            _roller.Cancel();
            _pushStarted = false;
            bool up = _rollTarget >= 1;
            _rollAnim.Set(up ? 1 : 0);
            ApplyProgress(up ? 1 : 0);
            State = up ? RugState.Up : RugState.Down;
            if (up) Compact(); else PuffFlat();
            UpdateToolTip();
            SaveState();
        }

        public void RollUp()
        {
            if (_sweep != null) return;
            if (CrewBusy) { SkipCrew(); return; }
            if (IsRolled || _flattening) return;
            ReleaseHand();
            if (_engine != null)                           // smooth it flat first, then roll it
            {
                _pendingRoll = true;
                BeginFlatten();
                return;
            }
            Expand();
            State = RugState.Rising;
            UpdateToolTip();
            if (UseCrew) { StartCrew(true); return; }
            _rollAnim.Start(1, RollSeconds);
            EnsureFrames();
        }

        public void Unroll()
        {
            if (_sweep != null) return;
            if (CrewBusy) { SkipCrew(); return; }
            if (State == RugState.Down || State == RugState.Falling) return;
            _roller?.Cancel();
            Expand();
            State = RugState.Falling;
            UpdateToolTip();
            if (UseCrew) { StartCrew(false); return; }
            _rollAnim.Start(0, UnrollSeconds);
            EnsureFrames();
        }

        public void Quit()
        {
            _quitting = true;
            _watch.Stop();
            _dragWatch.Stop();
            _frames.Stop();
            _engine?.Dispose();
            _engine = null;
            _roller?.Dispose();
            SweptStore.Changed -= OnSweptChanged;
            if (_sweep != null) { _sweep.Abort(); _sweep.Dispose(); _sweep = null; }
            if (_tidy != null) { _tidy.FinishNow(); _tidy.Dispose(); _tidy = null; }
            Close();
        }

        // ================================================================== mouse

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (State == RugState.Up)                       // a click on the roll unrolls it; a drag moves it
            {
                Native.GetCursorPos(out _pressCursor);
                _pressX = _rugX;
                _pressY = _rugY;
                _rollPressed = true;
                _rollDragging = false;
                CaptureMouse();
                return;
            }
            if (State != RugState.Down || _flattening || _sweep != null) return;

            if (e.ClickCount == 2) { ReleaseHand(); RollUp(); e.Handled = true; return; }

            if (_engine == null) EnterCloth();
            Point hand = HandPosition();
            if (_engine.Grab((float)hand.X, (float)hand.Y))
            {
                _holding = true;
                Cursor = HandCursors.Closed;
                CaptureMouse();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_holding)
            {
                Point hand = HandPosition();
                _engine.MoveHand((float)hand.X, (float)hand.Y);
                return;
            }
            if (!_rollPressed) return;
            if (e.LeftButton != MouseButtonState.Pressed) { OnMouseUp(false); return; }

            Native.GetCursorPos(out Native.POINT now);
            int dx = now.X - _pressCursor.X, dy = now.Y - _pressCursor.Y;
            if (!_rollDragging)
            {
                if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
                _rollDragging = true;
                Cursor = HandCursors.Closed;
            }
            _rugX = _pressX + dx;
            _rugY = _pressY + dy;
            ClampToMonitor();
            Native.SetWindowPos(_hwnd, IntPtr.Zero, _rugX, _rugY, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // The button came up (clicked == true) or the press was cancelled.
        private void OnMouseUp(bool clicked)
        {
            if (_holding)
            {
                ReleaseHand();
                return;
            }
            if (!_rollPressed) return;

            bool moved = _rollDragging;
            _rollPressed = _rollDragging = false;
            Cursor = HandCursors.Open;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (moved) SaveState();
            else if (clicked) Unroll();                    // click the roll: it opens back out
        }

        // Let go of the handful of rug; it drops and settles.
        private void ReleaseHand()
        {
            if (!_holding) return;
            _holding = false;
            _engine?.Release();
            Cursor = HandCursors.Open;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        // Where the mouse is, in the cloth's coordinates (DIPs from the top-left of the monitor's usable area).
        private Point HandPosition()
        {
            Native.GetCursorPos(out Native.POINT p);
            return new Point((p.X - _geo.X) / _wpf, (p.Y - _geo.Y) / _wpf);
        }

        private void UpdateToolTip()
        {
            ToolTip = State == RugState.Up ? "Click to unroll the rug"
                    : State == RugState.Down ? "Grab it anywhere: lift it, drag it, fold a corner over. Double-click to roll it up"
                    : null;
        }

        // A flat or rolled rug can be slid anywhere on its own monitor, but not off it.
        private void ClampToMonitor()
        {
            int w = _compact ? _rolledW : _rugW;
            _rugX = Math.Max(_geo.X, Math.Min(_geo.X + _geo.W - w, _rugX));
            _rugY = Math.Max(_geo.Y, Math.Min(_geo.Y + _geo.H - _rugH, _rugY));
        }

        // ================================================================== from flat to cloth and back

        private double RoomWidthDip => _geo.W / _wpf;
        private double RoomHeightDip => _geo.H / _wpf;

        private void EnterCloth()
        {
            double offX = (_rugX - _geo.X) / _wpf, offY = (_rugY - _geo.Y) / _wpf;
            float meshW = (float)(_widthDip - 2 * PadDip), meshH = (float)(_heightDip - 2 * PadDip);
            var cloth = new Cloth((float)(offX + PadDip), (float)(offY + PadDip), meshW, meshH, (float)RoomWidthDip, (float)RoomHeightDip, ClothGap, _spec.Feel);

            ClearFlatDust();
            if (_clothBitmap == null)
            {
                _clothBitmap = new WriteableBitmap(_geo.W, _geo.H, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32, null);
                _clothImage.Source = _clothBitmap;
                _clothImage.Stretch = Stretch.None;
                RenderOptions.SetBitmapScalingMode(_clothImage, BitmapScalingMode.NearestNeighbor);
            }
            else
            {
                _clothBitmap.WritePixels(new Int32Rect(0, 0, _geo.W, _geo.H), new byte[_geo.W * _geo.H * 4], _geo.W * 4, 0);   // blank
            }

            _engine = new ClothEngine(cloth, _geo.W, _geo.H, (float)_wpf, _art.FacePx, _art.BackPx, _art.TexW, _art.TexH,
                                      (float)(RugArt.PadDip * _geo.Scale), Dispatcher,
                                      (area, picture) => _clothBitmap.WritePixels(area, picture, _geo.W * 4, area.X, area.Y));
            _engine.Settled += () => SaveState();

            _engine.SetBump(_bumpShade);
            _engine.ShowNow();                             // the rug as it is, on screen, before the window grows
            ShowCloth(true);
            ApplyBounds(zOrder: false, show: false);       // now the window covers the whole monitor
        }

        private void ShowCloth(bool cloth)
        {
            _front.Visibility = cloth ? Visibility.Collapsed : Visibility.Visible;
            _clothImage.Visibility = cloth ? Visibility.Visible : Visibility.Collapsed;
            _dustImage.Visibility = cloth ? Visibility.Collapsed : Visibility.Visible;
            _bumpImage.Visibility = cloth ? Visibility.Collapsed : Visibility.Visible;
            double w = cloth ? RoomWidthDip : _widthDip, h = cloth ? RoomHeightDip : _heightDip;
            _stage.Width = _content.Width = w;
            _stage.Height = _content.Height = h;
            _clothImage.Width = RoomWidthDip;
            _clothImage.Height = RoomHeightDip;
        }

        // Leave cloth mode: the rug lies flat where the cloth is now.
        private void ExitCloth()
        {
            if (_engine == null) return;
            double meshW = _widthDip - 2 * PadDip, meshH = _heightDip - 2 * PadDip;
            double winLeftDip = _engine.CentreX - meshW / 2 - PadDip, winTopDip = _engine.CentreY - meshH / 2 - PadDip;
            _rugX = _geo.X + (int)Math.Round(winLeftDip * _wpf);
            _rugY = _geo.Y + (int)Math.Round(winTopDip * _wpf);
            _engine.Dispose();
            _engine = null;
            _holding = false;
            ClampToMonitor();
            ShowCloth(false);
            ApplyBounds(zOrder: false, show: false);
            UpdateBumpOverlay();                           // the rug has a new place, so the lumps show through somewhere new
            SaveState();
        }

        private void BeginFlatten()
        {
            _flattening = true;
            _flattenT = 0;
            _engine.BeginFlatten((float)RoomWidthDip, (float)RoomHeightDip);
            EnsureFrames();
        }

        // ================================================================== setup

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _wpf = VisualTreeHelper.GetDpi(this).DpiScaleX;

            // A tool window that never takes focus: it must not show in the taskbar or Alt+Tab, or steal focus when clicked.
            long ex = Native.GetExStyle(_hwnd);
            Native.SetExStyle(_hwnd, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);

            Build();
            ApplyBounds(zOrder: true, show: false);

            // Moving onto a monitor with a different DPI makes WPF re-scale; pick that up once, here.
            _initializing = false;
            double now = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(now - _wpf) > 1e-6)
            {
                _wpf = now;
                Build();
                ApplyBounds(zOrder: true, show: false);
            }
            Log.Write("rug " + _rugW + "x" + _rugH + " on monitor at " + _geo.X + "," + _geo.Y + " " + _geo.W + "x" + _geo.H + " @" + _geo.Scale + "x, wpf scale " + _wpf);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_initializing || _quitting || _hwnd == IntPtr.Zero) return;
            _wpf = newDpi.DpiScaleX;
            _clothBitmap = null;
            _engine?.Dispose();
            _engine = null;
            _holding = _flattening = false;
            Build();
            ApplyBounds(zOrder: true, show: true);
        }

        private void Build()
        {
            _widthDip = _rugW / _wpf;
            _heightDip = _rugH / _wpf;
            if (_initializing) { Width = _widthDip; Height = _heightDip; }   // later, the window is sized natively

            _art = RugArt.RenderAll(_rugW, _rugH, _geo.Scale, _wpf, _spec.Style);
            _dustBitmap = null;
            _dustBuf = null;
            _dustImage.Source = null;
            _dustShown = new Int32Rect(0, 0, 0, 0);
            _flatDust.Clear();
            _dustImage.Width = _widthDip;
            _dustImage.Height = _heightDip;
            _dustImage.Stretch = Stretch.None;
            RenderOptions.SetBitmapScalingMode(_dustImage, BitmapScalingMode.NearestNeighbor);
            _bumpImage.Width = _widthDip;
            _bumpImage.Height = _heightDip;
            _bumpImage.Stretch = Stretch.None;
            RenderOptions.SetBitmapScalingMode(_bumpImage, BitmapScalingMode.NearestNeighbor);
            _front.Source = _art.Front;
            _front.Stretch = Stretch.None;
            _front.Width = _widthDip;
            _front.Height = _heightDip;
            RenderOptions.SetBitmapScalingMode(_front, BitmapScalingMode.NearestNeighbor);

            double rollTop = PadDip - 2, rollHeight = _heightDip - 2 * PadDip + 4;
            _rollShadow.Height = rollHeight;
            _rollShadow.Background = new LinearGradientBrush(
                Color.FromArgb(0x70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), new Point(0, 0), new Point(1, 0));

            _roll.Children.Clear();
            _roll.Height = rollHeight;
            _roll.Children.Add(new Border { Background = MakeStripes(_spec.RollColors), CornerRadius = new CornerRadius(3) });
            _roll.Children.Add(new Border { Background = MakeShading(), CornerRadius = new CornerRadius(3) });

            // Two straps tie the roll shut.
            foreach (Border strap in new[] { _strapA, _strapB })
            {
                strap.Height = 11;
                strap.CornerRadius = new CornerRadius(2);
                strap.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x48, 0x2C));
                strap.BorderThickness = new Thickness(0, 1, 0, 1);
                strap.Background = new LinearGradientBrush(Color.FromRgb(0xD8, 0xC3, 0x92), Color.FromRgb(0x9C, 0x84, 0x56), 90);
            }
            Canvas.SetTop(_roll, rollTop);
            Canvas.SetTop(_rollShadow, rollTop);
            Canvas.SetTop(_strapA, rollTop + rollHeight * 0.13 - 5);
            Canvas.SetTop(_strapB, rollTop + rollHeight * 0.87 - 5);

            _content.Children.Clear();
            foreach (UIElement el in new UIElement[] { _front, _bumpImage, _dustImage, _clothImage, _rollShadow, _roll, _strapA, _strapB })
                _content.Children.Add(el);
            ShowCloth(false);
            RefreshBumps();

            double finalRadius = RollRadius(RollLength());
            double rolledDip = PadDip + FringeDip + 2 * finalRadius + (2 * finalRadius * 0.75 + 8) + 6;
            _rolledW = (int)Math.Ceiling(rolledDip * _wpf);

            ApplyProgress(_rollAnim.Value);
        }

        // Rolled-up rug seen from above: the pattern wraps round a cylinder, so bands bunch up at the edges.
        private static Brush MakeStripes(Color[] cols)
        {
            const int bands = 18;
            var stops = new GradientStopCollection();
            for (int i = 0; i < bands; i++)
            {
                double o0 = (Math.Sin(-Math.PI / 2 + Math.PI * i / bands) + 1) / 2;
                double o1 = (Math.Sin(-Math.PI / 2 + Math.PI * (i + 1) / bands) + 1) / 2;
                Color c = cols[i % cols.Length];
                stops.Add(new GradientStop(c, o0));
                stops.Add(new GradientStop(c, o1));
            }
            var b = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
            b.Freeze();
            return b;
        }

        private static Brush MakeShading()
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.00));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 0.14));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0.34));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.52));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0, 0, 0), 0.80));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 1.00));
            b.Freeze();
            return b;
        }

        // ================================================================== living on the desktop

        // Exact pixel rectangle: the whole monitor while the rug is cloth; otherwise the flat rug, or just the roll.
        private void ApplyBounds(bool zOrder, bool show)
        {
            int x, y, w, h;
            if (_engine != null) { x = _geo.X; y = _geo.Y; w = _geo.W; h = _geo.H; }
            else { x = _rugX; y = _rugY; w = _compact ? _rolledW : _rugW; h = _rugH; }

            uint flags = Native.SWP_NOACTIVATE | (show ? Native.SWP_SHOWWINDOW : 0) | (zOrder ? 0 : Native.SWP_NOZORDER);
            Native.SetWindowPos(_hwnd, zOrder ? Native.HWND_BOTTOM : IntPtr.Zero, x, y, w, h, flags);
            if (zOrder) Native.SitOnDesktop(_hwnd);
        }

        // Make the window big enough for the whole rug again (before it unrolls, or while it rolls up).
        private void Expand()
        {
            if (!_compact) return;
            _compact = false;
            ClampToMonitor();
            ApplyBounds(zOrder: false, show: false);
        }

        // Shrink the window to just the roll, so the rest of the desktop is clear of it.
        private void Compact()
        {
            if (_compact) return;
            _compact = true;
            ClampToMonitor();
            ApplyBounds(zOrder: false, show: false);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            ApplyBounds(zOrder: true, show: false);   // WPF positions the window in its own terms when it first shows; restore ours
        }

        // ================================================================== icons dropped under the rug

        // When the mouse button goes down somewhere that isn't this rug (an icon on the desktop, a rubber-band selection, a
        // file in another window) and stays down, the rug becomes see-through to the mouse. The drag then lands on the desktop
        // beneath it, so an icon (or a whole selection of them) can be dropped right under the rug and simply stays there.
        // It solidifies again a moment after the button is released, once the drop has finished.
        private void WatchDrag()
        {
            if (_quitting || _hwnd == IntPtr.Zero) return;
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;      // left button
            bool ours = _holding || _rollPressed || IsMouseCaptured;

            if (down && !ours)
            {
                _upSince = 0;
                if (++_downPolls >= 2 && !_passThrough) SetPassThrough(true);
            }
            else if (down)
            {
                _downPolls = 0;
            }
            else
            {
                _downPolls = 0;
                if (!_passThrough) return;
                long now = Stopwatch.GetTimestamp();
                if (_upSince == 0) _upSince = now;
                else if ((now - _upSince) / (double)Stopwatch.Frequency > 0.35) SetPassThrough(false);
            }
        }

        private void SetPassThrough(bool on)
        {
            _passThrough = on;
            _upSince = 0;
            long ex = Native.GetExStyle(_hwnd);
            Native.SetExStyle(_hwnd, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
        }

        // Twice a second: keep the rug directly above the desktop (Explorer restarting can leave it beneath).
        private void Tick()
        {
            if (_quitting || _hwnd == IntPtr.Zero) return;
            if (!Native.IsAboveDesktop(_hwnd)) Native.SitOnDesktop(_hwnd);
        }

        // Where the rug is, for remembering: the flat window's corner, worked out from the cloth if it's in use.
        private void SaveState()
        {
            if (TestMode) return;
            CurrentCorner(out int x, out int y);
            RugPositions.Set(_geo, x - _geo.X, y - _geo.Y, State == RugState.Up);
        }

        /// <summary>Remember where the rug is now, and report where its middle is (screen pixels), for a replacement rug to take over.</summary>
        internal (int x, int y) SaveAndGetCentre()
        {
            SaveState();
            CurrentCorner(out int x, out int y);
            return (x + _rugW / 2, y + _rugH / 2);
        }

        private void CurrentCorner(out int x, out int y)
        {
            x = _rugX; y = _rugY;
            if (_engine != null)
            {
                double meshW = _widthDip - 2 * PadDip, meshH = _heightDip - 2 * PadDip;
                x = _geo.X + (int)Math.Round((_engine.CentreX - meshW / 2 - PadDip) * _wpf);
                y = _geo.Y + (int)Math.Round((_engine.CentreY - meshH / 2 - PadDip) * _wpf);
                x = Math.Max(_geo.X, Math.Min(_geo.X + _geo.W - _rugW, x));
                y = Math.Max(_geo.Y, Math.Min(_geo.Y + _geo.H - _rugH, y));
            }
        }

        // ================================================================== animation

        private void EnsureFrames()
        {
            if (!_frames.IsEnabled)
            {
                _lastTick = Stopwatch.GetTimestamp();
                _frames.Start();
            }
        }

        private void OnFrame()
        {
            try { Frame(); }
            catch (Exception ex)
            {
                // Never let a glitch in the animation take the app down: note it, and lay the rug flat.
                Log.Write("animation error: " + ex);
                _frames.Stop();
                _holding = _flattening = _pendingRoll = false;
                _roller?.Cancel();
                _pushStarted = false;
                if (_engine != null) { _engine.Release(); ExitCloth(); }
                if (_rollAnim.Active) { _rollAnim.Set(State == RugState.Rising || State == RugState.Up ? 1 : 0); }
                ApplyProgress(_rollAnim.Value);
                if (State == RugState.Rising) State = RugState.Up;
                if (State == RugState.Falling) State = RugState.Down;
                if (State == RugState.Up) Compact();
            }
        }

        // Runs while the rug is rolling or being flattened (the cloth itself runs on the engine's own threads).
        private void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
            _lastTick = now;
            bool busy = false;

            if (_rollAnim.Active)
            {
                busy = true;
                _rollAnim.Step(now);
                ApplyProgress(_rollAnim.Value);
                if (!_rollAnim.Active)
                {
                    State = _rollAnim.Value >= 1 ? RugState.Up : RugState.Down;
                    if (State == RugState.Up) Compact(); else PuffFlat();     // it has landed: a little dust
                    UpdateToolTip();
                    SaveState();
                }
            }

            if (_engine != null && _flattening)
            {
                busy = true;
                _flattenT += elapsed / FlattenSeconds;
                _engine.FlattenStep((float)Math.Min(1, _flattenT));
                if (_flattenT >= 1)
                {
                    _flattening = false;
                    ExitCloth();
                    if (_pendingRoll) { _pendingRoll = false; RollUp(); busy = true; }
                }
            }

            if (_pendingTidy)                                 // wait until the rug is out, flat and nobody else is working on it
            {
                busy = true;
                if (State == RugState.Up) Unroll();
                else if (ReadyForWork) { _pendingTidy = false; StartTidy(); }
            }

            if (_pendingSweep && !_pendingTidy)
            {
                busy = true;
                if (State == RugState.Up) Unroll();
                else if (ReadyForWork) { _pendingSweep = false; StartSweep(); }
            }

            if (_tidy != null || _sweep != null) _jobGaps.Add((now - _lastJobTick) * 1000.0 / Stopwatch.Frequency);
            _lastJobTick = now;

            if (_tidy != null)
            {
                busy = true;
                if (!_tidy.Update(elapsed))
                {
                    int files = _tidy.FilesMoved, folders = _tidy.FolderCount;
                    _tidy.Dispose();
                    _tidy = null;
                    if (!_quietJobs) Notice?.Invoke("Sorted " + files + " files into " + folders + " folders. (Right-click the rug to undo, or to sweep the folders under the rug.)");
                    LogJobSmoothness("sort");
                    TidyDone?.Invoke();
                }
            }

            if (_sweep != null)
            {
                busy = true;
                if (!_sweep.Update(elapsed))
                {
                    LogJobSmoothness("sweep");
                    _sweep.Dispose();
                    _sweep = null;
                    PuffFlat();                                   // everything has gone under: a puff of dust, and the rug is lumpy
                    RefreshBumps();
                }
            }

            if (_roller != null && _roller.Active)
            {
                busy = true;
                double v = _rollAnim.Value;
                RollView view = ViewAt(v);
                bool up = _rollTarget >= 1;
                view.Fraction = up ? v : 1 - v;
                view.JobDone = _pushStarted && !_rollAnim.Active;
                _roller.Update(elapsed, view);
                if (_roller.PushRequested && !_pushStarted)       // he has hold of it: the rug starts to move
                {
                    _pushStarted = true;
                    _rollAnim.Start(_rollTarget, up ? CrewRollUpSeconds : CrewUnrollSeconds);
                }
            }

            if (_flatDust.Active || _dustShown.Width > 0)
            {
                _flatDust.Step((float)elapsed);
                DrawFlatDust();
                if (_flatDust.Active || _dustShown.Width > 0) busy = true;
            }

            if (!busy) _frames.Stop();
        }

        // ================================================================== the tidy-up

        public DeskGeometry Geometry => _geo;

        /// <summary>Raised when a tidy-up has finished (or there was nothing to tidy).</summary>
        public event Action TidyDone;

        // Ready for the merchant: the rug is out, flat, and nobody else is working on it.
        private bool ReadyForWork => State == RugState.Down && !_flattening && _engine == null && (_roller == null || !_roller.Active) && _sweep == null && _tidy == null;

        /// <summary>
        /// The merchant sorts the desktop's loose files into folders by type, as soon as the rug is out and free. <paramref name="quiet"/>
        /// (when the rug starts) means no messages unless something goes wrong.
        /// </summary>
        public void Tidy(bool quiet = false)
        {
            if (_tidy != null) { _tidy.FinishNow(); return; }       // asking again hurries him along
            _quietJobs = quiet;
            _pendingTidy = true;
            if (_engine != null && !_flattening) BeginFlatten();
            EnsureFrames();
        }

        private long _lastJobTick;

        private void LogJobSmoothness(string job)
        {
            if (_jobGaps.Count < 10) { _jobGaps.Clear(); return; }
            _jobGaps.RemoveAt(0);
            var sorted = new System.Collections.Generic.List<double>(_jobGaps);
            sorted.Sort();
            Log.Write(job + ": " + sorted.Count + " frames, gap median " + sorted[sorted.Count / 2].ToString("F1") + " ms, 95th " + sorted[(int)(sorted.Count * 0.95)].ToString("F1") + " ms, worst " + sorted[sorted.Count - 1].ToString("F1") + " ms");
            _jobGaps.Clear();
        }

        private void StartTidy()
        {
            var area = new Rect(_rugX, _rugY, _rugW, _rugH);
            TidyJob job = TidyJob.TryCreate(_geo, _hwnd, area, out string problem);
            if (job == null)
            {
                if (problem != null && !_quietJobs) Notice?.Invoke(problem);
                TidyDone?.Invoke();
                return;
            }
            _tidy = job;
            EnsureFrames();
        }

        // ================================================================== the broom

        /// <summary>Sweep this monitor's desktop folders in under the rug (unrolling and flattening it first if need be).</summary>
        public void Sweep(bool quiet = false)
        {
            if (_sweep != null) return;
            _quietJobs = quiet;
            _pendingSweep = true;
            if (_engine != null && !_flattening) BeginFlatten();
            EnsureFrames();
        }

        private void StartSweep()
        {
            Rect b = RugArt.BodyRect(_rugW, _rugH, _geo.Scale);
            var body = new Rect(_rugX + b.Left, _rugY + b.Top, b.Width, b.Height);
            SweepJob job = SweepJob.TryCreate(_geo, body, _hwnd, out string problem);
            if (job == null) { if (problem != null && !_quietJobs) Notice?.Invoke(problem); return; }
            _sweep = job;
            EnsureFrames();
        }

        private void OnSweptChanged()
        {
            if (_quitting) return;
            Dispatcher.BeginInvoke(new Action(() => { if (!_quitting) RefreshBumps(); }));
        }

        // The lumps under the rug come from the icons swept under it (where they are on the screen, not where the rug is).
        private void RefreshBumps()
        {
            _bumpShade = BumpField.Build(BumpField.BlobsFor(_geo), _geo.W, _geo.H);
            _engine?.SetBump(_bumpShade);
            UpdateBumpOverlay();
        }

        private void UpdateBumpOverlay()
        {
            if (_bumpShade == null || _art == null) { _bumpImage.Source = null; return; }
            byte[] px = BumpField.Overlay(_bumpShade, _geo.W, _geo.H, _rugX - _geo.X, _rugY - _geo.Y, _rugW, _rugH, _art.FacePx);
            BitmapSource overlay = BitmapSource.Create(_rugW, _rugH, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32, null, px, _rugW * 4);
            overlay.Freeze();
            _bumpImage.Source = overlay;
        }

        // ================================================================== dust on the flat rug

        // The rug has just come down: air pushed out from under it puffs out of every side.
        private void PuffFlat()
        {
            if (_engine != null || _compact || TestMode) return;
            Rect b = RugArt.BodyRect(_rugW, _rugH, _geo.Scale);
            double gap = 20 * _geo.Scale;
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side < 2;                                  // top and bottom edges run along x
                double length = horizontal ? b.Width : b.Height;
                int n = Math.Max(2, (int)(length / gap));
                for (int i = 0; i < n; i++)
                {
                    double t = (i + _dustRandom.NextDouble()) / n * length;
                    double x = horizontal ? b.Left + t : (side == 2 ? b.Left : b.Right);
                    double y = horizontal ? (side == 0 ? b.Top : b.Bottom) : b.Top + t;
                    float nx = horizontal ? 0 : (side == 2 ? -1 : 1), ny = horizontal ? (side == 0 ? -1 : 1) : 0;
                    _flatDust.Puff((float)x, (float)y, nx, ny, 0.55f + 0.4f * (float)_dustRandom.NextDouble());
                }
            }
            EnsureFrames();
        }

        private static Int32Rect UnionRect(Int32Rect a, Int32Rect b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            return new Int32Rect(x0, y0, Math.Max(a.X + a.Width, b.X + b.Width) - x0, Math.Max(a.Y + a.Height, b.Y + b.Height) - y0);
        }

        private void DrawFlatDust()
        {
            if (_dustBitmap == null)
            {
                _dustBitmap = new WriteableBitmap(_rugW, _rugH, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32, null);
                _dustBuf = new byte[_rugW * _rugH * 4];
                _dustImage.Source = _dustBitmap;
            }
            _flatDust.CopyTo(_dustView);
            Int32Rect now = Dust.Bounds(_dustView, 1f, _rugW, _rugH);
            Int32Rect wipe = UnionRect(_dustShown, now);
            if (wipe.Width <= 0 || wipe.Height <= 0) { _dustShown = now; return; }

            for (int y = wipe.Y; y < wipe.Y + wipe.Height; y++) Array.Clear(_dustBuf, (y * _rugW + wipe.X) * 4, wipe.Width * 4);
            Dust.Draw(_dustBuf, _rugW, _rugH, now, _dustView, 1f, _art.FacePx, (int)(12 * _geo.Scale));
            _dustBitmap.WritePixels(wipe, _dustBuf, _rugW * 4, wipe.X, wipe.Y);
            _dustShown = now;
        }

        private void ClearFlatDust()
        {
            _flatDust.Clear();
            if (_dustBitmap != null && _dustShown.Width > 0)
            {
                Array.Clear(_dustBuf, 0, _dustBuf.Length);
                _dustBitmap.WritePixels(new Int32Rect(0, 0, _rugW, _rugH), _dustBuf, _rugW * 4, 0, 0);
            }
            _dustShown = new Int32Rect(0, 0, 0, 0);
        }

        // ================================================================== self-test: how smooth is it really?

        internal void PrepareForTest()
        {
            State = RugState.Down;
            _compact = false;
            _rollAnim.Set(0);
            ApplyProgress(0);
            ClampToMonitor();
            ApplyBounds(zOrder: true, show: true);
            _watch.Start();
        }

        private static string Describe(string name, System.Collections.Generic.List<double> v)
        {
            if (v == null || v.Count == 0) return name + ": (none)";
            var sorted = new System.Collections.Generic.List<double>(v);
            sorted.Sort();
            double avg = 0; foreach (double d in v) avg += d; avg /= v.Count;
            return string.Format("{0,-30} avg {1,6:F1}   median {2,6:F1}   95th {3,6:F1}   worst {4,6:F1}", name, avg, sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.95)], sorted[sorted.Count - 1]);
        }

        /// <summary>
        /// Runs a temporary rug on the desktop for a few seconds with a scripted hand, and writes how smoothly it ran.
        /// It never touches the saved rug.   AladdinRug.exe --selftest report.txt
        /// </summary>
        internal static void RunSelfTest(string reportPath)
        {
            var rug = new RugWindow(DeskGeometry.Primary()) { TestMode = true };
            rug.Show();
            rug.PrepareForTest();

            int presented = 0;
            EventHandler counter = (s, e) => presented++;
            var hand = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            long start = 0;
            string report = "";
            int gc0 = 0, gc1 = 0, gc2 = 0;
            double gcPause = 0;

            var begin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            begin.Tick += (s, e) =>
            {
                begin.Stop();
                rug.EnterCloth();
                ClothEngine engine = rug._engine;
                engine.StatPhysics = new System.Collections.Generic.List<double>();
                engine.StatDraw = new System.Collections.Generic.List<double>();
                engine.StatUpload = new System.Collections.Generic.List<double>();
                engine.StatUploadGap = new System.Collections.Generic.List<double>();
                float cx = engine.CentreX, cy = engine.CentreY;
                engine.Grab(cx - 120, cy);
                rug._holding = true;
                CompositionTarget.Rendering += counter;
                gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2); gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
                for (int i = 0; i < ClothRenderer.Profile.Length; i++) ClothRenderer.Profile[i] = 0;
                start = Stopwatch.GetTimestamp();
                hand.Start();
                hand.Tick += (s2, e2) =>
                {
                    double t = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                    engine.MoveHand(cx - 120 + 230 * (float)Math.Sin(t * 3.2), cy + 120 * (float)Math.Sin(t * 2.1));
                    if (t > 6)
                    {
                        hand.Stop();
                        CompositionTarget.Rendering -= counter;
                        report = "rug pictures shown on screen: " + engine.StatUploadGap.Count + " in " + t.ToString("F1") + " s  =  "
                                 + (engine.StatUploadGap.Count / t).ToString("F1") + " per second   (window redraws per second: " + (presented / t).ToString("F1") + ")\r\n"
                                 + Describe("gap between pictures ms", engine.StatUploadGap) + "\r\n"
                                 + Describe("physics step ms (own thread)", engine.StatPhysics) + "\r\n"
                                 + Describe("drawing ms (own thread)", engine.StatDraw) + "\r\n"
                                 + Describe("copy to screen ms (window thread)", engine.StatUpload) + "\r\n"
                                 + "physics steps run: " + engine.StatPhysics.Count + " in 6 s (60 a second is real time = 360)\r\n"
                                 + "drawing phases, average ms: smooth " + (ClothRenderer.Profile[0] / engine.StatDraw.Count).ToString("F1")
                                 + ", clear " + (ClothRenderer.Profile[1] / engine.StatDraw.Count).ToString("F1")
                                 + ", sort " + (ClothRenderer.Profile[2] / engine.StatDraw.Count).ToString("F1")
                                 + ", pixels " + (ClothRenderer.Profile[3] / engine.StatDraw.Count).ToString("F1")
                                 + ", shadows " + (ClothRenderer.Profile[4] / engine.StatDraw.Count).ToString("F1") + "\r\n"
                                 + "garbage collections during the run: gen0 " + (GC.CollectionCount(0) - gc0) + ", gen1 " + (GC.CollectionCount(1) - gc1)
                                 + ", gen2 " + (GC.CollectionCount(2) - gc2) + ", total pause " + (GC.GetTotalPauseDuration().TotalMilliseconds - gcPause).ToString("F0") + " ms\r\n";
                        rug.ReleaseHand();
                        var finish = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                        finish.Tick += (s3, e3) =>
                        {
                            finish.Stop();
                            try { File.WriteAllText(reportPath, report); } catch (IOException) { }
                            rug.Quit();
                            Application.Current.Shutdown();
                        };
                        finish.Start();
                    }
                };
            };
            begin.Start();
        }

        // Radius of a roll that has taken up `length` DIPs of rug.
        private static double RollRadius(double length) => Math.Sqrt(RollThickness * length / Math.PI) + 1.5;

        private double RollLength() => (_widthDip - PadDip) - (PadDip + FringeDip);

        // t: 0 = rug flat, 1 = rolled up. The roll sits on the edge of the remaining flat rug and comes to rest at
        // the rug's left end, just clear of the fringe, which is left lying out beside it.
        private void ApplyProgress(double t)
        {
            double p = Math.Min(1, t / RollPortion);
            double straps = Math.Max(0, (t - RollPortion) / (1 - RollPortion));

            double bodyLeft = PadDip + FringeDip, right = _widthDip - PadDip;
            double length = RollLength();
            double finalRadius = RollRadius(length);
            double edge = right - (right - (bodyLeft + finalRadius)) * p;       // where the flat rug ends, and the roll sits

            bool rolling = t > 0.0005;
            Visibility v = rolling ? Visibility.Visible : Visibility.Collapsed;
            _roll.Visibility = _rollShadow.Visibility = v;
            _strapA.Visibility = _strapB.Visibility = straps > 0 ? Visibility.Visible : Visibility.Collapsed;

            _front.Clip = rolling ? new RectangleGeometry(new Rect(0, 0, Math.Max(0.01, edge), _heightDip)) : null;
            _bumpImage.Clip = _front.Clip;
            if (!rolling) return;

            double diameter = 2 * RollRadius(length * p);
            _roll.Width = diameter;
            Canvas.SetLeft(_roll, edge - diameter / 2);
            _rollShadow.Width = diameter * 0.75 + 8;
            Canvas.SetLeft(_rollShadow, edge + diameter / 2 - 2);

            foreach (Border strap in new[] { _strapA, _strapB })
            {
                strap.Width = diameter + 6;
                strap.Opacity = straps;
                Canvas.SetLeft(strap, edge - diameter / 2 - 3);
            }
        }

        // ================================================================== misc

        /// <summary>
        /// Draw the rug in a given state to a PNG, without showing anything:
        /// AladdinRug.exe --preview roll|rollmid|flat|hold out.png.  "hold" picks the rug up in the middle and drags it.
        /// </summary>
        public void RenderPreview(string kind, string path)
        {
            _ = new WindowInteropHelper(this).EnsureHandle();    // creates the window (not shown), which builds the pictures
            _compact = false;

            if (kind == "stuffed")        // the lumps of a pile of icons under the rug, with pretend icons
            {
                _rollAnim.Set(0);
                ApplyProgress(0);
                var blobs = new System.Collections.Generic.List<Blob>();
                Rect body = RugArt.BodyRect(_rugW, _rugH, _geo.Scale);
                double cw = 95 * _geo.Scale, ch = 107 * _geo.Scale;
                int n = 0;
                for (double y = body.Top + 8; y + ch < body.Bottom - 6; y += ch)
                    for (double x = body.Left + 8; x + cw < body.Right - 6; x += cw, n++)
                    {
                        int count = new[] { 3, 9, 1, 6, 12, 2, 5, 8, 4, 10, 1, 7, 3, 9, 2, 6, 5, 4 }[n % 18];
                        blobs.Add(new Blob { X = (float)(_rugX - _geo.X + x + cw * 0.5), Y = (float)(_rugY - _geo.Y + y + ch * 0.40), Rx = (float)(cw * (0.30 + 0.015 * Math.Min(count, 6))), Ry = (float)(ch * (0.27 + 0.015 * Math.Min(count, 6))), Height = 6f + 4.5f * Math.Min(count, 7) });
                    }
                _bumpShade = BumpField.Build(blobs, _geo.W, _geo.H);
                UpdateBumpOverlay();
            }
            else if (kind == "hold")
            {
                _rollAnim.Set(0);
                ApplyProgress(0);
                State = RugState.Down;
                double offX = (_rugX - _geo.X) / _wpf, offY = (_rugY - _geo.Y) / _wpf;
                float meshW = (float)(_widthDip - 2 * PadDip), meshH = (float)(_heightDip - 2 * PadDip);
                var cloth = new Cloth((float)(offX + PadDip), (float)(offY + PadDip), meshW, meshH, (float)RoomWidthDip, (float)RoomHeightDip, ClothGap, _spec.Feel);
                var renderer = new ClothRenderer(cloth, _geo.W, _geo.H, (float)_wpf, _art.FacePx, _art.BackPx, _art.TexW, _art.TexH, (float)(RugArt.PadDip * _geo.Scale));
                float cx = cloth.CentreX, cy = cloth.CentreY;
                cloth.Grab(cx - 100, cy);
                for (int f = 0; f < 80; f++)
                {
                    cloth.MoveHand(cx - 100 + 260 * Math.Min(1, f / 60f), cy + 20 * Math.Min(1, f / 60f));
                    cloth.Step(1f / 60f);
                }
                var buffer = new byte[_geo.W * _geo.H * 4];
                renderer.Render(buffer);
                var shown = new WriteableBitmap(_geo.W, _geo.H, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32, null);
                shown.WritePixels(new Int32Rect(0, 0, _geo.W, _geo.H), buffer, _geo.W * 4, 0);
                SaveClothPreview(shown, path);
                return;
            }

            switch (kind)
            {
                case "rollmid": _rollAnim.Set(0.5); ApplyProgress(0.5); break;
                case "roll": _rollAnim.Set(1); ApplyProgress(1); break;
                default: _rollAnim.Set(0); ApplyProgress(0); break;
            }

            var size = new Size(_widthDip, _heightDip);
            _stage.Measure(size);
            _stage.Arrange(new Rect(size));
            _stage.UpdateLayout();

            var layer = new RenderTargetBitmap(_rugW, _rugH, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32);
            layer.Render(_stage);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5C)), null, new Rect(size));
                dc.DrawImage(layer, new Rect(size));
            }
            var bmp = new RenderTargetBitmap(_rugW, _rugH, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        /// <summary>One picture of the merchant rolling the rug: the rug's own layer, and where the merchant stands (monitor pixels).</summary>
        internal struct RollFrame
        {
            public BitmapSource Layer;             // the rug window's picture
            public Rect LayerBounds;               // where it goes on the monitor
            public MerchantPose Pose;
            public double MerchantX, MerchantY, Unit;
            public double Time;
            public bool MerchantHere;
        }

        /// <summary>The flat rug's picture (face with shadow) and where it lies on the monitor, for the dev tools.</summary>
        internal BitmapSource FlatPicture => _art.Front;
        internal RugArt.Images Art => _art;
        internal Rect FlatBounds => new Rect(_rugX - _geo.X, _rugY - _geo.Y, _rugW, _rugH);

        /// <summary>
        /// Plays the merchant rolling the rug up (or out) in made-up time, with no window, one frame per step of
        /// <paramref name="dt"/> seconds, until he has walked away. Used by the dev tools and the promo video.
        /// </summary>
        internal IEnumerable<RollFrame> PlayRoll(bool up, double dt)
        {
            _ = new WindowInteropHelper(this).EnsureHandle();
            _compact = false;

            long clock = 0;
            Anim.Now = () => clock;
            _rollAnim.Set(up ? 0 : 1);
            ApplyProgress(up ? 0 : 1);
            State = up ? RugState.Rising : RugState.Falling;

            var crew = new Roller(_geo, IntPtr.Zero, headless: true);
            crew.Start(up, ViewAt(up ? 0 : 1));
            _rollTarget = up ? 1 : 0;
            _pushStarted = false;

            try
            {
                for (int f = 0; f < 60 * 30; f++)
                {
                    double t = f * dt;
                    clock = (long)(t * Stopwatch.Frequency);
                    if (_rollAnim.Active) _rollAnim.Step(clock);
                    double v = _rollAnim.Value;
                    ApplyProgress(v);
                    RollView view = ViewAt(v);
                    view.Fraction = up ? v : 1 - v;
                    view.JobDone = _pushStarted && !_rollAnim.Active;
                    crew.Update(dt, view);
                    if (crew.PushRequested && !_pushStarted)
                    {
                        _pushStarted = true;
                        _rollAnim.Start(_rollTarget, up ? CrewRollUpSeconds : CrewUnrollSeconds);
                    }

                    var size = new Size(_widthDip, _heightDip);
                    _stage.Measure(size);
                    _stage.Arrange(new Rect(size));
                    _stage.UpdateLayout();
                    var layer = new RenderTargetBitmap(_rugW, _rugH, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32);
                    layer.Render(_stage);
                    layer.Freeze();

                    yield return new RollFrame
                    {
                        Layer = layer, LayerBounds = new Rect(_rugX - _geo.X, _rugY - _geo.Y, _rugW, _rugH), Pose = crew.Pose,
                        MerchantX = crew.X - _geo.X, MerchantY = crew.Y - _geo.Y, Unit = crew.Unit, Time = t, MerchantHere = crew.Active,
                    };
                    if (!crew.Active && f > 30) yield break;
                }
            }
            finally { Anim.Now = Stopwatch.GetTimestamp; }
        }

        /// <summary>
        /// Plays the merchant rolling the rug up (or out) in made-up time, with no window, and saves the monitor-sized
        /// pictures at chosen moments: AladdinRug.exe --sequence up|out folder
        /// </summary>
        public void RenderSequence(string kind, string dir)
        {
            bool up = kind == "up";
            Directory.CreateDirectory(dir);
            double[] shots = { 0.3, 0.9, 1.5, 2.1, 3.0, 3.9, 4.8, 5.4, 6.1, 7.0 };
            int next = 0;
            foreach (RollFrame frame in PlayRoll(up, 1.0 / 60))
            {
                if (next >= shots.Length) break;
                if (frame.Time < shots[next]) continue;
                SaveSequenceFrame(Path.Combine(dir, kind + "_" + next + ".png"), frame);
                Console.WriteLine("{0}: t={1:F1}s, merchant at {2:F0},{3:F0}", kind, frame.Time, frame.MerchantX, frame.MerchantY);
                next++;
            }
        }

        private void SaveSequenceFrame(string path, RollFrame frame)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5C)), null, new Rect(0, 0, _geo.W, _geo.H));
                dc.DrawImage(frame.Layer, frame.LayerBounds);
                MerchantArt.Draw(dc, frame.Pose, frame.MerchantX, frame.MerchantY, frame.Unit);
            }
            var bmp = new RenderTargetBitmap(_geo.W, _geo.H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            int x0 = Math.Max(0, _rugX - _geo.X - 260), y0 = Math.Max(0, _rugY - _geo.Y - 120);
            int w = Math.Min(_geo.W - x0, _rugW + 520), h = Math.Min(_geo.H - y0, _rugH + 240);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bmp, new Int32Rect(x0, y0, w, h))));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        // The cloth picture over a plain "wallpaper", the size of the monitor.
        private void SaveClothPreview(BitmapSource picture, string path)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var room = new Rect(0, 0, RoomWidthDip, RoomHeightDip);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5C)), null, room);
                dc.DrawImage(picture, room);
            }
            var bmp = new RenderTargetBitmap(_geo.W, _geo.H, 96 * _wpf, 96 * _wpf, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path)) enc.Save(fs);
        }

        /// <summary>A value that eases towards a target over time.</summary>
        private sealed class Anim
        {
            public static double InOutCubic(double k) => k < 0.5 ? 4 * k * k * k : 1 - Math.Pow(-2 * k + 2, 3) / 2;

            private readonly Func<double, double> _ease;
            private double _from, _to, _seconds;
            private long _start;

            /// <summary>The clock (replaceable, so a test can run the animation in its own time).</summary>
            public static Func<long> Now = Stopwatch.GetTimestamp;

            public Anim(double value, Func<double, double> ease) { Value = value; _ease = ease; }

            public double Value { get; private set; }
            public bool Active { get; private set; }

            public void Set(double v) { Value = v; Active = false; }

            // Takes `fullSeconds` to travel the whole 0..1 range, proportionally less for a shorter trip.
            public void Start(double to, double fullSeconds)
            {
                _from = Value;
                _to = to;
                _seconds = fullSeconds * Math.Abs(to - _from);
                _start = Now();
                Active = _seconds >= 0.02;
                if (!Active) Value = to;
            }

            // Returns true while still moving.
            public bool Step(long now)
            {
                double k = Math.Min(1, (now - _start) / (double)Stopwatch.Frequency / _seconds);
                Value = _from + (_to - _from) * _ease(k);
                if (k >= 1) { Value = _to; Active = false; }
                return Active;
            }
        }
    }
}
