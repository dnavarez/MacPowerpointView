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
    private readonly Panel _stage = new();
    private readonly Panel _nextStage = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _nowLabel = new();
    private readonly TextBlock _nextLabel = new();
    private readonly TextBlock _clock = new();
    private readonly TextBlock _buildLabel = new();
    private readonly Button _presentButton = new();
    private readonly Button _prevButton = new();
    private readonly Button _nextButton = new();
    private readonly Grid _consoleGrid = new();
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
        var titleHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
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
        nowPane.Children.Add(_stage);

        var nextPane = new DockPanel();
        DockPanel.SetDock(_nextLabel, Dock.Top);
        _nextLabel.Margin = new Thickness(0, 0, 0, 8);
        nextPane.Children.Add(_nextLabel);
        nextPane.Children.Add(_nextStage);

        _consoleGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto,340");
        _consoleGrid.Margin = new Thickness(16);
        _consoleGrid.Children.Add(nowPane);
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        _consoleGrid.Children.Add(splitter);
        Grid.SetColumn(nextPane, 2);
        _consoleGrid.Children.Add(nextPane);
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

    private void BuildThumbnails()
    {
        if (_state.Presentation is not { } pres) return;
        var items = new List<Control>();
        for (int i = 0; i < pres.Slides.Count; i++)
        {
            const double thumbWidth = 150;
            var thumbHeight = thumbWidth * pres.Size.Height / pres.Size.Width;
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
                Child = SlideRenderer.Render(pres.Slides[i], pres.Size, thumbWidth, thumbHeight)
            });
            items.Add(row);
        }
        _suppressSelection = true;
        _thumbs.ItemsSource = items;
        _thumbs.SelectedIndex = 0;
        _suppressSelection = false;
        OnStateChanged();
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
        var stageW = Math.Max(50, _stage.Bounds.Width > 10 ? _stage.Bounds.Width : 800);
        var stageH = Math.Max(50, _stage.Bounds.Height > 10 ? _stage.Bounds.Height : 450);
        if (_state.CurrentSlide is { } slide)
            _stage.Children.Add(SlideRenderer.Render(slide, pres.Size, stageW, stageH));

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

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (_state.Presentation != null) RenderStage();
    }
}
