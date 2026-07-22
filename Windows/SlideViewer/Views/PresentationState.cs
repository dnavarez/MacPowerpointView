using SlideViewer.Models;
using SlideViewer.Parsing;

namespace SlideViewer.Views;

/// <summary>Open document, navigation, and animation build state.</summary>
public sealed class PresentationState
{
    private PptxParser? _parser;

    public Presentation? Presentation { get; private set; }
    public string? FileName { get; private set; }
    public int CurrentIndex { get; private set; }
    public int BuildIndex { get; private set; }
    public bool IsPresenting { get; set; }
    public DateTime? PresentationStart { get; set; }

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    public int SlideCount => Presentation?.Slides.Count ?? 0;

    public Slide? CurrentSlide =>
        Presentation != null && CurrentIndex >= 0 && CurrentIndex < Presentation.Slides.Count
            ? Presentation.Slides[CurrentIndex] : null;

    public Slide? NextSlide =>
        Presentation != null && CurrentIndex + 1 < Presentation.Slides.Count
            ? Presentation.Slides[CurrentIndex + 1] : null;

    /// <summary>Loads a deck. Throws with a user-facing message on failure.</summary>
    public void Open(string path)
    {
        var parser = new PptxParser(path);
        var presentation = parser.Parse();   // throws before we mutate state

        _parser?.Dispose();
        _parser = parser;
        Presentation = presentation;
        FileName = Path.GetFileName(path);
        CurrentIndex = 0;
        BuildIndex = 0;
        Rendering.SlideRenderer.ClearCaches();
        Notify();
    }

    // ── Navigation (a click plays the next build before changing slide) ──────

    public bool CanGoNext =>
        (IsPresenting && CurrentSlide != null && BuildIndex < CurrentSlide.BuildSteps.Count)
        || CurrentIndex < SlideCount - 1;

    public bool CanGoPrevious => (IsPresenting && BuildIndex > 0) || CurrentIndex > 0;

    public void GoNext()
    {
        if (IsPresenting && CurrentSlide is { } slide && BuildIndex < slide.BuildSteps.Count)
        {
            BuildIndex++;
            Notify();
            return;
        }
        if (CurrentIndex >= SlideCount - 1) return;
        CurrentIndex++;
        BuildIndex = 0;
        Notify();
    }

    public void GoPrevious()
    {
        if (IsPresenting && BuildIndex > 0)
        {
            BuildIndex--;
            Notify();
            return;
        }
        if (CurrentIndex <= 0) return;
        CurrentIndex--;
        // Landing back on a slide shows it fully built, like PowerPoint.
        BuildIndex = IsPresenting ? (CurrentSlide?.BuildSteps.Count ?? 0) : 0;
        Notify();
    }

    public void GoTo(int index)
    {
        if (Presentation == null || index < 0 || index >= Presentation.Slides.Count) return;
        CurrentIndex = index;
        BuildIndex = 0;
        Notify();
    }

    public void ResetBuilds()
    {
        BuildIndex = 0;
        Notify();
    }

    // ── Animation visibility ────────────────────────────────────────────────

    /// <summary>Shapes hidden right now: entrance targets not yet revealed, plus
    /// exit targets already played.</summary>
    public HashSet<string> HiddenShapes()
    {
        var hidden = new HashSet<string>();
        if (!IsPresenting || CurrentSlide is not { } slide || slide.BuildSteps.Count == 0) return hidden;

        for (int i = 0; i < slide.BuildSteps.Count; i++)
        {
            var step = slide.BuildSteps[i];
            if (i >= BuildIndex) hidden.UnionWith(step.Reveals);
            else
            {
                hidden.UnionWith(step.Hides);
                hidden.ExceptWith(step.Reveals);
            }
        }
        return hidden;
    }

    /// <summary>Paragraphs hidden right now, keyed by shape id.</summary>
    public Dictionary<string, HashSet<int>> HiddenParagraphs()
    {
        var hidden = new Dictionary<string, HashSet<int>>();
        if (!IsPresenting || CurrentSlide is not { } slide || slide.BuildSteps.Count == 0) return hidden;

        for (int i = BuildIndex; i < slide.BuildSteps.Count; i++)
            foreach (var (spid, paras) in slide.BuildSteps[i].ParagraphReveals)
            {
                if (!hidden.TryGetValue(spid, out var set)) hidden[spid] = set = new HashSet<int>();
                set.UnionWith(paras);
            }
        return hidden;
    }
}
