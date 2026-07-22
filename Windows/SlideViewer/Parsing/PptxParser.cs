using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using SlideViewer.Models;

namespace SlideViewer.Parsing;

/// <summary>
/// Parses a .pptx package into the slide model. Port of the macOS parser,
/// carrying the same inheritance rules and bug fixes: placeholder geometry and
/// text defaults from layout/master, background inheritance, master/layout
/// shape overlays, lstStyle + defRPr run defaults, theme colors and fonts,
/// tables, groups, connectors, srcRect crops, and animation timing.
/// </summary>
public sealed class PptxParser : IDisposable
{
    private readonly PptxPackage _pkg;
    private readonly Dictionary<string, Color> _themeColors = new();
    private string? _themeMajorFont, _themeMinorFont;

    private sealed class PlaceholderGeom
    {
        public Dictionary<string, Rect> ByIdx = new();
        public Dictionary<string, Rect> ByType = new();
    }

    private sealed class MasterStyles
    {
        public double? TitleSize;
        public Dictionary<int, double> BodySizes = new();
    }

    private sealed class SlideContext
    {
        public PlaceholderGeom Layout = new();
        public PlaceholderGeom Master = new();
        public MasterStyles Styles = new();
        public string? LayoutPath;
        public string? MasterPath;
    }

    private sealed class RunDefaults
    {
        public double? Size;
        public bool? Bold, Italic;
        public Color? Color;
        public string? FontName;
        public bool Shadow;
    }

    private readonly Dictionary<string, (PlaceholderGeom, string)> _layoutCache = new();
    private readonly Dictionary<string, (PlaceholderGeom, MasterStyles)> _masterCache = new();
    private readonly Dictionary<string, List<SlideElement>> _inheritedCache = new();

    public PptxParser(string path) => _pkg = new PptxPackage(path);
    public void Dispose() => _pkg.Dispose();

    // ── Entry point ─────────────────────────────────────────────────────────

    public Presentation Parse()
    {
        var presDoc = _pkg.LoadXml("ppt/presentation.xml")
            ?? throw new InvalidDataException("This file is missing ppt/presentation.xml — it may not be a PowerPoint file.");
        var presRoot = presDoc.Root;

        var result = new Presentation();
        var sldSz = presRoot.Descendant("sldSz");
        var cx = sldSz.AttrDouble("cx") ?? 0;
        var cy = sldSz.AttrDouble("cy") ?? 0;
        if (cx > 0 && cy > 0) result.Size = new Size(Emu.ToPoints(cx), Emu.ToPoints(cy));

        LoadTheme();

        var relIds = presRoot.DescendantsNamed("sldId")
            .Select(e => e.RelAttr("id")).Where(id => id != null).ToList();
        var presRels = _pkg.Relationships("ppt/presentation.xml");

        int index = 0;
        foreach (var rId in relIds)
        {
            if (!presRels.TryGetValue(rId!, out var target)) continue;
            var slidePath = PathUtil.Resolve("ppt", target);
            try
            {
                var slide = ParseSlide(slidePath, index);
                if (slide != null) { result.Slides.Add(slide); index++; }
            }
            catch { /* skip unreadable slide, keep the rest of the deck */ }
        }

        if (result.Slides.Count == 0)
            throw new InvalidDataException("No slides could be read from this presentation.");
        return result;
    }

    // ── Theme ───────────────────────────────────────────────────────────────

    private void LoadTheme()
    {
        // Office defaults as fallback.
        foreach (var (k, v) in new (string, string)[] {
            ("dk1","000000"), ("lt1","FFFFFF"), ("dk2","44546A"), ("lt2","E7E6E6"),
            ("tx1","000000"), ("bg1","FFFFFF"), ("tx2","44546A"), ("bg2","E7E6E6"),
            ("accent1","4472C4"), ("accent2","ED7D31"), ("accent3","A5A5A5"),
            ("accent4","FFC000"), ("accent5","5B9BD5"), ("accent6","70AD47"),
            ("hlink","0563C1"), ("folHlink","954F72") })
            _themeColors[k] = ColorUtil.FromHex(v);

        var doc = _pkg.LoadXml("ppt/theme/theme1.xml");
        if (doc?.Root == null) return;

        var scheme = doc.Root.Descendant("clrScheme");
        if (scheme != null)
            foreach (var child in scheme.Elements())
            {
                var c = ColorFromContainer(child);
                if (c.HasValue) _themeColors[child.Name.LocalName] = c.Value;
            }

        var fontScheme = doc.Root.Descendant("fontScheme");
        _themeMajorFont = fontScheme.Child("majorFont").Child("latin").Attr("typeface");
        _themeMinorFont = fontScheme.Child("minorFont").Child("latin").Attr("typeface");
    }

    /// <summary>+mj-lt/+mn-lt placeholders; no explicit font → theme minor font.</summary>
    private string? ThemeFont(string? name) => name switch
    {
        null or "+mn-lt" => _themeMinorFont,
        "+mj-lt" => _themeMajorFont,
        _ => name
    };

