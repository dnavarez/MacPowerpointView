using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using SlideViewer.Rendering;

namespace SlideViewer.Views;

/// <summary>
/// The audience-facing show window: borderless, black, full-screen. When a
/// second display is attached the show goes there and the presenter console
/// stays on the primary screen.
/// </summary>
public sealed class PresentationWindow : Window
{
    private readonly PresentationState _state;
    private readonly Panel _host = new();

    public event Action? Ended;

    public PresentationWindow(PresentationState state, Screen? target)
    {
        _state = state;

        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Black;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        Content = _host;
        Cursor = new Cursor(StandardCursorType.None);

        if (target != null)
        {
            // Position on the chosen screen before going full-screen.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = target.Bounds.Position;
            Width = target.Bounds.Width / (target.Scaling <= 0 ? 1 : target.Scaling);
            Height = target.Bounds.Height / (target.Scaling <= 0 ? 1 : target.Scaling);
        }

        _state.Changed += Refresh;
        KeyDown += OnKeyDown;
        PointerPressed += (_, _) => _state.GoNext();
        Opened += (_, _) => Refresh();
        LayoutUpdated += (_, _) => { if (_needsRefresh) Refresh(); };
    }

    private bool _needsRefresh = true;
    private Size _lastSize;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right or Key.Down or Key.Space or Key.Enter or Key.PageDown:
                _state.GoNext(); e.Handled = true; break;
            case Key.Left or Key.Up or Key.PageUp:
                _state.GoPrevious(); e.Handled = true; break;
            case Key.Escape:
                Ended?.Invoke(); e.Handled = true; break;
        }
    }

    public void Refresh()
    {
        var size = new Size(Bounds.Width, Bounds.Height);
        if (size.Width < 10 || size.Height < 10) { _needsRefresh = true; return; }
        _needsRefresh = false;
        _lastSize = size;

        _host.Children.Clear();
        if (_state.CurrentSlide is { } slide && _state.Presentation is { } pres)
            _host.Children.Add(SlideRenderer.Render(slide, pres.Size, size.Width, size.Height,
                _state.HiddenShapes(), _state.HiddenParagraphs()));
    }

    protected override void OnClosed(EventArgs e)
    {
        _state.Changed -= Refresh;
        base.OnClosed(e);
    }
}
