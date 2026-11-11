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
        public OcrTextRange? CharacterRange { get; init; }
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
    private int[] _wordGroups = [];
    private OcrHitIndex _hitIndex = new([]);
    private SelectionState? _selection;
    private SelectionMode _mode = SelectionMode.Smart;
    private Point? _start;
    private int _dragWordStart = -1;
    private TextGroup? _dragGroup;
    private int _focusedWord = -1;
    private int _focusedCharacter;
    private OcrTextPosition? _keyboardAnchor;
    private int _hoverWord = -1;
    private OcrTextPosition? _hoverCharacter;
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
            var detailed = await _screenOcr.RecognizeAsync(image, _ocrCancellation.Token);
            RunOnUi(() =>
            {
                if (_selection is null && !_dragging)
                {
                    _lines = detailed.Lines;
                    _focusedWord = _hoverWord = _hoverLine = -1;
                    _focusedCharacter = 0;
                    _keyboardAnchor = null;
                    _hoverCharacter = null;
                }
                else
                {
                    var merged = new OcrDocumentBuilder(new OcrDocument("", _lines));
                    merged.Add(detailed, new Rectangle(Point.Empty, _frame.Size));
                    _lines = merged.Build().Lines;
                }
                RebuildTextTargets();
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
        _keyboardAnchor = null;
        _hoverWord = _hoverLine = -1;
        _hoverCharacter = null;
        Cursor = Cursors.Cross;
        SetStatus(mode switch
        {
            SelectionMode.Smart => "Smart mode: tap/sweep words or drag a text-free area to select its image.",
            SelectionMode.Text => "Text mode: tap a character or drag across recognized text.",
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
            IReadOnlySet<int>? selectedWords = null;
            OcrTextRange? characterRange = null;
            if (_dragging && _dragGroup is not null && _start is not null && _drawing.Count > 0)
            {
                var end = _drawing[^1];
                if (IsTap(_start.Value, end))
                {
                    if (_mode == SelectionMode.Text && _dragWordStart >= 0)
                    {
                        var position = OcrSelection.PositionAt(_hits, _dragWordStart, _start.Value);
                        characterRange = OcrTextRange.Between(position, position);
                    }
                    else
                    {
                        var range = OcrSelection.TapRange(_hits, _dragGroup, _dragWordStart,
                            _mode == SelectionMode.Text);
                        selectedWords = range is { } target
                            ? Enumerable.Range(target.First, target.Last - target.First + 1).ToHashSet()
                            : _dragGroup.WordIndices.ToHashSet();
                    }
                }
                else
                {
                    var endWord = FindWordAt(end, extendHorizontally: true);
                    if (endWord >= 0)
                    {
                        if (_mode == SelectionMode.Smart)
                        {
                            var first = Math.Min(_dragWordStart, endWord);
                            var last = Math.Max(_dragWordStart, endWord);
                            selectedWords = Enumerable.Range(first, last - first + 1).ToHashSet();
                        }
                        else
                            characterRange = OcrTextRange.Between(
                                OcrSelection.PositionAt(_hits, _dragWordStart, _start.Value),
                                OcrSelection.PositionAt(_hits, endWord, end));
                    }
                }
            }
            else if (!_dragging && _selection?.Mode == SelectionMode.Text)
            {
                selectedWords = _selection.SelectedWords;
                characterRange = _selection.CharacterRange;
            }

            using var selectedBrush = new SolidBrush(Color.FromArgb(100, 67, 132, 255));
            if (characterRange is { } selectedRange)
            {
                for (var i = selectedRange.First.WordIndex; i <= selectedRange.Last.WordIndex; i++)
                {
                    var word = _hits[i].Word;
                    var (first, last) = OcrSelection.CharacterIndices(selectedRange, i, word);
                    DrawHighlight(g, OcrSelection.SelectedBounds(word, first, last), selectedBrush);
                }
            }
            else if (selectedWords is not null)
                foreach (var index in selectedWords)
                    if (index >= 0 && index < _hits.Count)
                        DrawHighlight(g, _hits[index].Word.Bounds, selectedBrush);

            DrawFocus(g, _hoverWord, selectedWords, characterRange);
            if (_focusedWord != _hoverWord) DrawFocus(g, _focusedWord, selectedWords, characterRange);
        }

        if (_dragging && _start is not null && _drawing.Count > 0
            && !IsTap(_start.Value, _drawing[^1]))
        {
            var areaDrag = _mode == SelectionMode.Rectangle
                || _mode == SelectionMode.Smart && _dragGroup is null
                || (_mode is SelectionMode.Smart or SelectionMode.Text) && _dragGroup is not null
                    && FindWordAt(_drawing[^1], extendHorizontally: true) < 0;
            if (areaDrag)
                DrawOutline(g, SelectionImage.RectangleBetween(_start.Value, _drawing[^1], _frame.Size), null);
        }
        if (_dragging && _mode == SelectionMode.Lasso && _drawing.Count > 1)
        {
            using var pen = new Pen(Color.White, 3) { DashStyle = DashStyle.Dash };
            g.DrawLines(pen, _drawing.ToArray());
        }
    }

    private static void DrawHighlight(Graphics graphics, PixelBounds bounds, Brush brush)
    {
        var box = BoundsRectangle(bounds);
        graphics.FillRectangle(brush, box);
        graphics.DrawRectangle(Pens.White, box);
    }

    private void DrawFocus(Graphics graphics, int index, IReadOnlySet<int>? selectedWords,
        OcrTextRange? characterRange)
    {
        if (index < 0 || index >= _hits.Count || selectedWords?.Contains(index) == true)
            return;
        var word = _hits[index].Word;
        var character = index == _hoverWord && _hoverCharacter is { } position
            ? position.CharacterIndex : index == _focusedWord ? _focusedCharacter : -1;
        if (characterRange is { } range && index >= range.First.WordIndex && index <= range.Last.WordIndex)
        {
            var (first, last) = OcrSelection.CharacterIndices(range, index, word);
            if (_mode != SelectionMode.Text || character < 0 || character >= first && character <= last)
                return;
        }
        var bounds = _mode == SelectionMode.Text && character >= 0
            ? OcrSelection.SelectedBounds(word, character, character) : word.Bounds;
        graphics.DrawRectangle(Pens.White, BoundsRectangle(bounds));
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
        _keyboardAnchor = null;
        if (_mode is SelectionMode.Smart or SelectionMode.Text)
        {
            _dragWordStart = FindWordAt(e.Location);
            var groupIndex = FindGroupAt(e.Location, _dragWordStart);
            _dragGroup = groupIndex >= 0 ? _groups[groupIndex] : null;
            if (_dragGroup is null)
            {
                _dragWordStart = -1;
            }
            else
            {
                if (_dragWordStart < 0)
                    _dragWordStart = _dragGroup.WordIndices.MinBy(index =>
                        DistanceSquared(e.Location, CenterOf(WordRectangle(_hits[index].Word))));
                _focusedWord = _dragWordStart;
                _focusedCharacter = OcrSelection.PositionAt(_hits, _dragWordStart, e.Location).CharacterIndex;
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
            var line = _mode is SelectionMode.Smart or SelectionMode.Text ? FindGroupAt(e.Location, hover) : -1;
            OcrTextPosition? character = _mode == SelectionMode.Text && hover >= 0
                ? OcrSelection.PositionAt(_hits, hover, e.Location) : null;
            if (hover != _hoverWord || line != _hoverLine)
            {
                _hoverWord = hover;
                _hoverLine = line;
                _hoverCharacter = character;
                Cursor = hover >= 0 || line >= 0 ? Cursors.Hand : Cursors.Cross;
                Invalidate();
            }
            else if (character != _hoverCharacter)
            {
                var oldBounds = HoverCharacterBounds(_hoverCharacter);
                var newBounds = HoverCharacterBounds(character);
                _hoverCharacter = character;
                if (!oldBounds.IsEmpty) Invalidate(oldBounds);
                if (!newBounds.IsEmpty) Invalidate(newBounds);
            }
            return;
        }
        var point = Clamp(e.Location);
        if (_drawing.Count > 0 && DistanceSquared(_drawing[^1], point) <= 9) return;
        if (_mode == SelectionMode.Lasso || _drawing.Count < 2) _drawing.Add(point);
        else _drawing[^1] = point;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging || _hoverWord < 0 && _hoverLine < 0) return;
        _hoverWord = _hoverLine = -1;
        _hoverCharacter = null;
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
                var endWord = FindWordAt(end, extendHorizontally: true);
                if (IsTap(_start.Value, end))
                    SelectAtPoint(_dragGroup, _dragWordStart, _start.Value);
                else if (endWord >= 0)
                {
                    if (_mode == SelectionMode.Smart)
                    {
                        _focusedWord = endWord;
                        _focusedCharacter = OcrSelection.PositionAt(_hits, endWord, end).CharacterIndex;
                        SelectWords(_dragWordStart, endWord);
                    }
                    else
                    {
                        var first = OcrSelection.PositionAt(_hits, _dragWordStart, _start.Value);
                        var last = OcrSelection.PositionAt(_hits, endWord, end);
                        _focusedWord = last.WordIndex;
                        _focusedCharacter = last.CharacterIndex;
                        _keyboardAnchor = first;
                        SelectCharacters(OcrTextRange.Between(first, last));
                    }
                }
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
                if (IsTap(_start.Value, end))
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

    private void SelectAtPoint(TextGroup group, int wordIndex, Point point)
    {
        if (_mode == SelectionMode.Text && wordIndex >= 0)
        {
            var position = OcrSelection.PositionAt(_hits, wordIndex, point);
            _focusedWord = position.WordIndex;
            _focusedCharacter = position.CharacterIndex;
            _keyboardAnchor = position;
            SelectCharacters(OcrTextRange.Between(position, position));
            return;
        }
        var range = OcrSelection.TapRange(_hits, group, wordIndex, _mode == SelectionMode.Text);
        if (range is { } selected) SelectWords(selected.First, selected.Last);
        else SelectGroup(group);
    }

    private void SelectCharacters(OcrTextRange range)
    {
        var words = OcrSelection.Slice(_hits, range);
        var bounds = SelectionImage.WordBounds(words, _frame.Size);
        if (bounds.IsEmpty) return;
        Commit(new SelectionState
        {
            Mode = SelectionMode.Text,
            Bounds = bounds,
            ImagePng = SelectionImage.Crop(_frame, bounds),
            FirstWord = range.First.WordIndex,
            LastWord = range.Last.WordIndex,
            CharacterRange = range,
            Text = OcrSelection.TextForRange(_hits, range, words),
            TextReady = true
        });
        SetStatus("Text selected. Choose an action or draw a new selection.");
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
            var document = await _cropOcr.RecognizeAsync(image, _ocrCancellation.Token);
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
            var result = await _cropOcr.RecognizeAsync(crop, _ocrCancellation.Token, includeCharacters: true);
            RunOnUi(() =>
            {
                if (version != _selectionVersion) return;
                var merged = new OcrDocumentBuilder(new OcrDocument("", _lines));
                merged.Add(result, area);
                _lines = merged.Build().Lines;
                RebuildTextTargets();
                var wordIndex = FindWordAt(point);
                var groupIndex = FindGroupAt(point, wordIndex);
                if (groupIndex >= 0)
                {
                    var group = _groups[groupIndex];
                    if (wordIndex < 0)
                        wordIndex = group.WordIndices.MinBy(index =>
                            DistanceSquared(point, CenterOf(WordRectangle(_hits[index].Word))));
                    SelectAtPoint(group, wordIndex, point);
                }
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
        _keyboardAnchor = null;
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
        _keyboardAnchor = null;
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

    private void RebuildTextTargets()
    {
        _hits = OcrSelection.Flatten(new OcrDocument("", _lines));
        if (_focusedWord >= 0 && _focusedWord < _hits.Count)
            _focusedCharacter = Math.Min(_focusedCharacter, OcrSelection.CharacterCount(_hits[_focusedWord].Word) - 1);
        _hitIndex = new OcrHitIndex(_hits);
        _groups = TextLayout.Build(_hits);
        _wordGroups = new int[_hits.Count];
        Array.Fill(_wordGroups, -1);
        for (var group = 0; group < _groups.Count; group++)
            foreach (var index in _groups[group].WordIndices)
                _wordGroups[index] = group;
    }

    private int FindWordAt(Point point, bool extendHorizontally = false) =>
        _hitIndex.WordAt(point, extendHorizontally);

    private int FindGroupAt(Point point, int wordIndex)
    {
        if (wordIndex >= 0) return _wordGroups[wordIndex];
        var best = -1;
        var bestArea = long.MaxValue;
        for (var i = 0; i < _groups.Count; i++)
        {
            var bounds = _groups[i].Bounds;
            var area = (long)bounds.Width * bounds.Height;
            bounds.Inflate(3, 3);
            if (!bounds.Contains(point) || area >= bestArea) continue;
            best = i;
            bestArea = area;
        }
        return best;
    }

    private static Rectangle WordRectangle(OcrWord word) => BoundsRectangle(word.Bounds);

    private Rectangle HoverCharacterBounds(OcrTextPosition? position)
    {
        if (position is not { } target || target.WordIndex < 0 || target.WordIndex >= _hits.Count)
            return Rectangle.Empty;
        var bounds = BoundsRectangle(OcrSelection.SelectedBounds(_hits[target.WordIndex].Word,
            target.CharacterIndex, target.CharacterIndex));
        bounds.Inflate(3, 3);
        return bounds;
    }

    private static Rectangle BoundsRectangle(PixelBounds bounds) => Rectangle.FromLTRB(
        (int)Math.Floor(bounds.X), (int)Math.Floor(bounds.Y),
        (int)Math.Ceiling(bounds.X + bounds.Width),
        (int)Math.Ceiling(bounds.Y + bounds.Height));

    private static Point CenterOf(Rectangle bounds) =>
        new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Math.Max(0, ClientSize.Width - 1)),
        Math.Clamp(point.Y, 0, Math.Max(0, ClientSize.Height - 1)));

    private static int DistanceSquared(Point first, Point last) =>
        (first.X - last.X) * (first.X - last.X) + (first.Y - last.Y) * (first.Y - last.Y);

    private static bool IsTap(Point first, Point last) => DistanceSquared(first, last) < 16;

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
        else if ((_mode is SelectionMode.Smart or SelectionMode.Text) && _hits.Count > 0
            && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            ActiveControl = null;
            Focus();
            var step = e.KeyCode is Keys.Left or Keys.Up ? -1 : 1;
            var oldPosition = _focusedWord < 0 ? new OcrTextPosition(0, 0)
                : new OcrTextPosition(_focusedWord, _focusedCharacter);
            OcrTextPosition next;
            if (_focusedWord < 0) next = oldPosition;
            else if (_mode == SelectionMode.Text && (e.KeyCode is Keys.Left or Keys.Right))
                next = OcrSelection.MoveCharacter(_hits, oldPosition, step);
            else
            {
                var index = Math.Clamp(_focusedWord + step, 0, _hits.Count - 1);
                next = new OcrTextPosition(index, Math.Min(_focusedCharacter,
                    OcrSelection.CharacterCount(_hits[index].Word) - 1));
            }
            _focusedWord = next.WordIndex;
            _focusedCharacter = next.CharacterIndex;
            if (e.Shift && _mode == SelectionMode.Text)
            {
                _keyboardAnchor ??= oldPosition;
                SelectCharacters(OcrTextRange.Between(_keyboardAnchor.Value, next));
            }
            else if (e.Shift && _selection?.Mode == SelectionMode.Text)
                SelectWords(_selection.FirstWord, _focusedWord);
            else _keyboardAnchor = null;
            var word = _hits[_focusedWord].Word;
            var text = _mode == SelectionMode.Text && word.Characters is { } characters
                ? characters[_focusedCharacter].Text : word.Text;
            SetStatus($"{(_mode == SelectionMode.Text ? "Character" : "Word")}: {text}. Press Enter to select.");
            Invalidate();
            e.Handled = true;
        }
        else if (_mode is SelectionMode.Smart or SelectionMode.Text && _focusedWord >= 0 && e.KeyCode is Keys.Enter or Keys.Space && ActiveControl is not Button)
        {
            if (_mode == SelectionMode.Text)
            {
                var position = new OcrTextPosition(_focusedWord, _focusedCharacter);
                _keyboardAnchor = position;
                SelectCharacters(OcrTextRange.Between(position, position));
            }
            else SelectWords(_focusedWord, _focusedWord);
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
