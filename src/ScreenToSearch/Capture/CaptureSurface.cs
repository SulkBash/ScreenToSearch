using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace screen_to_search;

internal enum SelectionMode { Smart, Text, Rectangle, Lasso }

internal sealed class CaptureSurface : Form
{
    private sealed class SelectionState
    {
        public required SelectionMode Mode { get; set; }
        public required Rectangle Bounds { get; init; }
        public required byte[] ImagePng { get; init; }
        public byte[]? OcrImagePng { get; set; }
        public IReadOnlyList<Point>? Polygon { get; init; }
        public int FirstWord { get; init; } = -1;
        public int LastWord { get; init; } = -1;
        public IReadOnlySet<int>? SelectedWords { get; init; }
        public bool AutoClassify { get; init; }
        public string Text { get; set; } = "";
        public bool TextReady { get; set; }
    }

    private readonly Bitmap _frame;
    private byte[]? _framePng;
    private readonly AppSettings _settings;
    private readonly NeuralOcrService _cropOcr = new();
    private readonly ScreenOcrPipeline _screenOcr = new();
    private readonly CancellationTokenSource _ocrCancellation = new();
    private readonly FlowLayoutPanel _toolbar;
    private readonly FlowLayoutPanel _actions;
    private readonly Stack<SelectionState?> _history = new();
    private readonly List<Point> _drawing = new();
    private IReadOnlyList<OcrHit> _hits = [];
    private IReadOnlyList<OcrLine> _lines = [];
    private IReadOnlyList<TextGroup> _groups = [];
    private SelectionState? _selection;
    private SelectionMode _mode = SelectionMode.Smart;
    private Point? _start;
    private int _dragWordStart = -1;
    private TextGroup? _dragGroup;
    private int _focusedWord = -1;
    private int _hoverWord = -1;
    private int _hoverLine = -1;
    private int _selectionVersion;
    private bool _dragging;
    private bool _resourcesDisposed;

    public CaptureSurface(MonitorFrame captured, AppSettings settings)
    {
        _framePng = captured.Png;
        _settings = settings;
        using var stream = new MemoryStream(captured.Png);
        using var decoded = new Bitmap(stream);
        _frame = new Bitmap(decoded);

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Screen to Search surface";
        StartPosition = FormStartPosition.Manual;
        Bounds = captured.Monitor.PixelBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;

        _toolbar = new FlowLayoutPanel
        {
            BackColor = Color.FromArgb(28, 34, 50),
            Padding = new Padding(5),
            WrapContents = false,
            AutoScroll = true,
            Location = new Point(14, 14),
            Size = new Size(Math.Min(314, Math.Max(200, ClientSize.Width - 28)), 48),
            TabStop = false
        };
        AddToolbarButton("Smart (A)", () => SetMode(SelectionMode.Smart));
        AddToolbarButton("Text (T)", () => SetMode(SelectionMode.Text));
        AddToolbarButton("Close", Close);
        Controls.Add(_toolbar);
        AccessibleDescription = "Reading screen text… You can drag an area while it scans.";

        _actions = new FlowLayoutPanel
        {
            BackColor = Color.FromArgb(28, 34, 50),
            Padding = new Padding(6),
            WrapContents = true,
            AutoScroll = true,
            Size = new Size(Math.Min(690, Math.Max(240, ClientSize.Width - 20)),
                ClientSize.Width < 700 ? 92 : 52),
            Visible = false,
            TabStop = false
        };
        AddActionButton("Search text", SelectionActionKind.SearchText);
        AddActionButton("Translate", SelectionActionKind.Translate);
        AddActionButton("Copy text", SelectionActionKind.CopyText);
        AddActionButton("Copy image", SelectionActionKind.CopyImage);
        Controls.Add(_actions);
    }

    private void AddToolbarButton(string label, Action action)
    {
        var button = MakeButton(label, 94);
        button.Click += (_, _) => action();
        _toolbar.Controls.Add(button);
    }