    // ── Slide ───────────────────────────────────────────────────────────────

    private Slide? ParseSlide(string partPath, int index)
    {
        var doc = _pkg.LoadXml(partPath, protectWhitespace: true);
        var root = doc?.Root;
        if (root == null) return null;

        var rels = _pkg.Relationships(partPath);
        var partDir = PathUtil.Directory(partPath);
        var ctx = BuildContext(rels, partDir);

        var slide = new Slide
        {
            Index = index,
            Background = ResolveBackground(partPath, root, ctx),
            HasTransition = root.Child("transition") != null || root.Descendant("transition") != null
        };

        // Master/layout non-placeholder shapes (logos, watermarks, footers) draw
        // beneath slide content, unless the slide opts out.
        if (root.Attr("showMasterSp") != "0")
        {
            if (ctx.MasterPath != null) slide.Elements.AddRange(InheritedElements(ctx.MasterPath));
            if (ctx.LayoutPath != null) slide.Elements.AddRange(InheritedElements(ctx.LayoutPath));
        }

        var spTree = root.Descendant("spTree");
        if (spTree != null)
            slide.Elements.AddRange(ParseShapeTree(spTree, ctx, rels, partDir));

        slide.BuildSteps = ParseBuildSteps(root);
        return slide;
    }

    private List<SlideElement> ParseShapeTree(XElement spTree, SlideContext ctx,
        Dictionary<string, string> rels, string partDir)
    {
        var elements = new List<SlideElement>();
        foreach (var node in spTree.Elements())
        {
            switch (node.Name.LocalName)
            {
                case "sp":
                case "cxnSp":
                    var shape = ParseShape(node, ctx, rels, partDir);
                    if (shape != null) elements.Add(shape);
                    break;
                case "pic":
                    var pic = ParsePicture(node, rels, partDir);
                    if (pic != null) elements.Add(pic);
                    break;
                case "graphicFrame":
                    var table = ParseTable(node);
                    if (table != null) elements.Add(table);
                    break;
                case "grpSp":
                    elements.AddRange(ParseGroup(node, ctx, rels, partDir));
                    break;
            }
        }
        return elements;
    }

    // ── Shapes ──────────────────────────────────────────────────────────────

    private SlideElement? ParseShape(XElement sp, SlideContext ctx,
        Dictionary<string, string> rels, string partDir)
    {
        var spPr = sp.Descendant("spPr");
        var shapeId = sp.Descendant("cNvPr").Attr("id");
        var (phType, phIdx) = PlaceholderInfo(sp);
        var frame = XfrmFrame(spPr) ?? InheritedFrame(phType, phIdx, ctx) ?? default;
        var (rotation, flipH, flipV) = XfrmOrientation(spPr);
        var defaultSize = DefaultFontSize(phType, ctx);

        TextFrame? textBox = null;
        var txBody = sp.Descendant("txBody");
        if (txBody != null)
        {
            var levelDefaults = ListStyleDefaults(txBody.Child("lstStyle"));
            var paragraphs = ParseParagraphs(txBody, defaultSize, levelDefaults);
            if (paragraphs.Any(p => p.Runs.Count > 0))
                textBox = new TextFrame
                {
                    Paragraphs = paragraphs,
                    VerticalAnchor = AnchorOf(txBody),
                    Insets = InsetsOf(txBody.Child("bodyPr"), tableCell: false)
                };
        }

        var fill = ParseFill(spPr, rels, partDir);
        var geometry = GeometryOf(spPr);
        var stroke = StrokeOf(spPr);

        // Themed shapes carry no explicit fill/line; <p:style> refs theme colors.
        var style = sp.Child("style");
        if (style != null)
        {
            if (fill == null && spPr.Child("noFill") == null)
            {
                var fillRef = style.Child("fillRef");
                if ((fillRef.AttrInt("idx") ?? 0) > 0)
                {
                    var c = ColorFromContainer(fillRef);
                    if (c.HasValue) fill = new Fill.Solid { Color = c.Value };
                }
            }
            if (stroke == null && spPr.Child("ln").Child("noFill") == null)
            {
                var lnRef = style.Child("lnRef");
                if ((lnRef.AttrInt("idx") ?? 0) > 0)
                {
                    var c = ColorFromContainer(lnRef);
                    if (c.HasValue) stroke = new StrokeInfo { Color = c.Value, Width = 1 };
                }
            }
        }

        // A connector with no styling still needs a visible line.
        if (geometry == ShapeGeometry.Line && stroke == null)
            stroke = new StrokeInfo { Color = Colors.Black, Width = 1 };

        if (fill != null || stroke != null)
            return new ShapeElement
            {
                ShapeId = shapeId, Frame = frame, Fill = fill, Stroke = stroke,
                Geometry = geometry, Text = textBox,
                Rotation = rotation, FlipH = flipH, FlipV = flipV
            };

        if (textBox != null)
            return new TextElement { ShapeId = shapeId, Frame = frame, Box = textBox };

        return null;
    }

