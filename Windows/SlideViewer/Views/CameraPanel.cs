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
/// Capture is off until switched on, so the camera light never comes on
/// unasked, and the device is released as soon as it is switched off or the
/// presentation ends.
/// </summary>
public sealed class CameraPanel : UserControl
{
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
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly List<CaptureDeviceDescriptor> _devices = new();
    private CaptureDevice? _device;
    private volatile bool _frameInFlight;

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
        _toggle.OnContent = null;
        _toggle.OffContent = null;
        Grid.SetColumn(_toggle, 2);
        _toggle.IsCheckedChanged += (_, _) => { if (_toggle.IsChecked == true) Start(); else Stop(); };
        header.Children.Add(_toggle);

        _devicePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        _devicePicker.Margin = new Thickness(0, 6, 0, 0);
        _devicePicker.IsVisible = false;
        _devicePicker.SelectionChanged += (_, _) =>
        {
            if (_toggle.IsChecked == true) { Stop(); Start(); }
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

    /// <summary>Enumerates cameras; shows the picker only when there is a choice.</summary>
    public void RefreshDevices()
    {
        _devices.Clear();
        try
        {
            _devices.AddRange(new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Length > 0));
        }
        catch { /* reported through the message label below */ }

        _devicePicker.ItemsSource = _devices.Select(d => d.Name).ToList();
        if (_devices.Count > 0 && _devicePicker.SelectedIndex < 0) _devicePicker.SelectedIndex = 0;
        _devicePicker.IsVisible = _devices.Count > 1;
    }

    private async void Start()
    {
        RefreshDevices();
        if (_devices.Count == 0)
        {
            ShowMessage("No camera detected");
            return;
        }

        var descriptor = _devices[Math.Max(0, Math.Min(_devicePicker.SelectedIndex, _devices.Count - 1))];
        ShowMessage("Starting camera…");
        try
        {
            // Prefer a modest resolution: the preview is small and lower
            // resolutions start faster and cost less per frame.
            var characteristics = descriptor.Characteristics
                .OrderBy(c => Math.Abs(c.Width - 640) + Math.Abs(c.Height - 480))
                .First();

            _device = await descriptor.OpenAsync(characteristics, OnFrameArrived);
            await _device.StartAsync();
        }
        catch (Exception ex)
        {
            ShowMessage($"Camera unavailable.\n{ex.Message}");
            await StopDeviceAsync();
            Dispatcher.UIThread.Post(() => _toggle.IsChecked = false);
        }
    }

    private async void OnFrameArrived(PixelBufferScope scope)
    {
        // Drop frames while one is still being decoded rather than queueing up.
        if (_frameInFlight) { scope.ReleaseNow(); return; }
        _frameInFlight = true;
        try
        {
            var image = scope.Buffer.ExtractImage();   // JPEG/DIB bytes
            scope.ReleaseNow();
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(image);
                return new Bitmap(stream);
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var previous = _preview.Source as Bitmap;
                _preview.Source = bitmap;
                _preview.IsVisible = true;
                _message.IsVisible = false;
                previous?.Dispose();
            });
        }
        catch { /* a dropped frame is not worth surfacing */ }
        finally { _frameInFlight = false; }
    }

    public void Stop()
    {
        _ = StopDeviceAsync();
        ShowMessage("Camera off");
    }

    private async Task StopDeviceAsync()
    {
        var device = _device;
        _device = null;
        if (device == null) return;
        try { await device.StopAsync(); } catch { }
        try { await device.DisposeAsync(); } catch { }
    }

    private void ShowMessage(string text)
    {
        void Apply()
        {
            _message.Text = text;
            _message.IsVisible = true;
            _preview.IsVisible = false;
            (_preview.Source as Bitmap)?.Dispose();
            _preview.Source = null;
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}
