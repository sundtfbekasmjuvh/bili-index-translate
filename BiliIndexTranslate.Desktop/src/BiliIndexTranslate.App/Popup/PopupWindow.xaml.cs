using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App.Popup;

/// <summary>
/// The reading card. A non-activating, tool-style, topmost WPF window: it never takes keyboard focus,
/// never appears in Alt+Tab or the taskbar, and all of its controls are non-focusable so that a click
/// inside it leaves the source application's focus and selection untouched.
/// </summary>
public partial class PopupWindow : Window
{
    /// <summary>Logical margin around the card reserved for the shadow (matches Root.Margin in XAML).</summary>
    public const double ShadowMarginLogical = 14;

    private const double ParagraphFontSize = 15;
    private const double CompactFontSize = 20;
    private const double OriginalFontSize = 12.5;

    private double _textScale = 1.0;
    private ReadingLayout _layout = ReadingLayout.Paragraph;
    private bool _showOriginal;
    private bool _dragging;
    private Point _dragStart;
    private PxPoint _dragOrigin;
    private readonly bool _animations = SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    public PopupWindow()
    {
        InitializeComponent();
        Card.MouseEnter += (_, _) => SetControlsVisible(true);
        Card.MouseLeave += (_, _) => SetControlsVisible(IsPinned);
        Card.MouseLeftButtonDown += OnCardMouseDown;
        Card.MouseMove += OnCardMouseMove;
        Card.MouseLeftButtonUp += OnCardMouseUp;
    }

    public event Action? CopyRequested;
    public event Action? PinRequested;
    public event Action? CloseRequested;
    public event Action? RetryRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? OriginalToggled;
    /// <summary>Raised after a pinned card has been dragged to a new place.</summary>
    public event Action? Moved;

    public IntPtr Hwnd { get; private set; }
    public bool IsPinned { get; private set; }
    public string? CurrentTranslation { get; private set; }
    public string? CurrentSource { get; private set; }

    public void EnsureHandle()
    {
        if (Hwnd != IntPtr.Zero) return;
        var helper = new WindowInteropHelper(this);
        Hwnd = helper.EnsureHandle();
        ApplyNonActivatingStyles();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        ApplyNonActivatingStyles();
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
    }

    private void ApplyNonActivatingStyles()
    {
        if (Hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtrW(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtrW(Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // ---- content states ----------------------------------------------------------------------

    public void ShowLoading(SelectionSnapshot snapshot, string? caption)
    {
        CurrentSource = snapshot.Text;
        CurrentTranslation = null;
        _layout = ReadingLayoutClassifier.Classify(snapshot.Text, null);
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Collapsed;
        OriginalPanel.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        Caption.Text = caption ?? string.Empty;
        CopyButton.IsEnabled = false;
        PinButton.IsEnabled = false;
        StartLoadingPulse();
        ApplyTypography();
    }

    public void ShowTranslation(SelectionSnapshot snapshot, string translation, string? caption)
    {
        StopLoadingPulse();
        CurrentSource = snapshot.Text;
        CurrentTranslation = translation;
        _layout = ReadingLayoutClassifier.Classify(snapshot.Text, translation);
        TranslationText.Text = translation;
        OriginalText.Text = snapshot.Text.Trim();
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Visible;
        BodyScroll.ScrollToTop();
        Footer.Visibility = Visibility.Visible;
        Caption.Text = caption ?? string.Empty;
        CopyButton.IsEnabled = true;
        PinButton.IsEnabled = !IsPinned;
        OriginalPanel.Visibility = _showOriginal ? Visibility.Visible : Visibility.Collapsed;
        ApplyTypography();
    }

    public void ShowError(SelectionSnapshot snapshot, TranslationError error, string? caption)
    {
        StopLoadingPulse();
        CurrentSource = snapshot.Text;
        CurrentTranslation = null;
        _layout = ReadingLayout.Paragraph;
        ErrorTitle.Text = error.Kind switch
        {
            TranslationErrorKind.Timeout => "No response",
            TranslationErrorKind.Network => "Connection problem",
            TranslationErrorKind.Configuration => "Check settings",
            TranslationErrorKind.EmptyResult => "Empty translation",
            TranslationErrorKind.InvalidResponse => "Unexpected response",
            _ => "Translation failed",
        };
        ErrorMessage.Text = error.Message;
        ErrorHint.Text = error.Hint ?? string.Empty;
        ErrorHint.Visibility = string.IsNullOrEmpty(error.Hint) ? Visibility.Collapsed : Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Collapsed;
        OriginalPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        Caption.Text = caption ?? string.Empty;
        CopyButton.IsEnabled = false;
        PinButton.IsEnabled = false;
        ApplyTypography();
    }

    public void SetShowOriginal(bool show)
    {
        _showOriginal = show;
        OriginalButton.ToolTip = show ? "Hide original text" : "Show original text";
        OriginalButton.Foreground = show ? (Brush)FindResource("Popup.Accent") : (Brush)FindResource("Popup.TextSecondary");
        if (BodyScroll.Visibility == Visibility.Visible)
            OriginalPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;
        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        PinButton.ToolTip = pinned ? "Unpin and close" : "Keep this translation open (pinned window)";
        PinButton.IsEnabled = true;
        Card.BorderBrush = pinned ? (Brush)FindResource("Popup.PinnedBorder") : (Brush)FindResource("Popup.Border");
        Card.Cursor = pinned ? Cursors.SizeAll : null;
        if (pinned)
        {
            Caption.Text = "pinned · drag to move";
            SetControlsVisible(true);
        }
    }

    /// <summary>Applies the per-monitor size limits; values are logical units for this window's DPI.</summary>
    public void SetSizeLimits(double maxCardWidth, double maxBodyHeight)
    {
        Card.MaxWidth = Math.Max(120, maxCardWidth);
        BodyScroll.MaxHeight = Math.Max(48, maxBodyHeight);
    }

    public void ApplyTextScale(double scale)
    {
        _textScale = Math.Clamp(scale, 0.8, 1.6);
        ApplyTypography();
    }

    public double TextScale => _textScale;

    private void ApplyTypography()
    {
        var compact = _layout == ReadingLayout.Compact;
        var body = (compact ? CompactFontSize : ParagraphFontSize) * _textScale;
        TranslationText.FontSize = body;
        TranslationText.LineHeight = Math.Round(body * (compact ? 1.35 : 1.65));
        TranslationText.FontWeight = compact ? FontWeights.Medium : FontWeights.Normal;
        TranslationText.Margin = compact ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 2, 2);
        OriginalText.FontSize = OriginalFontSize * _textScale;
        OriginalText.LineHeight = Math.Round(OriginalFontSize * _textScale * 1.55);
        CardGrid.Margin = compact ? new Thickness(14, 9, 14, 6) : new Thickness(16, 12, 16, 8);
        Footer.Margin = compact ? new Thickness(0, 2, 0, 0) : new Thickness(0, 8, 0, 0);
    }

    // ---- interaction -----------------------------------------------------------------------------

    public void ScrollBy(int wheelDelta)
    {
        if (BodyScroll.Visibility != Visibility.Visible) return;
        var lines = -wheelDelta / 120.0 * 3;
        BodyScroll.ScrollToVerticalOffset(BodyScroll.VerticalOffset + lines * TranslationText.LineHeight);
    }

    public bool CanScroll => BodyScroll.Visibility == Visibility.Visible && BodyScroll.ScrollableHeight > 0;

    private void SetControlsVisible(bool visible)
    {
        var target = visible ? 1.0 : 0.0;
        if (_animations)
            Controls.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() });
        else
            Controls.Opacity = target;
    }

    public void FadeIn()
    {
        if (_animations)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        else
            Opacity = 1;
    }

    public void ResetOpacity()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
    }