    private PictureElement? ParsePicture(XElement pic, Dictionary<string, string> rels, string partDir)
    {
        var frame = XfrmFrame(pic.Descendant("spPr"));
        if (frame == null) return null;

        var blip = pic.Descendant("blip");
        var embed = blip.RelAttr("embed");
        if (embed == null || !rels.TryGetValue(embed, out var target)) return null;
        var mediaPath = _pkg.ExtractMedia(PathUtil.Resolve(partDir, target));
        if (mediaPath == null) return null;

        var crop = CropInsets.Zero;
        var src = pic.Descendant("blipFill").Child("srcRect");
        if (src != null)
            crop = new CropInsets(
                (src.AttrDouble("l") ?? 0) / 100000.0, (src.AttrDouble("t") ?? 0) / 100000.0,
                (src.AttrDouble("r") ?? 0) / 100000.0, (src.AttrDouble("b") ?? 0) / 100000.0);

        return new PictureElement
        {
            ShapeId = pic.Descendant("cNvPr").Attr("id"),
            Frame = frame.Value, ImagePath = mediaPath, Crop = crop
        };
    }

    /// <summary>Maps group children from child space (chOff/chExt) to slide space.</summary>
    private List<SlideElement> ParseGroup(XElement grp, SlideContext ctx,
        Dictionary<string, string> rels, string partDir)
    {
        var children = ParseShapeTree(grp, ctx, rels, partDir);

        var xfrm = grp.Child("grpSpPr").Child("xfrm");
        var off = xfrm.Child("off"); var ext = xfrm.Child("ext");
        var chOff = xfrm.Child("chOff"); var chExt = xfrm.Child("chExt");
        if (off == null || ext == null || chOff == null || chExt == null) return children;

        double ox = Emu.ToPoints(off.AttrDouble("x") ?? 0), oy = Emu.ToPoints(off.AttrDouble("y") ?? 0);
        double ew = Emu.ToPoints(ext.AttrDouble("cx") ?? 0), eh = Emu.ToPoints(ext.AttrDouble("cy") ?? 0);
        double cx = Emu.ToPoints(chOff.AttrDouble("x") ?? 0), cy = Emu.ToPoints(chOff.AttrDouble("y") ?? 0);
        double cw = Emu.ToPoints(chExt.AttrDouble("cx") ?? 0), ch = Emu.ToPoints(chExt.AttrDouble("cy") ?? 0);
        double sx = cw > 0 ? ew / cw : 1, sy = ch > 0 ? eh / ch : 1;

        foreach (var el in children)
            el.Frame = new Rect((el.Frame.X - cx) * sx + ox, (el.Frame.Y - cy) * sy + oy,
                                el.Frame.Width * sx, el.Frame.Height * sy);
        return children;
    }

    private TableElement? ParseTable(XElement frameEl)
    {
        var tbl = frameEl.Descendant("tbl");
        if (tbl == null) return null;
        var origin = XfrmFrame(frameEl)?.Position ?? default;

        var widths = tbl.Child("tblGrid").Children("gridCol")
            .Select(c => Emu.ToPoints(c.AttrDouble("w") ?? 0)).ToList();

        var rows = new List<TableRow>();
        foreach (var tr in tbl.Children("tr"))
        {
            var row = new TableRow { Height = Emu.ToPoints(tr.AttrDouble("h") ?? 0) };
            foreach (var tc in tr.Children("tc"))
            {
                if (tc.Attr("hMerge") == "1" || tc.Attr("vMerge") == "1") continue;
                var cell = new TableCell { GridSpan = Math.Max(1, tc.AttrInt("gridSpan") ?? 1) };
                var txBody = tc.Child("txBody");
                if (txBody != null)
                    cell.Paragraphs = ParseParagraphs(txBody, 14, ListStyleDefaults(txBody.Child("lstStyle")));
                var tcPr = tc.Child("tcPr");
                if (tcPr != null)
                {
                    cell.Fill = FillColor(tcPr);
                    cell.Insets = InsetsOf(tcPr, tableCell: true);
                }
                row.Cells.Add(cell);
            }
            rows.Add(row);
        }
        if (rows.Count == 0 || widths.Count == 0) return null;

        var size = new Size(widths.Sum(), rows.Sum(r => r.Height));
        return new TableElement
        {
            ShapeId = frameEl.Descendant("cNvPr").Attr("id"),
            Frame = new Rect(origin, size),
            ColumnWidths = widths, Rows = rows
        };
    }

    // ── Text ────────────────────────────────────────────────────────────────

    private Dictionary<int, RunDefaults> ListStyleDefaults(XElement? lstStyle)
    {
        var result = new Dictionary<int, RunDefaults>();
        if (lstStyle == null) return result;
        for (int lvl = 1; lvl <= 9; lvl++)
        {
            var defRPr = lstStyle.Child($"lvl{lvl}pPr").Child("defRPr");
            if (defRPr != null) result[lvl] = RunDefaultsFrom(defRPr);
        }
        return result;
    }

