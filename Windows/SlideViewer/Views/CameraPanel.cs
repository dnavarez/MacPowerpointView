using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;

namespace SlideViewer.Views;

/// <summary>
/// Presenter-console camera preview: an on/off switch, a device picker when more
/// than one camera is attached, and a live aspect-fit preview.
///
/// Capture stays off until switched on, so the camera light never comes on
/// unasked, and the device is released when switched off or the show ends.
///
/// Robustness notes — a camera must never be able to take the app down:
/// * Displayed bitmaps are never disposed. Avalonia's renderer may still hold a
///   frame after it has been replaced, and freeing that native Skia memory is an
///   access violation no try/catch can recover from. The garbage collector
///   reclaims them safely instead.
/// * Frames are decoded on the UI thread (they are small and throttled), which
///   avoids cross-thread imaging hazards entirely.
/// * Every entry point from the capture thread is wrapped; failures switch the
///   camera off and show a message rather than propagating.
/// </summary>
public sealed class CameraPanel : UserControl
{
    private const int TargetFps = 15;

    private readonly ComboBox _devicePicker = new();
    private readonly ToggleSwitch _toggle = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Panel _previewHost = new();
    private readonly TextBlock _message = new()
    {
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10)
    };

    private readonly List<CaptureDeviceDescriptor> _devices = new();
    private CaptureDevice? _device;
    private long _lastFrameTicks;
    private volatile bool _pending;
    private volatile bool _running;

    public CameraPanel()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "Camera",
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_toggle, 2);
        _toggle.IsCheckedChanged += OnToggled;
        header.Children.Add(_toggle);

        _devicePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        _devicePicker.Margin = new Thickness(0, 6, 0, 0);
        _devicePicker.IsVisible = false;
        _devicePicker.SelectionChanged += (_, _) =>
        {
            if (_running) { StopCapture(); StartCapture(); }
        };

        _previewHost.Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
        _previewHost.Children.Add(_message);
        _previewHost.Children.Add(_preview);
        _preview.IsVisible = false;
        _message.Text = "Camera off";

        var layout = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_devicePicker, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_devicePicker);
        layout.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 200)),
            BorderThickness = new Thickness(1),
            MinHeight = 110,
            Child = _previewHost
        });
        Content = layout;
    }

    private void OnToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_toggle.IsChecked == true) StartCapture();
            else StopCapture();
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Enumerates cameras; the picker appears only when there's a choice.</summary>
    public void RefreshDevices()
    {
        _devices.Clear();
        try
        {
            _devices.AddRange(new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Length > 0));
        }
        catch (Exception ex)
        {
            CrashReporter.Log(ex, "Camera: enumerating devices");
        }

        var selected = _devicePicker.SelectedIndex;
        _devicePicker.ItemsSource = _devices.Select(d => d.Name).ToList();
        if (_devices.Count > 0)
            _devicePicker.SelectedIndex = selected >= 0 && selected < _devices.Count ? selected : 0;
        _devicePicker.IsVisible = _devices.Count > 1;
    }

    // ── Capture lifecycle ───────────────────────────────────────────────────

    private async void StartCapture()
    {
        try
        {
            RefreshDevices();
            if (_devices.Count == 0)
            {
                ShowMessage("No camera detected");
                _toggle.IsChecked = false;
                return;
            }

            var index = Math.Clamp(_devicePicker.SelectedIndex, 0, _devices.Count - 1);
            var descriptor = _devices[index];
            ShowMessage("Starting camera...");

            // Prefer a modest resolution in a format we can decode: the preview is
            // small, and lower resolutions start faster and cost less per frame.
            var characteristics = descriptor.Characteristics
                .Where(c => c.PixelFormat != PixelFormats.Unknown)
                .OrderBy(c => Math.Abs(c.Width - 640) + Math.Abs(c.Height - 480))
                .FirstOrDefault()
                ?? descriptor.Characteristics.FirstOrDefault();

            if (characteristics == null)
            {
                ShowMessage("This camera reports no usable video format.");
                _toggle.IsChecked = false;
                return;
            }

            _lastFrameTicks = 0;
            var device = await descriptor.OpenAsync(characteristics, OnFrameArrived);
            _device = device;
            _running = true;
            await device.StartAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async void StopCapture()
    {
        _running = false;
        var device = _device;
        _device = null;

        // Drop the reference before tearing down so no late frame touches it.
        _preview.Source = null;
        _preview.IsVisible = false;
        ShowMessage("Camera off");

        if (device == null) return;
        try { await device.StopAsync(); } catch { }
        try { await device.DisposeAsync(); } catch { }
    }

    /// <summary>Called on a capture thread. Copies the frame out, releases the
    /// buffer, and hands the bytes to the UI thread — nothing imaging-related
    /// happens off the UI thread.</summary>
    private void OnFrameArrived(PixelBufferScope scope)
    {
        byte[]? image = null;
        try
        {
            if (!_running) return;

            // Throttle: skip frames rather than queueing work we can't keep up with.
            var now = DateTime.UtcNow.Ticks;
            var minInterval = TimeSpan.TicksPerSecond / TargetFps;
            if (_pending || (_lastFrameTicks != 0 && now - _lastFrameTicks < minInterval)) return;
            _lastFrameTicks = now;

            image = scope.Buffer.ExtractImage();
        }
        catch { return; }
        finally
        {
            try { scope.ReleaseNow(); } catch { }
        }

        if (image == null) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!_running) return;
                using var stream = new MemoryStream(image);
                var bitmap = new Bitmap(stream);
                // The outgoing bitmap is deliberately NOT disposed: the renderer
                // may still be using it. The GC reclaims it safely.
                _preview.Source = bitmap;
                _preview.IsVisible = true;
                _message.IsVisible = false;
            }
            catch { /* a dropped frame isn't worth surfacing */ }
            finally { _pending = false; }
        }, DispatcherPriority.Background);
    }

    /// <summary>Public stop used when the presentation ends.</summary>
    public void Stop()
    {
        try
        {
            if (_toggle.IsChecked == true) _toggle.IsChecked = false;  // triggers StopCapture
            else StopCapture();
        }
        catch (Exception ex) { CrashReporter.Log(ex, "Camera: stop"); }
    }

    /// <summary>Drives the on/off path once so a camera failure can never reach
    /// a user as a crash. Used by --selftest --camera.</summary>
    public void SelfTestToggle()
    {
        _toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(1500);
        Dispatcher.UIThread.RunJobs();
        _toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
    }

    private void Fail(Exception ex)
    {
        CrashReporter.Log(ex, "Camera");
        _running = false;
        ShowMessage($"Camera unavailable.\n{ex.Message}");
        try { _toggle.IsChecked = false; } catch { }
    }

    private void ShowMessage(string text)
    {
        void Apply()
        {
            _message.Text = text;
            _message.IsVisible = true;
            _preview.IsVisible = false;
            _preview.Source = null;
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}
