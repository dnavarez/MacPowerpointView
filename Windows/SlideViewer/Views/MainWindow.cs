using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SlideViewer.Models;
using SlideViewer.Rendering;

namespace SlideViewer.Views;

/// <summary>Browser window: thumbnail sidebar, slide view, presenter console.</summary>
public sealed class MainWindow : Window
{
    private readonly PresentationState _state = new();
    private PresentationWindow? _showWindow;
    private readonly DispatcherTimer _timer;

    private readonly ListBox _thumbs = new();
    private readonly Panel _stage = new();          // browser view
    private readonly Panel _consoleStage = new();   // presenter console, current slide
    private double _thumbWidth = 150;
    private ColumnDefinition? _sidebarColumn;
    private readonly StackPanel _consoleTransport = new();
    private readonly StackPanel _browserTransport = new();
    private readonly Button _endButton = new();
    private bool _sidebarSized;
    private readonly TextBlock _status = new();
    private readonly TextBlock _nowLabel = new();
    private readonly TextBlock _clock = new();
    private readonly TextBlock _buildLabel = new();
    private readonly Button _presentButton = new();
    private readonly Button _prevButton = new();
    private readonly Button _nextButton = new();
    private readonly Grid _consoleGrid = new();
    private readonly CameraPanel _camera = new();
    private readonly Panel _emptyState = new();
    private readonly Grid _content = new();
    private bool _suppressSelection;

    public MainWindow()
    {
        Title = "SlideViewer";
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyLaunchSize();
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateClock();

        Content = BuildLayout();
        _state.Changed += OnStateChanged;

        KeyDown += OnKeyDown;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        DragDrop.SetAllowDrop(this, true);

        ShowEmptyState(true);
    }