    private void StartLoadingPulse()
    {
        if (!_animations) { LoadingDots.Opacity = 0.7; return; }
        var pulse = new DoubleAnimation(0.35, 0.9, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LoadingDots.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopLoadingPulse()
    {
        LoadingDots.BeginAnimation(OpacityProperty, null);
        LoadingDots.Opacity = 0.7;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        CopyRequested?.Invoke();
        var text = CurrentTranslation;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetDataObject(text, true);
            FlashCaption("Copied");
        }
        catch (Exception)
        {
            FlashCaption("Clipboard busy");
        }
    }

    private string? _captionBeforeFlash;

    private void FlashCaption(string text)
    {
        _captionBeforeFlash ??= Caption.Text;
        Caption.Text = text;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Caption.Text = _captionBeforeFlash ?? string.Empty;
            _captionBeforeFlash = null;
        };
        timer.Start();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (IsPinned) CloseRequested?.Invoke();
        else PinRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnOriginalClick(object sender, RoutedEventArgs e)
    {
        SetShowOriginal(!_showOriginal);
        OriginalToggled?.Invoke(_showOriginal);
    }

    // Pinned windows can be dragged by their card; the window is never activated while dragging.
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPinned || e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is not null) return;
        _dragging = true;
        _dragStart = PointToScreenPhysical(e.GetPosition(this));
        NativeMethods.GetWindowRect(Hwnd, out var r);
        _dragOrigin = new PxPoint(r.Left, r.Top);
        Card.CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = PointToScreenPhysical(e.GetPosition(this));
        var x = _dragOrigin.X + (int)(now.X - _dragStart.X);
        var y = _dragOrigin.Y + (int)(now.Y - _dragStart.Y);
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Card.ReleaseMouseCapture();
        Moved?.Invoke();
    }

    private Point PointToScreenPhysical(Point logical)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return logical;
        var screen = PointToScreen(logical); // already device pixels for the window's monitor
        return screen;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T t) return t;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>Window rectangle in physical pixels (including shadow margin), or null if not shown.</summary>
    public PxRect? PhysicalRect()
    {
        if (Hwnd == IntPtr.Zero || !IsVisible || !NativeMethods.GetWindowRect(Hwnd, out var r)) return null;
        return new PxRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Card rectangle in physical pixels (window rect minus the shadow margin at this window's DPI).</summary>
    public PxRect? CardPhysicalRect()
    {
        var w = PhysicalRect();
        if (w is not { } rect) return null;
        var inset = (int)Math.Round(ShadowMarginLogical * DpiScale());
        return new PxRect(rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
    }

    public double DpiScale()
    {
        if (Hwnd != IntPtr.Zero)
        {
            var dpi = NativeMethods.GetDpiForWindow(Hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }
}