    private void AddActionButton(string label, SelectionActionKind kind)
    {
        var button = MakeButton(label, 122);
        button.Click += (_, _) => ExecuteAction(kind);
        _actions.Controls.Add(button);
    }

    private static Button MakeButton(string label, int width) => new()
    {
        Text = label,
        AccessibleName = label,
        Width = width,
        Height = 34,
        Margin = new Padding(2),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(51, 70, 111),
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 9.5f),
        Cursor = Cursors.Hand,
        TabStop = true
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _ = RecognizeFrameAsync();
    }

    private async Task RecognizeFrameAsync()
    {
        try
        {
            var image = _framePng;
            _framePng = null;
            if (image is null) return;
            var detailed = await _screenOcr.RecognizeAsync(image,
                _settings.OcrLanguage, _ocrCancellation.Token);
            RunOnUi(() =>
            {
                if (_selection is null && !_dragging)
                {
                    _lines = detailed.Lines;
                    _focusedWord = _hoverWord = _hoverLine = -1;
                }
                else
                {
                    var merged = new OcrDocumentBuilder(new OcrDocument("", _lines));
                    merged.Add(detailed, new Rectangle(Point.Empty, _frame.Size));
                    _lines = merged.Build().Lines;
                }
                _hits = OcrSelection.Flatten(new OcrDocument("", _lines));
                _groups = TextLayout.Build(_hits);
                if (_selection is null)
                    SetStatus(_hits.Count == 0
                        ? "No words found. Drag an area to select its image."
                        : $"{_hits.Count} words ready. Tap/sweep text, or drag a text-free area for its image.");
                Invalidate();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunOnUi(() => SetStatus($"OCR unavailable: {ex.Message}"));
        }
    }

    private void SetMode(SelectionMode mode)
    {
        _mode = mode;
        _hoverWord = _hoverLine = -1;
        Cursor = Cursors.Cross;
        SetStatus(mode switch
        {
            SelectionMode.Smart => "Smart mode: tap/sweep words or drag a text-free area to select its image.",
            SelectionMode.Text => "Text mode: tap a word or drag across recognized words.",
            SelectionMode.Rectangle => "Rectangle mode: drag around text or an image.",
            _ => "Lasso mode: draw around an image or text."
        });
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_frame, ClientRectangle);
        using (var shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
            g.FillRectangle(shade, ClientRectangle);

        if (_selection is not null)
        {
            var state = g.Save();
            if (_selection.Polygon is { Count: >= 3 } polygon)
            {
                using var path = new GraphicsPath();
                path.AddPolygon(polygon.ToArray());
                g.SetClip(path, CombineMode.Intersect);
            }
            else g.SetClip(_selection.Bounds, CombineMode.Intersect);
            g.DrawImage(_frame, ClientRectangle);
            g.Restore(state);
            DrawOutline(g, _selection.Bounds, _selection.Polygon);
        }

        if (_mode is SelectionMode.Smart or SelectionMode.Text)
        {
            IReadOnlySet<int>? preview = null;
            if (_dragging && _dragGroup is not null && _start is not null
                && _drawing.Count > 0 && DistanceSquared(_start.Value, _drawing[^1]) < 64)
            {
                var range = OcrSelection.TapRange(_hits, _dragGroup, _dragWordStart,
                    _mode == SelectionMode.Text);
                preview = range is { } target
                    ? Enumerable.Range(target.First, target.Last - target.First + 1).ToHashSet()
                    : _dragGroup.WordIndices.ToHashSet();
            }
            using var selectedBrush = new SolidBrush(Color.FromArgb(100, 67, 132, 255));
            for (var i = 0; i < _hits.Count; i++)
            {
                var selected = preview?.Contains(i) == true
                    || !_dragging && _selection?.Mode == SelectionMode.Text && _selection.SelectedWords?.Contains(i) == true;
                if (!selected && i != _hoverWord && i != _focusedWord)
                    continue;

                var box = WordRectangle(_hits[i].Word);
                if (selected)
                    g.FillRectangle(selectedBrush, box);
                g.DrawRectangle(Pens.White, box);
            }
        }

        if (_dragging && _start is not null && (_mode is SelectionMode.Smart or SelectionMode.Text or SelectionMode.Rectangle) && _drawing.Count > 0
            && DistanceSquared(_start.Value, _drawing[^1]) >= 64)
            DrawOutline(g, SelectionImage.RectangleBetween(_start.Value, _drawing[^1], _frame.Size), null);
        if (_dragging && _mode == SelectionMode.Lasso && _drawing.Count > 1)
        {
            using var pen = new Pen(Color.White, 3) { DashStyle = DashStyle.Dash };
            g.DrawLines(pen, _drawing.ToArray());
        }
    }

    private static void DrawOutline(Graphics graphics, Rectangle bounds, IReadOnlyList<Point>? polygon)
    {
        using var outer = new Pen(Color.FromArgb(45, 105, 244), 5);
        using var inner = new Pen(Color.White, 2);
        if (polygon is { Count: >= 3 })
        {
            graphics.DrawPolygon(outer, polygon.ToArray());
            graphics.DrawPolygon(inner, polygon.ToArray());
        }
        else if (bounds.Width > 0 && bounds.Height > 0)
        {
            graphics.DrawRectangle(outer, bounds);
            graphics.DrawRectangle(inner, bounds);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        if (_mode is SelectionMode.Smart or SelectionMode.Text)
        {
            _dragGroup = TextLayout.At(_groups, _hits, e.Location);
            if (_dragGroup is null)
            {
                _dragWordStart = -1;
            }
            else
            {
                _dragWordStart = FindWordAt(e.Location);
                if (_dragWordStart < 0)
                    _dragWordStart = _dragGroup.WordIndices.MinBy(index =>
                        DistanceSquared(e.Location, CenterOf(WordRectangle(_hits[index].Word))));
                _focusedWord = _dragWordStart;
            }
        }
        _start = Clamp(e.Location);
        _drawing.Clear();
        _drawing.Add(_start.Value);
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            var hover = _mode is SelectionMode.Smart or SelectionMode.Text ? FindWordAt(e.Location) : -1;
            var line = _mode is SelectionMode.Smart or SelectionMode.Text ? FindGroupAt(e.Location) : -1;
            if (hover != _hoverWord || line != _hoverLine)
            {
                _hoverWord = hover;
                _hoverLine = line;
                Cursor = hover >= 0 || line >= 0 ? Cursors.Hand : Cursors.Cross;
                Invalidate();
            }
            return;
        }
        var point = Clamp(e.Location);
        if (_drawing.Count > 0 && DistanceSquared(_drawing[^1], point) <= 9) return;
        _drawing.Add(point);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging || _hoverWord < 0 && _hoverLine < 0) return;
        _hoverWord = _hoverLine = -1;
        Cursor = Cursors.Cross;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        Capture = false;
        _dragging = false;
        var end = Clamp(e.Location);
        try
        {
            if (_mode is SelectionMode.Smart or SelectionMode.Text && _dragGroup is not null && _start is not null)
            {
                var endWord = FindWordAt(end);
                if (DistanceSquared(_start.Value, end) < 64)
                    SelectAtPoint(_dragGroup, _dragWordStart);
                else if (endWord >= 0)
                    SelectWords(_dragWordStart, endWord);
                else
                {
                    var bounds = SelectionImage.RectangleBetween(_start.Value, end, _frame.Size);
                    var words = OcrSelection.WordsInside(_hits, bounds);
                    if (words.Count > 0) SelectSpatialWords(bounds, words);
                    else if (_mode == SelectionMode.Smart) SelectShape(bounds, null, autoClassify: true);
                    else SetStatus("No words found in that area.");
                }
            }
            else if (_mode == SelectionMode.Text && _start is not null)
            {
                if (DistanceSquared(_start.Value, end) < 64)
                    _ = RecognizeAtPointAsync(end, _selectionVersion);
                else SetStatus("No text found at the drag start. Try Smart or Rectangle.");
            }
            else if (_mode == SelectionMode.Smart && _start is not null)
            {
                var bounds = SelectionImage.RectangleBetween(_start.Value, end, _frame.Size);
                if (bounds.Width >= 8 && bounds.Height >= 8)
                {
                    var words = OcrSelection.WordsInside(_hits, bounds);
                    if (words.Count > 0) SelectSpatialWords(bounds, words);
                    else SelectShape(bounds, null, autoClassify: true);
                }
                else _ = RecognizeAtPointAsync(end, _selectionVersion);
            }
            else if (_mode == SelectionMode.Rectangle && _start is not null)
                SelectShape(SelectionImage.RectangleBetween(_start.Value, end, _frame.Size), null);
            else if (_mode == SelectionMode.Lasso)
            {
                _drawing.Add(end);
                SelectShape(SelectionImage.LassoBounds(_drawing, _frame.Size), _drawing.ToArray());
            }
        }
        catch (Exception ex) { SetStatus($"Selection failed: {ex.Message}"); }
        _start = null;
        _drawing.Clear();
        _dragWordStart = -1;
        _dragGroup = null;
        Invalidate();
    }

    private void SelectGroup(TextGroup group)
    {
        var bounds = SelectionImage.WordBounds(group.WordIndices.Select(i => _hits[i].Word), _frame.Size);
        if (bounds.IsEmpty) return;
        Commit(new SelectionState
        {
            Mode = SelectionMode.Text,
            Bounds = bounds,
            ImagePng = SelectionImage.Crop(_frame, bounds),
            FirstWord = group.WordIndices[0],
            LastWord = group.WordIndices[^1],
            SelectedWords = group.WordIndices.ToHashSet(),
            Text = group.Text,
            TextReady = true
        });
        SetStatus("Text selected. Choose an action or draw a new selection.");
    }

    private void SelectAtPoint(TextGroup group, int wordIndex)
    {
        var range = OcrSelection.TapRange(_hits, group, wordIndex, _mode == SelectionMode.Text);
        if (range is { } selected) SelectWords(selected.First, selected.Last);
        else SelectGroup(group);
    }

    private void SelectWords(int first, int last)
    {
        if (first < 0 || last < 0 || _hits.Count == 0) return;
        var low = Math.Min(first, last);
        var high = Math.Max(first, last);
        var bounds = SelectionImage.WordBounds(_hits.Skip(low).Take(high - low + 1)
            .Select(hit => hit.Word), _frame.Size);
        if (bounds.IsEmpty) return;
        Commit(new SelectionState
        {
            Mode = SelectionMode.Text,
            Bounds = bounds,
            ImagePng = SelectionImage.Crop(_frame, bounds),
            FirstWord = low,
            LastWord = high,
            SelectedWords = Enumerable.Range(low, high - low + 1).ToHashSet(),
            Text = OcrSelection.TextBetween(_hits, low, high)
        });
        SetStatus("Text selected. Choose an action or draw a new selection.");
    }

    private void SelectSpatialWords(Rectangle bounds, IReadOnlyList<int> words)
    {
        bounds = Rectangle.Union(bounds, SelectionImage.WordBounds(words.Select(i => _hits[i].Word),
            _frame.Size));
        Commit(new SelectionState
        {
            Mode = SelectionMode.Text,
            Bounds = bounds,
            ImagePng = SelectionImage.Crop(_frame, bounds),
            SelectedWords = words.ToHashSet(),
            Text = OcrSelection.TextForIndices(_hits, words),
            TextReady = true
        });
        SetStatus("Text selected from the area. Choose a text or image action.");
    }

    private void SelectShape(Rectangle bounds, IReadOnlyList<Point>? polygon, bool autoClassify = false)
    {
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            SetStatus("Draw a larger selection.");
            return;
        }
        var image = SelectionImage.Crop(_frame, bounds, polygon);
        var ocrImage = polygon is null ? image : SelectionImage.Crop(_frame, bounds, polygon, whiteOutside: true);
        var selection = new SelectionState
        {
            Mode = autoClassify ? SelectionMode.Rectangle : _mode,
            Bounds = bounds,
            Polygon = polygon,
            ImagePng = image,
            OcrImagePng = ocrImage,
            AutoClassify = autoClassify
        };
        Commit(selection);
        SetStatus("Image selected. Reading text in the crop…");
        _ = RecognizeSelectionAsync(selection, ocrImage, _selectionVersion);
    }

    private async Task RecognizeSelectionAsync(SelectionState selection, byte[] image, int version)
    {
        try
        {
            var document = await _cropOcr.RecognizeForLanguageAsync(image,
                _settings.OcrLanguage, _ocrCancellation.Token);
            RunOnUi(() =>
            {
                if (version != _selectionVersion || !ReferenceEquals(selection, _selection)) return;
                selection.Text = document.Text.Trim();
                selection.TextReady = true;
                selection.OcrImagePng = null;
                if (selection.AutoClassify && selection.Text.Length > 0)
                    selection.Mode = SelectionMode.Text;
                SetStatus(selection.Text.Length > 0
                    ? "Text recognized in the area. Choose a text or image action."
                    : "No text found. Rectangle selected for image actions.");
                Invalidate();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunOnUi(() => SetStatus($"Crop OCR unavailable: {ex.Message}"));
        }
    }

    private async Task RecognizeAtPointAsync(Point point, int version)
    {
        try
        {
            SetStatus("Checking for small text at that point…");
            var area = Rectangle.Intersect(new Rectangle(Point.Empty, _frame.Size),
                new Rectangle(point.X - 120, point.Y - 55, 240, 110));
            var crop = SelectionImage.Crop(_frame, area);
            var result = await _cropOcr.RecognizeForLanguageAsync(crop,
                _settings.OcrLanguage, _ocrCancellation.Token);
            RunOnUi(() =>
            {
                if (version != _selectionVersion) return;
                var merged = new OcrDocumentBuilder(new OcrDocument("", _lines));
                merged.Add(result, area);
                _lines = merged.Build().Lines;
                _hits = OcrSelection.Flatten(new OcrDocument("", _lines));
                _groups = TextLayout.Build(_hits);
                var target = TextLayout.At(_groups, _hits, point);
                if (target is not null) SelectAtPoint(target, FindWordAt(point));
                else SetStatus("No text at that point. Drag around it to select the image.");
                Invalidate();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunOnUi(() => SetStatus($"Small-text OCR unavailable: {ex.Message}"));
        }
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }

    private void Commit(SelectionState selection)
    {
        SaveHistory();
        _selection = selection;
        _selectionVersion++;
        _actions.Visible = true;
        PositionActions();
        Invalidate();
    }

    private void Undo()
    {
        if (_history.Count == 0) return;
        _selection = _history.Pop();
        _selectionVersion++;
        _actions.Visible = _selection is not null;
        PositionActions();
        if (_selection is { TextReady: false, OcrImagePng: not null } pending)
            _ = RecognizeSelectionAsync(pending, pending.OcrImagePng, _selectionVersion);
        SetStatus(_selection is null ? "Selection undone." : "Previous selection restored.");
        Invalidate();
    }

    private void ClearSelection()
    {
        if (_selection is null) return;
        SaveHistory();
        _selection = null;
        _selectionVersion++;
        _actions.Visible = false;
        SetStatus("Selection cleared. Draw or tap again.");
        Invalidate();
    }

    private void SaveHistory()
    {
        const int limit = 10;
        if (_history.Count >= limit)
        {
            var recent = _history.Take(limit - 1).Reverse().ToArray();
            _history.Clear();
            foreach (var item in recent) _history.Push(item);
        }
        _history.Push(_selection);
    }

    private void PositionActions()
    {
        if (_selection is null) return;
        var area = _selection.Bounds;
        var x = Math.Clamp(area.Left + (area.Width - _actions.Width) / 2, 10,
            Math.Max(10, ClientSize.Width - _actions.Width - 10));
        var below = area.Bottom + 12;
        var y = below + _actions.Height <= ClientSize.Height - 10
            ? below : Math.Max(68, area.Top - _actions.Height - 12);
        _actions.Location = new Point(x, Math.Min(y, ClientSize.Height - _actions.Height - 10));
        _actions.BringToFront();
    }

    private void ExecuteAction(SelectionActionKind kind)
    {
        if (_selection is null) { SetStatus("Select text or an image first."); return; }
        try
        {
            var result = SelectionActions.Execute(kind,
                new SelectionPayload(_selection.Text, _selection.ImagePng), _settings, this);
            SetStatus(result.Message);
            if (result.CloseSurface) Close();
        }
        catch (Exception ex) { SetStatus($"Action failed: {ex.Message}"); }
    }

    private int FindWordAt(Point point)
    {
        for (var i = 0; i < _hits.Count; i++)
        {
            var rect = WordRectangle(_hits[i].Word);
            rect.Inflate(3, 3);
            if (rect.Contains(point)) return i;
        }
        return -1;
    }

    private int FindGroupAt(Point point)
    {
        var group = TextLayout.At(_groups, _hits, point);
        return group is null ? -1 : Enumerable.Range(0, _groups.Count)
            .First(index => ReferenceEquals(_groups[index], group));
    }

    private static Rectangle WordRectangle(OcrWord word) => Rectangle.FromLTRB(
        (int)Math.Floor(word.Bounds.X), (int)Math.Floor(word.Bounds.Y),
        (int)Math.Ceiling(word.Bounds.X + word.Bounds.Width),
        (int)Math.Ceiling(word.Bounds.Y + word.Bounds.Height));

    private static Point CenterOf(Rectangle bounds) =>
        new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Math.Max(0, ClientSize.Width - 1)),
        Math.Clamp(point.Y, 0, Math.Max(0, ClientSize.Height - 1)));

    private static int DistanceSquared(Point first, Point last) =>
        (first.X - last.X) * (first.X - last.X) + (first.Y - last.Y) * (first.Y - last.Y);

    private void SetStatus(string message) => AccessibleDescription = message;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
        else if (e.KeyCode is Keys.Delete or Keys.Back) { ClearSelection(); e.Handled = true; }
        else if (e.KeyCode == Keys.A) { SetMode(SelectionMode.Smart); e.Handled = true; }
        else if (e.KeyCode == Keys.T) { SetMode(SelectionMode.Text); e.Handled = true; }
        else if (e.KeyCode == Keys.R) { SetMode(SelectionMode.Rectangle); e.Handled = true; }
        else if (e.KeyCode == Keys.L) { SetMode(SelectionMode.Lasso); e.Handled = true; }
        else if (_mode is SelectionMode.Smart or SelectionMode.Text && _hits.Count > 0 && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            ActiveControl = null;
            Focus();
            var step = e.KeyCode is Keys.Left or Keys.Up ? -1 : 1;
            _focusedWord = Math.Clamp(_focusedWord < 0 ? 0 : _focusedWord + step, 0, _hits.Count - 1);
            if (e.Shift && _selection?.Mode == SelectionMode.Text)
                SelectWords(_selection.FirstWord, _focusedWord);
            SetStatus($"Word {_focusedWord + 1} of {_hits.Count}: {_hits[_focusedWord].Word.Text}. Press Enter to select.");
            Invalidate();
            e.Handled = true;
        }
        else if (_mode is SelectionMode.Smart or SelectionMode.Text && _focusedWord >= 0 && e.KeyCode is Keys.Enter or Keys.Space && ActiveControl is not Button)
        {
            SelectWords(_focusedWord, _focusedWord);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_toolbar is null) return;
        _toolbar.Width = Math.Min(314, Math.Max(200, ClientSize.Width - 28));
        _actions.Width = Math.Min(690, Math.Max(240, ClientSize.Width - 20));
        _actions.Height = ClientSize.Width < 700 ? 92 : 52;
        PositionActions();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _ocrCancellation.Cancel();
            _ocrCancellation.Dispose();
            _framePng = null;
            _frame.Dispose();
        }
        base.Dispose(disposing);
    }
}
