using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly Panel _nextStage = new();      // presenter console, next slide
    private readonly TextBlock _status = new();
    private readonly TextBlock _nowLabel = new();
    private readonly TextBlock _nextLabel = new();
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
        Width = 1280;
        Height = 780;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
        _nextLabel.Foreground = Brushes.Gray;
        _nextLabel.FontWeight = FontWeight.SemiBold;
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

        var nextPane = new DockPanel();
        DockPanel.SetDock(_nextLabel, Dock.Top);
        _nextLabel.Margin = new Thickness(0, 0, 0, 8);
        nextPane.Children.Add(_nextLabel);
        nextPane.Children.Add(_nextStage);

        // Right column: next slide above, camera below, each resizable.
        var rightColumn = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        rightColumn.Children.Add(nextPane);
        var vSplitter = new GridSplitter { Height = 6, Background = Brushes.Transparent };
        Grid.SetRow(vSplitter, 1);
        rightColumn.Children.Add(vSplitter);
        Grid.SetRow(_camera, 2);
        rightColumn.Children.Add(_camera);

        _consoleGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto,340");
        _consoleGrid.Margin = new Thickness(16);
        _consoleGrid.Children.Add(nowPane);
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        _consoleGrid.Children.Add(splitter);
        Grid.SetColumn(rightColumn, 2);
        _consoleGrid.Children.Add(rightColumn);
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

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4
        };
        transport.Children.Add(_prevButton);
        transport.Children.Add(_status);
        transport.Children.Add(_nextButton);
        transport.Children.Add(hint);

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

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        main.Children.Add(_thumbs);
        var rightSide = new DockPanel();
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        rightSide.Children.Add(bottomBar);
        rightSide.Children.Add(browserArea);
        Grid.SetColumn(rightSide, 1);
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

    private const double ThumbWidth = 150;

    private void BuildThumbnails()
    {
        if (_state.Presentation is not { } pres) return;

        var thumbHeight = ThumbWidth * pres.Size.Height / pres.Size.Width;
        var items = new List<Control>();
        var images = new List<Image>();

        // Lightweight placeholders first so a large deck opens instantly; the
        // bitmaps fill in on the dispatcher afterwards.
        for (int i = 0; i < pres.Slides.Count; i++)
        {
            var image = new Image
            {
                Width = ThumbWidth, Height = thumbHeight, Stretch = Stretch.Fill
            };
            images.Add(image);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                Width = 22,
                TextAlignment = TextAlignment.Right,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 200)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Child = image
            });
            items.Add(row);
        }

        _suppressSelection = true;
        _thumbs.ItemsSource = items;
        _thumbs.SelectedIndex = 0;
        _suppressSelection = false;
        OnStateChanged();

        _thumbnailToken++;
        RenderThumbnails(pres, images, 0, _thumbnailToken);
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
            try { images[i].Source = SlideRenderer.RenderToBitmap(pres.Slides[i], pres.Size, ThumbWidth); }
            catch { /* a thumbnail is cosmetic; never break opening a deck */ }
        }
        if (end < images.Count)
            Dispatcher.UIThread.Post(() => RenderThumbnails(pres, images, end, token),
                DispatcherPriority.Background);
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

        _nextStage.Children.Clear();
        if (_state.IsPresenting)
        {
            var nextW = Math.Max(50, _nextStage.Bounds.Width > 10 ? _nextStage.Bounds.Width : 320);
            var nextH = nextW * pres.Size.Height / pres.Size.Width;
            if (_state.NextSlide is { } next)
                _nextStage.Children.Add(SlideRenderer.Render(next, pres.Size, nextW, nextH));
            else
                _nextStage.Children.Add(new Border
                {
                    Height = nextH,
                    Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                    Child = new TextBlock
                    {
                        Text = "End of presentation",
                        Foreground = Brushes.Gray,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
        }
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

        _nowLabel.Text = $"● Now Presenting — Slide {_state.CurrentIndex + 1} of {_state.SlideCount}";
        _nextLabel.Text = _state.NextSlide != null ? $"Next — Slide {_state.CurrentIndex + 2}" : "Next";

        var builds = _state.CurrentSlide?.BuildSteps.Count ?? 0;
        _buildLabel.Text = builds > 0 ? $"Build {_state.BuildIndex}/{builds}" : "";
        UpdateClock();
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
        if (_state.IsPresenting) EndPresentation();
        else StartPresentation();
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
                EndPresentation(); e.Handled = true; break;
            case Key.F5:
                TogglePresentation(); e.Handled = true; break;
            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = OpenDialog(); e.Handled = true; break;
        }
    }

    /// <summary>Drives the risky UI paths once so failures surface in CI/builds
    /// rather than on a user's machine.</summary>
    public void RunSelfTest()
    {
        StartPresentation();
        _state.GoNext();
        _state.GoNext();
        _state.GoPrevious();
        EndPresentation();
        _state.GoTo(0);
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

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (_state.Presentation != null) RenderStage();
    }
}