    private RunDefaults RunDefaultsFrom(XElement defRPr)
    {
        var d = new RunDefaults();
        var sz = defRPr.AttrDouble("sz"); if (sz.HasValue) d.Size = sz.Value / 100.0;
        var b = defRPr.Attr("b"); if (b != null) d.Bold = b == "1";
        var i = defRPr.Attr("i"); if (i != null) d.Italic = i == "1";
        d.Color = FillColor(defRPr);
        d.FontName = defRPr.Child("latin").Attr("typeface");
        d.Shadow = defRPr.Descendant("outerShdw") != null;
        return d;
    }

    private List<Paragraph> ParseParagraphs(XElement txBody, double defaultSize,
        Dictionary<int, RunDefaults> levelDefaults)
    {
        var result = new List<Paragraph>();
        var autoNumCounters = new Dictionary<int, int>();

        foreach (var p in txBody.Children("p"))
        {
            var para = new Paragraph();
            var pPr = p.Child("pPr");
            if (pPr != null)
            {
                para.Alignment = pPr.Attr("algn") switch
                {
                    "ctr" => ParagraphAlignment.Center,
                    "r" => ParagraphAlignment.Trailing,
                    _ => ParagraphAlignment.Leading   // "just" has no direct equivalent
                };
                para.Level = pPr.AttrInt("lvl") ?? 0;
                var marL = pPr.AttrDouble("marL"); if (marL.HasValue) para.MarginLeft = Emu.ToPoints(marL.Value);
                var ind = pPr.AttrDouble("indent"); if (ind.HasValue) para.Indent = Emu.ToPoints(ind.Value);
                para.SpaceBefore = SpacingPoints(pPr.Child("spcBef"));
                para.SpaceAfter = SpacingPoints(pPr.Child("spcAft"));
                var pct = pPr.Child("lnSpc").Child("spcPct").AttrDouble("val");
                if (pct.HasValue) para.LineSpacing = pct.Value / 100000.0;
                para.Bullet = ParseBullet(pPr);

                if (para.MarginLeft == 0 && para.Indent == 0 && para.Bullet != null)
                {
                    para.MarginLeft = (para.Level + 1) * 24;
                    para.Indent = -18;
                }
            }

            // lstStyle levels are 1-based; a pPr/defRPr overrides them.
            levelDefaults.TryGetValue(para.Level + 1, out var lvlDefaults);
            var defaults = Clone(lvlDefaults);
            var paraDefRPr = pPr.Child("defRPr");
            if (paraDefRPr != null)
            {
                var pd = RunDefaultsFrom(paraDefRPr);
                if (pd.Size.HasValue) defaults.Size = pd.Size;
                if (pd.Bold.HasValue) defaults.Bold = pd.Bold;
                if (pd.Italic.HasValue) defaults.Italic = pd.Italic;
                if (pd.Color.HasValue) defaults.Color = pd.Color;
                if (pd.FontName != null) defaults.FontName = pd.FontName;
                if (pd.Shadow) defaults.Shadow = true;
            }
            defaults.Size ??= defaultSize;

            foreach (var child in p.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "r":
                        var t = child.Child("t")?.Value;
                        if (t != null) para.Runs.Add(BuildRun(child.Child("rPr"), t, defaults));
                        break;
                    case "br":
                        para.Runs.Add(new TextRun { Text = "\n", FontSize = defaults.Size ?? 18 });
                        break;
                    case "fld":
                        var ft = child.Child("t")?.Value;
                        if (!string.IsNullOrEmpty(ft))
                            para.Runs.Add(BuildRun(child.Child("rPr"), ft, defaults));
                        break;
                }
            }

            // Auto-numbered bullets resolve against per-level counters.
            if (para.Bullet?.AutoNumFormat is string fmt)
            {
                var value = (autoNumCounters.TryGetValue(para.Level, out var last)
                    ? last : para.Bullet.StartAt - 1) + 1;
                autoNumCounters[para.Level] = value;
                foreach (var deeper in autoNumCounters.Keys.Where(k => k > para.Level).ToList())
                    autoNumCounters.Remove(deeper);
                para.Bullet.Glyph = AutoNumberText(fmt, value);
            }

            result.Add(para);
        }
        return result;
    }

    private static RunDefaults Clone(RunDefaults? d) => d == null ? new RunDefaults() : new RunDefaults
    {
        Size = d.Size, Bold = d.Bold, Italic = d.Italic,
        Color = d.Color, FontName = d.FontName, Shadow = d.Shadow
    };

    private TextRun BuildRun(XElement? rPr, string text, RunDefaults defaults)
    {
        var run = new TextRun
        {
            Text = text.Replace(PptxPackage.SpaceSentinel, " "),
            FontSize = defaults.Size ?? 18,
            Bold = defaults.Bold ?? false,
            Italic = defaults.Italic ?? false,
            FontName = defaults.FontName,
            Shadow = defaults.Shadow
        };
        if (defaults.Color.HasValue) run.Color = defaults.Color.Value;

        if (rPr != null)
        {
            var sz = rPr.AttrDouble("sz"); if (sz.HasValue) run.FontSize = sz.Value / 100.0;
            var b = rPr.Attr("b"); if (b != null) run.Bold = b == "1";
            var i = rPr.Attr("i"); if (i != null) run.Italic = i == "1";
            var u = rPr.Attr("u"); if (u != null && u != "none") run.Underline = true;
            var s = rPr.Attr("strike"); if (s != null && s != "noStrike") run.Strikethrough = true;
            var c = FillColor(rPr); if (c.HasValue) run.Color = c.Value;
            var latin = rPr.Child("latin").Attr("typeface"); if (latin != null) run.FontName = latin;
            if (rPr.Descendant("outerShdw") != null) run.Shadow = true;
        }
        run.FontName = ThemeFont(run.FontName);
        return run;
    }

    private Bullet? ParseBullet(XElement pPr)
    {
        if (pPr.Child("buNone") != null) return null;

        var color = ColorFromContainer(pPr.Child("buClr"));
        var sizePct = (pPr.Child("buSzPct").AttrDouble("val") ?? 100000) / 100000.0;
        var fontName = ThemeFont(pPr.Child("buFont").Attr("typeface"));

        var buChar = pPr.Child("buChar")?.Attr("char");
        if (buChar != null)
        {
            // Symbol fonts render differently across platforms; map to Unicode.
            var mapped = SymbolFont.UnicodeBullet(buChar, fontName);
            return mapped != null
                ? new Bullet { Glyph = mapped, FontName = null, Color = color, SizePercent = sizePct }
                : new Bullet { Glyph = buChar, FontName = fontName, Color = color, SizePercent = sizePct };
        }

        var auto = pPr.Child("buAutoNum");
        if (auto != null)
            return new Bullet
            {
                Glyph = "", Color = color, SizePercent = sizePct,
                AutoNumFormat = auto.Attr("type") ?? "arabicPeriod",
                StartAt = auto.AttrInt("startAt") ?? 1
            };
        return null;
    }

    private static string AutoNumberText(string format, int value)
    {
        static string Letters(int n)
        {
            var sb = new System.Text.StringBuilder();
            while (n > 0) { n--; sb.Insert(0, (char)('A' + n % 26)); n /= 26; }
            return sb.ToString();
        }
        static string Roman(int n)
        {
            (int v, string s)[] table = { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),
                (90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") };
            var sb = new System.Text.StringBuilder();
            foreach (var (v, s) in table) while (n >= v) { sb.Append(s); n -= v; }
            return sb.ToString();
        }

        string core =
            format.StartsWith("alphaLc") ? Letters(value).ToLowerInvariant() :
            format.StartsWith("alphaUc") ? Letters(value) :
            format.StartsWith("romanLc") ? Roman(value).ToLowerInvariant() :
            format.StartsWith("romanUc") ? Roman(value) :
            value.ToString();

        if (format.EndsWith("ParenR")) return core + ")";
        if (format.EndsWith("ParenBoth")) return "(" + core + ")";
        if (format.EndsWith("Plain")) return core;
        return core + ".";
    }

    private static double SpacingPoints(XElement? spc)
    {
        var v = spc.Child("spcPts").AttrDouble("val");
        return v.HasValue ? v.Value / 100.0 : 0;
    }

    private static VerticalAnchor AnchorOf(XElement txBody) =>
        txBody.Child("bodyPr").Attr("anchor") switch
        {
            "ctr" => VerticalAnchor.Center,
            "b" => VerticalAnchor.Bottom,
            _ => VerticalAnchor.Top
        };

    private static TextInsets InsetsOf(XElement? element, bool tableCell)
    {
        var d = TextInsets.Default;
        if (element == null) return d;
        double Get(string name, double fallback)
        {
            var v = element.AttrDouble(name);
            return v.HasValue ? Emu.ToPoints(v.Value) : fallback;
        }
        return tableCell
            ? new TextInsets(Get("marL", d.Leading), Get("marT", d.Top), Get("marR", d.Trailing), Get("marB", d.Bottom))
            : new TextInsets(Get("lIns", d.Leading), Get("tIns", d.Top), Get("rIns", d.Trailing), Get("bIns", d.Bottom));
    }

    // ── Placeholder inheritance ─────────────────────────────────────────────

    private static (string? type, string? idx) PlaceholderInfo(XElement sp)
    {
        var ph = sp.Descendant("ph");
        if (ph == null) return (null, null);
        var type = ph.Attr("type");
        if (type == "ctrTitle") type = "title";
        return (type, ph.Attr("idx"));
    }

    private static Rect? InheritedFrame(string? type, string? idx, SlideContext ctx)
    {
        foreach (var geom in new[] { ctx.Layout, ctx.Master })
        {
            if (idx != null && geom.ByIdx.TryGetValue(idx, out var byIdx)) return byIdx;
            if (type != null && geom.ByType.TryGetValue(type, out var byType)) return byType;
        }
        return null;
    }

    private static double DefaultFontSize(string? phType, SlideContext ctx) =>
        phType == "title"
            ? ctx.Styles.TitleSize ?? 44
            : ctx.Styles.BodySizes.TryGetValue(1, out var s) ? s : 18;

    private SlideContext BuildContext(Dictionary<string, string> slideRels, string slideDir)
    {
        var ctx = new SlideContext();
        var layoutTarget = slideRels.Values.FirstOrDefault(t => t.Contains("slideLayout"));
        if (layoutTarget == null) return ctx;

        var layoutPath = PathUtil.Resolve(slideDir, layoutTarget);
        ctx.LayoutPath = layoutPath;

        if (!_layoutCache.TryGetValue(layoutPath, out var layoutEntry))
        {
            var geom = PlaceholderGeomOf(layoutPath);
            var layoutRels = _pkg.Relationships(layoutPath);
            var masterTarget = layoutRels.Values.FirstOrDefault(t => t.Contains("slideMaster"));
            var mPath = masterTarget != null
                ? PathUtil.Resolve(PathUtil.Directory(layoutPath), masterTarget) : "";
            layoutEntry = (geom, mPath);
            _layoutCache[layoutPath] = layoutEntry;
        }
        ctx.Layout = layoutEntry.Item1;
        var masterPath = layoutEntry.Item2;
        if (string.IsNullOrEmpty(masterPath)) return ctx;

        ctx.MasterPath = masterPath;
        if (!_masterCache.TryGetValue(masterPath, out var masterEntry))
        {
            masterEntry = (PlaceholderGeomOf(masterPath), MasterStylesOf(masterPath));
            _masterCache[masterPath] = masterEntry;
        }
        ctx.Master = masterEntry.Item1;
        ctx.Styles = masterEntry.Item2;
        return ctx;
    }

    private PlaceholderGeom PlaceholderGeomOf(string partPath)
    {
        var geom = new PlaceholderGeom();
        var spTree = _pkg.LoadXml(partPath)?.Root.Descendant("spTree");
        if (spTree == null) return geom;
        foreach (var sp in spTree.Children("sp"))
        {
            var frame = XfrmFrame(sp.Descendant("spPr"));
            if (frame == null) continue;
            var (type, idx) = PlaceholderInfo(sp);
            if (idx != null) geom.ByIdx[idx] = frame.Value;
            if (type != null) geom.ByType[type] = frame.Value;
        }
        return geom;
    }

    private MasterStyles MasterStylesOf(string partPath)
    {
        var styles = new MasterStyles();
        var txStyles = _pkg.LoadXml(partPath)?.Root.Descendant("txStyles");
        if (txStyles == null) return styles;

        var titleSz = txStyles.Child("titleStyle").Child("lvl1pPr").Child("defRPr").AttrDouble("sz");
        if (titleSz.HasValue) styles.TitleSize = titleSz.Value / 100.0;

        var body = txStyles.Child("bodyStyle");
        for (int lvl = 1; lvl <= 9; lvl++)
        {
            var sz = body.Child($"lvl{lvl}pPr").Child("defRPr").AttrDouble("sz");
            if (sz.HasValue) styles.BodySizes[lvl] = sz.Value / 100.0;
        }
        return styles;
    }

    /// <summary>Non-placeholder shapes a layout/master contributes to every slide.</summary>
    private List<SlideElement> InheritedElements(string partPath)
    {
        if (_inheritedCache.TryGetValue(partPath, out var cached)) return cached;

        var elements = new List<SlideElement>();
        var spTree = _pkg.LoadXml(partPath, protectWhitespace: true)?.Root.Descendant("spTree");
        if (spTree != null)
        {
            var rels = _pkg.Relationships(partPath);
            var dir = PathUtil.Directory(partPath);
            foreach (var node in spTree.Elements())
            {
                switch (node.Name.LocalName)
                {
                    case "sp":
                    case "cxnSp":
                        if (node.Descendant("ph") != null) continue;  // template prompt
                        var shape = ParseShape(node, new SlideContext(), rels, dir);
                        if (shape != null) elements.Add(shape);
                        break;
                    case "pic":
                        var pic = ParsePicture(node, rels, dir);
                        if (pic != null) elements.Add(pic);
                        break;
                    case "grpSp":
                        elements.AddRange(ParseGroup(node, new SlideContext(), rels, dir));
                        break;
                }
            }
        }
        _inheritedCache[partPath] = elements;
        return elements;
    }

    // ── Background ──────────────────────────────────────────────────────────

    private SlideBackground? ResolveBackground(string slidePart, XElement slideRoot, SlideContext ctx)
    {
        return BackgroundOf(slidePart, slideRoot)
            ?? (ctx.LayoutPath != null ? BackgroundOf(ctx.LayoutPath, null) : null)
            ?? (ctx.MasterPath != null ? BackgroundOf(ctx.MasterPath, null) : null);
    }

    private SlideBackground? BackgroundOf(string partPath, XElement? providedRoot)
    {
        var root = providedRoot ?? _pkg.LoadXml(partPath)?.Root;
        var bgPr = root.Descendant("bg").Descendant("bgPr");
        if (bgPr == null) return null;

        var rels = _pkg.Relationships(partPath);
        var dir = PathUtil.Directory(partPath);
        return ParseFill(bgPr, rels, dir) switch
        {
            Fill.Solid s => new SlideBackground.Solid { Color = s.Color },
            Fill.Gradient g => new SlideBackground.Gradient { Stops = g.Stops, Angle = g.Angle },
            Fill.Picture p => new SlideBackground.Picture { Path = p.Path },
            _ => null
        };
    }

    // ── Geometry, fills, colors ─────────────────────────────────────────────

    private static Rect? XfrmFrame(XElement? element)
    {
        var xfrm = element.Descendant("xfrm");
        var off = xfrm.Child("off"); var ext = xfrm.Child("ext");
        if (off == null || ext == null) return null;
        return new Rect(
            Emu.ToPoints(off.AttrDouble("x") ?? 0), Emu.ToPoints(off.AttrDouble("y") ?? 0),
            Emu.ToPoints(ext.AttrDouble("cx") ?? 0), Emu.ToPoints(ext.AttrDouble("cy") ?? 0));
    }

    private static (double rotation, bool flipH, bool flipV) XfrmOrientation(XElement? element)
    {
        var xfrm = element.Descendant("xfrm");
        if (xfrm == null) return (0, false, false);
        return ((xfrm.AttrDouble("rot") ?? 0) / 60000.0,
                xfrm.Attr("flipH") == "1", xfrm.Attr("flipV") == "1");
    }

    private static ShapeGeometry GeometryOf(XElement? spPr) =>
        spPr.Descendant("prstGeom").Attr("prst") switch
        {
            "rect" or "snip1Rect" or "snip2SameRect" or "snip2DiagRect" => ShapeGeometry.Rectangle,
            "roundRect" or "round1Rect" or "round2SameRect" => ShapeGeometry.RoundedRectangle,
            "ellipse" or "circle" => ShapeGeometry.Ellipse,
            "triangle" => ShapeGeometry.Triangle,
            "rtTriangle" => ShapeGeometry.RightTriangle,
            "diamond" => ShapeGeometry.Diamond,
            "parallelogram" => ShapeGeometry.Parallelogram,
            "trapezoid" => ShapeGeometry.Trapezoid,
            "pentagon" => ShapeGeometry.Pentagon,
            "hexagon" => ShapeGeometry.Hexagon,
            "chevron" => ShapeGeometry.Chevron,
            "homePlate" => ShapeGeometry.HomePlate,
            "rightArrow" or "arrow" or "notchedRightArrow" => ShapeGeometry.ArrowRight,
            "leftArrow" => ShapeGeometry.ArrowLeft,
            "upArrow" => ShapeGeometry.ArrowUp,
            "downArrow" => ShapeGeometry.ArrowDown,
            "star5" or "star4" or "star6" => ShapeGeometry.Star5,
            "line" or "straightConnector1" or "bentConnector2" or "bentConnector3"
                or "curvedConnector2" or "curvedConnector3" => ShapeGeometry.Line,
            null => ShapeGeometry.Rectangle,
            _ => ShapeGeometry.Other
        };

    private StrokeInfo? StrokeOf(XElement? spPr)
    {
        var ln = spPr.Child("ln");
        if (ln == null || ln.Child("noFill") != null) return null;
        var color = FillColor(ln);
        if (!color.HasValue) return null;
        var w = ln.AttrDouble("w");
        return new StrokeInfo
        {
            Color = color.Value,
            Width = Math.Max(0.5, w.HasValue ? Emu.ToPoints(w.Value) : 1)
        };
    }

    private Fill? ParseFill(XElement? element, Dictionary<string, string> rels, string partDir)
    {
        if (element == null || element.Child("noFill") != null) return null;

        var solid = element.Child("solidFill");
        if (solid != null)
        {
            var c = ColorFromContainer(solid);
            if (c.HasValue) return new Fill.Solid { Color = c.Value };
        }

        var grad = element.Child("gradFill");
        if (grad != null)
        {
            var stops = new List<GradientStopSpec>();
            foreach (var gs in grad.DescendantsNamed("gs"))
            {
                var c = ColorFromContainer(gs);
                if (c.HasValue)
                    stops.Add(new GradientStopSpec((gs.AttrDouble("pos") ?? 0) / 100000.0, c.Value));
            }
            stops.Sort((a, b) => a.Position.CompareTo(b.Position));
            var angle = (grad.Child("lin").AttrDouble("ang") ?? 5400000) / 60000.0;
            if (stops.Count >= 2) return new Fill.Gradient { Stops = stops, Angle = angle };
            if (stops.Count == 1) return new Fill.Solid { Color = stops[0].Color };
        }

        var blip = element.Child("blipFill").Descendant("blip");
        var embed = blip.RelAttr("embed");
        if (embed != null && rels.TryGetValue(embed, out var target))
        {
            var path = _pkg.ExtractMedia(PathUtil.Resolve(partDir, target));
            if (path != null) return new Fill.Picture { Path = path };
        }
        return null;
    }

    /// <summary>Solid fill color from a container's DIRECT children — a nested
    /// line or effect fill must never masquerade as the container's own fill.</summary>
    private Color? FillColor(XElement? element)
    {
        if (element == null || element.Child("noFill") != null) return null;
        var solid = element.Child("solidFill");
        return solid == null ? null : ColorFromContainer(solid);
    }

    private Color? ColorFromContainer(XElement? container)
    {
        if (container == null) return null;

        var srgb = container.Descendant("srgbClr");
        if (srgb != null)
        {
            var val = srgb.Attr("val");
            if (val != null) return ColorUtil.WithAlpha(ColorUtil.FromHex(val), AlphaMod(srgb));
        }

        var scheme = container.Descendant("schemeClr");
        if (scheme != null)
        {
            var val = scheme.Attr("val");
            if (val != null)
            {
                var key = val switch
                {
                    "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => val
                };
                var baseColor = _themeColors.TryGetValue(val, out var c1) ? c1
                    : _themeColors.TryGetValue(key, out var c2) ? c2 : Colors.Black;
                return ColorUtil.WithAlpha(baseColor, AlphaMod(scheme));
            }
        }

        var sys = container.Descendant("sysClr");
        if (sys != null)
        {
            var last = sys.Attr("lastClr");
            return last != null ? ColorUtil.FromHex(last) : Colors.Black;
        }
        return null;
    }

    private static double AlphaMod(XElement clr)
    {
        var v = clr.Child("alpha").AttrDouble("val");
        return v.HasValue ? v.Value / 100000.0 : 1.0;
    }

    // ── Animation timing ────────────────────────────────────────────────────

    /// <summary>Click-triggered build steps from p:timing. Top-level par nodes of
    /// the main sequence are clicks; entrance effects reveal, exits hide, and
    /// pRg targets reveal individual paragraphs.</summary>
    private List<BuildStep> ParseBuildSteps(XElement slideRoot)
    {
        var steps = new List<BuildStep>();
        var timing = slideRoot.Child("timing") ?? slideRoot.Descendant("timing");
        if (timing == null) return steps;

        var seqs = timing.DescendantsNamed("seq").ToList();
        var mainSeq = seqs.FirstOrDefault(s => s.Child("cTn").Attr("nodeType") == "mainSeq")
                      ?? seqs.FirstOrDefault();
        var clickList = mainSeq.Child("cTn").Child("childTnLst");
        if (clickList == null) return steps;

        foreach (var clickPar in clickList.Children("par"))
        {
            var step = new BuildStep();
            bool hasClickTrigger = false;

            foreach (var ctn in clickPar.DescendantsNamed("cTn"))
            {
                var nodeType = ctn.Attr("nodeType");
                if (nodeType is "clickEffect" or "clickPar") hasClickTrigger = true;

                var presetClass = ctn.Attr("presetClass");
                if (presetClass == null && nodeType is not ("clickEffect" or "withEffect" or "afterEffect"))
                    continue;

                var targets = ctn.DescendantsNamed("spTgt").ToList();
                if (targets.Count == 0) continue;

                bool isEntrance;
                switch (presetClass)
                {
                    case "entr": isEntrance = true; break;
                    case "exit": isEntrance = false; break;
                    case null:
                        var val = ctn.DescendantsNamed("set").FirstOrDefault()
                            .Descendant("strVal").Attr("val");
                        isEntrance = val != "hidden";
                        break;
                    default: continue;   // emphasis / motion: visibility unchanged
                }

                foreach (var tgt in targets)
                {
                    var spid = tgt.Attr("spid");
                    if (spid == null) continue;
                    var pRg = tgt.Descendant("pRg");
                    var st = pRg.AttrInt("st"); var end = pRg.AttrInt("end");
                    if (isEntrance && pRg != null && st.HasValue && end.HasValue && st <= end)
                    {
                        if (!step.ParagraphReveals.TryGetValue(spid, out var set))
                            step.ParagraphReveals[spid] = set = new HashSet<int>();
                        for (int i = st.Value; i <= end.Value; i++) set.Add(i);
                    }
                    else if (isEntrance) step.Reveals.Add(spid);
                    else step.Hides.Add(spid);
                }
            }

            if (step.IsEmpty) continue;
            if (!hasClickTrigger && steps.Count > 0)
            {
                // with/after-previous: merge into the preceding click.
                var prev = steps[^1];
                prev.Reveals.UnionWith(step.Reveals);
                prev.Hides.UnionWith(step.Hides);
                foreach (var (k, v) in step.ParagraphReveals)
                {
                    if (!prev.ParagraphReveals.TryGetValue(k, out var set))
                        prev.ParagraphReveals[k] = set = new HashSet<int>();
                    set.UnionWith(v);
                }
            }
            else steps.Add(step);
        }
        return steps;
    }
}

public static class ColorUtil
{
    public static Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return Colors.Gray;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    public static Color WithAlpha(Color c, double alpha) =>
        alpha >= 1.0 ? c : Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);
}