    /// <summary>Opens at 75% of the screen's working height (which excludes the
    /// taskbar), centred horizontally and anchored at the top — so the desktop
    /// and the window's own title bar stay visible instead of the window
    /// appearing full-screen.</summary>
    private void ApplyLaunchSize()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) { Width = 1180; Height = 720; return; }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var work = screen.WorkingArea;                 // device pixels
        var workWidth = work.Width / scaling;          // logical units
        var workHeight = work.Height / scaling;

        var height = Math.Max(MinHeight, workHeight * 0.75);
        // 16:10 suits a 16:9 slide plus the sidebar and controls.
        var width = Math.Clamp(height * 1.6, MinWidth, workWidth);

        Width = width;
        Height = height;
        Position = new PixelPoint(
            (int)(work.X + (work.Width - width * scaling) / 2),
            work.Y);
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        // Toolbar
        var openButton = new Button { Content = "Open…", Padding = new Thickness(14, 6) };
        openButton.Click += async (_, _) => await OpenDialog();

        _presentButton.Content = "▶  Present";
        _presentButton.Padding = new Thickness(14, 6);
        _presentButton.IsEnabled = false;
        _presentButton.Click += (_, _) => TogglePresentation();

        var title = new TextBlock
        {
            Text = "SlideViewer",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        toolbar.Children.Add(title);
        Grid.SetColumn(openButton, 2);
        Grid.SetColumn(_presentButton, 3);
        toolbar.Children.Add(openButton);
        toolbar.Children.Add(_presentButton);
        var toolbarBorder = new Border
        {
            Padding = new Thickness(14, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Child = toolbar
        };

        // Thumbnails
        _thumbs.SelectionMode = SelectionMode.Single;
        _thumbs.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        _thumbs.SelectionChanged += (_, _) =>
        {
            if (_suppressSelection || _thumbs.SelectedIndex < 0) return;
            _state.GoTo(_thumbs.SelectedIndex);
        };

        // Browser stage
        var stageHost = new Border { Padding = new Thickness(20), Child = _stage };

        // Presenter console
        _nowLabel.Foreground = Brushes.Tomato;
        _nowLabel.FontWeight = FontWeight.SemiBold;
        _clock.Foreground = Brushes.Gainsboro;
        _clock.FontWeight = FontWeight.SemiBold;
        _buildLabel.Foreground = Brushes.Gray;

        var nowHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        nowHeader.Children.Add(_nowLabel);
        Grid.SetColumn(_buildLabel, 1);
        _buildLabel.Margin = new Thickness(10, 0, 0, 0);
        nowHeader.Children.Add(_buildLabel);
        Grid.SetColumn(_clock, 3);
        nowHeader.Children.Add(_clock);

        var nowPane = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(nowHeader, Dock.Top);
        nowHeader.Margin = new Thickness(0, 0, 0, 8);
        nowPane.Children.Add(nowHeader);
        nowPane.Children.Add(_consoleStage);

        // Transport sits directly under the live slide, where the presenter is
        // already looking, rather than at the bottom of the window.
        _consoleTransport.Orientation = Orientation.Horizontal;
        _consoleTransport.HorizontalAlignment = HorizontalAlignment.Center;
        _consoleTransport.Spacing = 6;
        _consoleTransport.Margin = new Thickness(0, 8, 0, 0);
        _consoleTransport.Children.Add(_prevButton);
        _consoleTransport.Children.Add(_status);
        _consoleTransport.Children.Add(_nextButton);
        DockPanel.SetDock(_consoleTransport, Dock.Bottom);
        nowPane.Children.Insert(1, _consoleTransport);

        // Live slide above, camera below, draggable divider between.
        _consoleGrid.ColumnDefinitions = new ColumnDefinitions("*");
        _consoleGrid.RowDefinitions = new RowDefinitions("*,Auto,Auto");
        _consoleGrid.Margin = new Thickness(16);
        _consoleGrid.Children.Add(nowPane);
        var vSplitter = new GridSplitter { Height = 6, Background = Brushes.Transparent };
        Grid.SetRow(vSplitter, 1);
        _consoleGrid.Children.Add(vSplitter);
        Grid.SetRow(_camera, 2);
        _camera.MinHeight = 140;
        _consoleGrid.Children.Add(_camera);
        _consoleGrid.IsVisible = false;

        // Bottom transport bar
        _prevButton.Content = "‹";
        _prevButton.Padding = new Thickness(16, 4);
        _prevButton.Click += (_, _) => _state.GoPrevious();
        _nextButton.Content = "›";
        _nextButton.Padding = new Thickness(16, 4);
        _nextButton.Click += (_, _) => _state.GoNext();
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(14, 0);
        _status.Foreground = Brushes.Gainsboro;

        var hint = new TextBlock
        {
            Text = "← → navigate · Esc ends the show",
            Foreground = Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0)
        };

        var transport = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(hint, Dock.Left);
        hint.VerticalAlignment = VerticalAlignment.Center;
        transport.Children.Add(hint);
        DockPanel.SetDock(_endButton, Dock.Right);
        _endButton.Content = "■  End Presentation";
        _endButton.Padding = new Thickness(14, 4);
        _endButton.IsVisible = false;
        _endButton.Click += (_, _) => RequestEndPresentation();
        transport.Children.Add(_endButton);
        DockPanel.SetDock(_browserTransport, Dock.Right);
        _browserTransport.Orientation = Orientation.Horizontal;
        _browserTransport.Spacing = 6;
        _browserTransport.Margin = new Thickness(0, 0, 12, 0);
        transport.Children.Add(_browserTransport);

        var bottomBar = new Border
        {
            Padding = new Thickness(10, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Child = transport
        };

        // Empty state
        var emptyStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12
        };
        emptyStack.Children.Add(new TextBlock
        {
            Text = "SlideViewer",
            FontSize = 32, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.White
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = "Viewer for PowerPoint & PPTX",
            FontSize = 16, Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = "Drag a .pptx file here, or open one to begin.",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });
        var emptyOpen = new Button
        {
            Content = "Open Presentation…",
            Padding = new Thickness(20, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        emptyOpen.Click += async (_, _) => await OpenDialog();
        emptyStack.Children.Add(emptyOpen);
        _emptyState.Children.Add(emptyStack);

        // Content area: sidebar + stage (or console)
        var browserArea = new Grid();
        browserArea.Children.Add(stageHost);
        browserArea.Children.Add(_consoleGrid);

        // Sidebar starts at ~30% of the window and is draggable; MinWidth/MaxWidth
        // bound the drag rather than a fixed width.
        var main = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(360, GridUnitType.Pixel) { MinWidth = 170, MaxWidth = 900 },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            }
        };
        _sidebarColumn = main.ColumnDefinitions[0];
        main.Children.Add(_thumbs);

        var sidebarSplitter = new GridSplitter { Width = 6, Background = Brushes.Transparent };
        Grid.SetColumn(sidebarSplitter, 1);
        // Re-render thumbnails at the new width once the drag settles.
        sidebarSplitter.DragCompleted += (_, _) => RebuildThumbnailsForWidth();
        main.Children.Add(sidebarSplitter);

        var rightSide = new DockPanel();
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        rightSide.Children.Add(bottomBar);
        rightSide.Children.Add(browserArea);
        Grid.SetColumn(rightSide, 2);
        main.Children.Add(rightSide);

        _content.Children.Add(main);
        _content.Children.Add(_emptyState);

        var root = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        root.Children.Add(toolbarBorder);
        root.Children.Add(_content);
        return root;
    }

    private void ShowEmptyState(bool empty)
    {
        _emptyState.IsVisible = empty;
        foreach (var child in _content.Children)
            if (!ReferenceEquals(child, _emptyState)) child.IsVisible = !empty;
    }

    // ── Opening ─────────────────────────────────────────────────────────────

    private async Task OpenDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Presentation",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PowerPoint Presentation") { Patterns = new[] { "*.pptx" } }
            }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenFile(path);
    }

    public void OpenFile(string path)
    {
        try
        {
            EndPresentation();
            _state.Open(path);
            Title = $"{_state.FileName} — SlideViewer";
            ShowEmptyState(false);
            _presentButton.IsEnabled = true;
            BuildThumbnails();
        }
        catch (Exception ex)
        {
            ShowError(ex is InvalidDataException ? ex.Message
                : $"“{Path.GetFileName(path)}” could not be opened.\n\n{ex.Message}");
        }
    }

    private async void ShowError(string message)
    {
        var dialog = new Window
        {
            Title = "Could not open presentation",
            Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var ok = new Button
        {
            Content = "OK", Padding = new Thickness(24, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ok.Click += (_, _) => dialog.Close();
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(ok);
        dialog.Content = stack;
        await dialog.ShowDialog(this);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (file != null && file.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            OpenFile(file);
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    /// <summary>Thumbnail width from the sidebar's current width, so dragging the
    /// divider scales them and how many fit follows from the size chosen.</summary>
    private double CurrentThumbWidth()
    {
        // ActualWidth is only meaningful after layout; fall back to the list's
        // own bounds, then the configured column width, then a sane default.
        var column = _sidebarColumn?.ActualWidth ?? 0;
        if (column < 50) column = _thumbs.Bounds.Width;
        if (column < 50 && _sidebarColumn?.Width.IsAbsolute == true) column = _sidebarColumn.Width.Value;
        if (column < 50) column = 360;
        return Math.Max(90, column - 64);   // slide number, badge, padding, scrollbar
    }

    private readonly List<Border> _thumbFrames = new();
    private readonly List<TextBlock> _thumbBadges = new();

    private void BuildThumbnails()
    {
        if (_state.Presentation is not { } pres) return;

        _thumbWidth = CurrentThumbWidth();
        _thumbFrames.Clear();
        _thumbBadges.Clear();
        var items = new List<Control>();
        var images = new List<Image>();

        // Lightweight placeholders first so a large deck opens instantly; the
        // bitmaps fill in on the dispatcher afterwards.
        for (int i = 0; i < pres.Slides.Count; i++)
        {
            // Uniform + stretch: the thumbnail always scales to the column it is
            // given. A fixed Width computed before layout overflows a narrower
            // sidebar and gets clipped, which crops the slide instead of
            // shrinking it.
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top
            };
            images.Add(image);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(0, 1)
            };

            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                Width = 26,
                Margin = new Thickness(0, 0, 6, 0),
                TextAlignment = TextAlignment.Right,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(number);

            var frame = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Child = image
            };
            Grid.SetColumn(frame, 1);
            _thumbFrames.Add(frame);
            row.Children.Add(frame);

            var badge = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };
            Grid.SetColumn(badge, 2);
            _thumbBadges.Add(badge);
            row.Children.Add(badge);
            items.Add(row);
        }

        _suppressSelection = true;
        _thumbs.ItemsSource = items;
        _thumbs.SelectedIndex = 0;
        _suppressSelection = false;
        OnStateChanged();

        _thumbnailToken++;
        RenderThumbnails(pres, images, 0, _thumbnailToken);

        // The first build can run before the sidebar has its real width; once
        // layout settles, re-render at the true size so thumbnails are sharp
        // rather than a scaled-down bitmap.
        Dispatcher.UIThread.Post(RebuildThumbnailsForWidth, DispatcherPriority.Loaded);
    }

    private int _thumbnailToken;

    /// <summary>Rasterises thumbnails a few at a time so the UI stays responsive
    /// while a long deck fills in.</summary>
    private void RenderThumbnails(Models.Presentation pres, List<Image> images, int start, int token)
    {
        if (token != _thumbnailToken) return;   // superseded by a newer document
        const int batch = 8;
        var end = Math.Min(start + batch, images.Count);
        for (int i = start; i < end; i++)
        {
            try { images[i].Source = SlideRenderer.RenderToBitmap(pres.Slides[i], pres.Size, _thumbWidth, RenderScaling); }
            catch { /* a thumbnail is cosmetic; never break opening a deck */ }
        }
        if (end < images.Count)
            Dispatcher.UIThread.Post(() => RenderThumbnails(pres, images, end, token),
                DispatcherPriority.Background);
    }

    /// <summary>Re-rasterises thumbnails after the sidebar is resized, but only
    /// when the width actually changed meaningfully.</summary>
    private bool _rebuildingThumbnails;

    private void RebuildThumbnailsForWidth()
    {
        if (_state.Presentation == null || _rebuildingThumbnails) return;
        var width = CurrentThumbWidth();
        if (Math.Abs(width - _thumbWidth) < 8) return;
        _rebuildingThumbnails = true;
        try { BuildThumbnails(); }
        finally { _rebuildingThumbnails = false; }
    }

    private void OnStateChanged()
    {
        RenderStage();
        UpdateChrome();
        _showWindow?.Refresh();
    }

    private void RenderStage()
    {
        if (_state.Presentation is not { } pres) return;

        _stage.Children.Clear();
        _consoleStage.Children.Clear();
        var target = _state.IsPresenting ? _consoleStage : _stage;
        var stageW = Math.Max(50, target.Bounds.Width > 10 ? target.Bounds.Width : 800);
        var stageH = Math.Max(50, target.Bounds.Height > 10 ? target.Bounds.Height : 450);
        if (_state.CurrentSlide is { } slide)
            target.Children.Add(SlideRenderer.Render(slide, pres.Size, stageW, stageH));

    }

    private void UpdateChrome()
    {
        _status.Text = _state.SlideCount > 0
            ? $"Slide {_state.CurrentIndex + 1} of {_state.SlideCount}" : "";
        _prevButton.IsEnabled = _state.CanGoPrevious;
        _nextButton.IsEnabled = _state.CanGoNext;

        _suppressSelection = true;
        if (_thumbs.SelectedIndex != _state.CurrentIndex) _thumbs.SelectedIndex = _state.CurrentIndex;
        _suppressSelection = false;
        // Keep the live slide centred so upcoming slides read from the list.
        CenterCurrentThumbnail();
        UpdateThumbnailMarkers();

        _nowLabel.Text = $"● Now Presenting — Slide {_state.CurrentIndex + 1} of {_state.SlideCount}";

        var builds = _state.CurrentSlide?.BuildSteps.Count ?? 0;
        _buildLabel.Text = builds > 0 ? $"Build {_state.BuildIndex}/{builds}" : "";
        UpdateClock();
    }

    /// <summary>Scrolls the list so the live slide sits in the middle, with the
    /// preceding and upcoming slides visible around it.
    ///
    /// ListBox.ScrollIntoView only brings an item just barely into view, which
    /// leaves the live slide pinned to the top or bottom edge — so the scroll
    /// offset is set directly. Rows are uniform (every thumbnail is the same
    /// size), so the row height can be derived from the extent.</summary>
    private void CenterCurrentThumbnail()
    {
        // Posted so it runs after the layout pass that follows a selection change.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var count = _state.SlideCount;
                if (count == 0) return;
                var scroll = _thumbs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll == null) return;

                var extent = scroll.Extent.Height;
                var viewport = scroll.Viewport.Height;
                if (extent <= 0 || viewport <= 0 || extent <= viewport) return;

                var rowHeight = extent / count;
                var target = (_state.CurrentIndex + 0.5) * rowHeight - viewport / 2;
                scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(target, 0, extent - viewport));
            }
            catch { /* scrolling is cosmetic */ }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Marks which slide is live and which is next, so the sidebar
    /// replaces a dedicated next-slide pane.</summary>
    private void UpdateThumbnailMarkers()
    {
        for (int i = 0; i < _thumbFrames.Count; i++)
        {
            var isCurrent = i == _state.CurrentIndex;
            var isNext = i == _state.CurrentIndex + 1;
            _thumbFrames[i].BorderBrush = isCurrent
                ? Brushes.Red
                : isNext && _state.IsPresenting
                    ? Brushes.DodgerBlue
                    : new SolidColorBrush(Color.FromArgb(80, 200, 200, 200));
            _thumbFrames[i].BorderThickness = new Thickness(isCurrent ? 3 : 1);

            var badge = _thumbBadges[i];
            badge.IsVisible = _state.IsPresenting && (isCurrent || isNext);
            badge.Text = isCurrent ? "LIVE" : "NEXT";
            badge.Foreground = isCurrent ? Brushes.Red : Brushes.Gray;
        }
    }

    private void UpdateClock()
    {
        if (_state.PresentationStart is { } start)
        {
            var elapsed = DateTime.Now - start;
            _clock.Text = elapsed.TotalHours >= 1
                ? $"⏱ {(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"⏱ {elapsed.Minutes}:{elapsed.Seconds:00}";
        }
        else _clock.Text = "";
    }

    // ── Presentation mode ───────────────────────────────────────────────────

    private void TogglePresentation()
    {
        if (_state.IsPresenting) RequestEndPresentation();
        else StartPresentation();
    }

    /// <summary>Esc asks before ending — an accidental keypress mid-service
    /// should not drop the audience back to the desktop.</summary>
    private async void RequestEndPresentation()
    {
        if (!_state.IsPresenting) return;
        if (_confirmingEnd) return;
        _confirmingEnd = true;
        try
        {
            // The show window sits above everything; drop it so the prompt is
            // visible on a single-display setup.
            _showWindow?.SetConfirming(true);
            var end = await ConfirmAsync(
                "End the presentation?",
                "The slide window will close and the audience display will return to the desktop.",
                "End Presentation", "Keep Presenting");
            if (end) EndPresentation();
            else _showWindow?.SetConfirming(false);
        }
        finally { _confirmingEnd = false; }
    }

    private bool _confirmingEnd;

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var confirm = new Button { Content = confirmText, Padding = new Thickness(18, 6) };
        var cancel = new Button { Content = cancelText, Padding = new Thickness(18, 6), IsDefault = true };
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => { result = false; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(buttons);
        dialog.Content = stack;

        await dialog.ShowDialog(this);
        return result;
    }

    private void StartPresentation()
    {
        if (_state.Presentation == null || _state.IsPresenting) return;

        _state.IsPresenting = true;
        _state.PresentationStart = DateTime.Now;
        _state.ResetBuilds();

        // Prefer a screen that is not the one hosting this window.
        var here = Screens.ScreenFromWindow(this);
        var target = Screens.All.FirstOrDefault(s => !Equals(s, here)) ?? here;

        _showWindow = new PresentationWindow(_state, target);
        _showWindow.Ended += EndPresentation;
        _showWindow.Closed += (_, _) => { if (_state.IsPresenting) EndPresentation(); };
        _showWindow.Show();

        _presentButton.Content = "■  End";
        _camera.RefreshDevices();
        MoveTransport(toConsole: true);
        _endButton.IsVisible = true;
        _consoleGrid.IsVisible = true;
        _timer.Start();
        OnStateChanged();
        Activate();
    }

    private void EndPresentation()
    {
        if (!_state.IsPresenting) { _showWindow?.Close(); _showWindow = null; return; }
        _state.IsPresenting = false;
        _state.PresentationStart = null;
        _timer.Stop();

        var window = _showWindow;
        _showWindow = null;
        window?.Close();

        _camera.Stop();
        _presentButton.Content = "▶  Present";
        MoveTransport(toConsole: false);
        _endButton.IsVisible = false;
        _consoleGrid.IsVisible = false;
        OnStateChanged();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right or Key.Down or Key.Space or Key.PageDown:
                _state.GoNext(); e.Handled = true; break;
            case Key.Left or Key.Up or Key.PageUp:
                _state.GoPrevious(); e.Handled = true; break;
            case Key.Escape when _state.IsPresenting:
                RequestEndPresentation(); e.Handled = true; break;
            case Key.F5:
                TogglePresentation(); e.Handled = true; break;
            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = OpenDialog(); e.Handled = true; break;
        }
    }

    /// <summary>Drives the risky UI paths once so failures surface in CI/builds
    /// rather than on a user's machine.</summary>
    public void RunSelfTest(bool exerciseCamera = false)
    {
        StartPresentation();
        if (exerciseCamera)
        {
            _camera.RefreshDevices();
            _camera.SelfTestToggle();
        }
        _state.GoNext();
        _state.GoNext();
        _state.GoPrevious();
        EndPresentation();
        _state.GoTo(0);
    }

    /// <summary>Reports the launch geometry so it can be checked against the
    /// screen's working area rather than eyeballed.</summary>
    public void ReportGeometry()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) { Console.WriteLine("GEOMETRY: no screen"); return; }
        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var workW = screen.WorkingArea.Width / scaling;
        var workH = screen.WorkingArea.Height / scaling;
        Console.WriteLine($"GEOMETRY work={workW:F0}x{workH:F0} scaling={scaling:F2} " +
                          $"window={Width:F0}x{Height:F0} " +
                          $"heightPct={(Height / workH * 100):F0}% widthPct={(Width / workW * 100):F0}% " +
                          $"pos={Position.X},{Position.Y}");
        var thumb = CurrentThumbWidth();
        Console.WriteLine($"GEOMETRY sidebarColumn={_sidebarColumn?.ActualWidth:F0} thumbWidth={thumb:F0} rendered={_thumbWidth:F0}");
    }

    /// <summary>Checks that the live slide really lands in the middle of the
    /// list — ScrollIntoView used to leave it pinned to an edge.</summary>
    public void VerifyCentering()
    {
        foreach (var index in new[] { 0, 20, 60, Math.Max(0, _state.SlideCount - 1) })
        {
            if (index >= _state.SlideCount) continue;
            _state.GoTo(index);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            var scroll = _thumbs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll == null) { Console.WriteLine("CENTER: no scrollviewer"); return; }

            var extent = scroll.Extent.Height;
            var viewport = scroll.Viewport.Height;
            if (extent <= viewport) { Console.WriteLine($"CENTER slide {index + 1}: list fits, no scroll"); continue; }

            var rowHeight = extent / _state.SlideCount;
            var itemCentre = (index + 0.5) * rowHeight - scroll.Offset.Y;   // relative to viewport top
            var viewportCentre = viewport / 2;
            // Slides near the ends can't centre; the offset clamps instead.
            var clamped = scroll.Offset.Y <= 0.5 || scroll.Offset.Y >= extent - viewport - 0.5;
            var delta = Math.Abs(itemCentre - viewportCentre);
            Console.WriteLine($"CENTER slide {index + 1}: offset {itemCentre:F0} vs centre {viewportCentre:F0} " +
                              $"(delta {delta:F0}px){(clamped ? " [clamped at list end]" : "")}");
        }
    }

    /// <summary>Times the per-slide work so optimisation targets are measured.</summary>
    public void RunTiming()
    {
        if (_state.Presentation is not { } pres) return;
        var sw = new System.Diagnostics.Stopwatch();

        sw.Restart();
        for (int i = 0; i < Math.Min(20, pres.Slides.Count); i++)
            SlideRenderer.Render(pres.Slides[i], pres.Size, 900, 506);
        Console.WriteLine($"TIMING render-20-slides: {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 20.0:F1}ms each)");

        sw.Restart();
        for (int i = 0; i < Math.Min(20, pres.Slides.Count); i++) _state.GoTo(i);
        Console.WriteLine($"TIMING goto-20-slides(full UI): {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 20.0:F1}ms each)");

        sw.Restart();
        var thumbH = 150.0 * pres.Size.Height / pres.Size.Width;
        for (int i = 0; i < Math.Min(50, pres.Slides.Count); i++)
            SlideRenderer.Render(pres.Slides[i], pres.Size, 150, thumbH);
        Console.WriteLine($"TIMING thumbnails-50: {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Re-parents the prev/counter/next controls between the bottom bar
    /// and the console. Avalonia allows a control only one parent, so they move
    /// rather than being duplicated.</summary>
    private void MoveTransport(bool toConsole)
    {
        var from = toConsole ? _browserTransport : _consoleTransport;
        var to = toConsole ? _consoleTransport : _browserTransport;
        foreach (var control in new Control[] { _prevButton, _status, _nextButton })
        {
            from.Children.Remove(control);
            if (!to.Children.Contains(control)) to.Children.Add(control);
        }
    }

    /// <summary>Sidebar opens at ~30% of the window; afterwards the user's drag
    /// is left alone.</summary>
    private void SizeSidebar()
    {
        if (_sidebarSized || _sidebarColumn == null || Bounds.Width < 100) return;
        _sidebarSized = true;
        _sidebarColumn.Width = new GridLength(Math.Clamp(Bounds.Width * 0.30, 220, 560), GridUnitType.Pixel);
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        SizeSidebar();
        CheckRenderScaling();
        if (_state.Presentation != null) RenderStage();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SizeSidebar();
        _lastRenderScaling = RenderScaling;
    }

    private double _lastRenderScaling = 1;

    /// <summary>Moving the window to a display with different scaling changes the
    /// pixel density the thumbnails were rasterised for; re-render so they stay
    /// sharp instead of being upscaled.</summary>
    private void CheckRenderScaling()
    {
        if (Math.Abs(RenderScaling - _lastRenderScaling) <= 0.01) return;
        _lastRenderScaling = RenderScaling;
        if (_state.Presentation != null) BuildThumbnails();
    }
}
