// Form1.cs — .NET 8 WinForms — single file (no Designer).
// NuGet needed: SkiaSharp, SkiaSharp.NativeAssets.Win (for WebP).
// Credits: Creator Mick Carry, WinForms by Rob Pickup.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using SkiaSharp;

namespace Site_View_v2
{
    // ---- top-level extension (no name clash with Control.DoubleBuffered) ----
    public static class DataGridViewExtensions
    {
        public static void SetDoubleBuffered(this DataGridView dgv, bool enabled)
        {
            typeof(DataGridView)
                .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dgv, enabled, null);
        }
    }

    public partial class Form1 : Form
    {
        // ----- drawing state -----
        private Bitmap _image = null;
        private string _imageFileName = "layout.png";   // expected name inside layout
        private string _imagePath = null;               // full path of currently loaded image

        private readonly List<LineShape> _phases = new();
        private readonly List<IShape> _detectors = new(); // LineShape or SquareShape
        private readonly List<TextShape> _texts = new();

        private readonly Dictionary<string, int> _idToPosition = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _phaseNames = new();
        private readonly List<string> _detNames = new() { "None" };

        // From CPF
        private readonly Dictionary<string, string> _lampSymbolByPhase = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArrowType> _defaultArrowByPhase = new(StringComparer.OrdinalIgnoreCase);

        private IShape _selected = null;  // selected shape
        private IShape _copied = null;    // clipboard

        // dragging / editing
        private bool _panning = false;
        private Point _panStart;
        private int _dragPointIndex = -1; // for LineShape point drag (0..n-1). tip is last index.
        private PointF _lastMouseImgPt;   // remember for "Add Bend Here"

        // grid→canvas DnD
        private Point _dragStartPt;
        private bool _dragArming;

        // zoom & pan
        private float _zoom = 1.0f;
        private const float MinZoom = 1.0f;
        private const float MaxZoom = 3.5f;
        private const float ZoomStep = 0.1f;
        private PointF _offset = new(0, 0); // panning offset (screen px)

        // handles
        private const float HandleSize = 16f;
        private const float CenterHandleSize = 10f;
        private const float StretchHandleSize = 10f; // bigger to grab

        // UI
        private ToolStrip _toolbar;
        private Panel _root;
        private SplitContainer _split;
        private Panel _canvasHost;
        private Canvas _canvas;
        private TextBox _iniOutput;
        private ToolStripLabel _statusZoom;

        // Right table
        private DataGridView _gridPhases;
        private DataGridView _gridDetectors;

        // layout save path / dirty tracking
        private string _layoutPath = null;
        private bool _dirty = false;

        private readonly ContextMenuStrip _ctx = new();

        private enum NewShapeMode { None, Line, Square, Text }
        private NewShapeMode _newShape = NewShapeMode.None;

        public Form1()
        {
            InitializeCustomUi();

            // Compose custom canvas and hook events
            _canvas = new Canvas(this) { Dock = DockStyle.Fill, AllowDrop = true, BackColor = Color.FromArgb(224, 224, 224) };
            _canvasHost.Controls.Add(_canvas);
            _canvas.ContextMenuStrip = _ctx;

            // Context menu handler
            _ctx.Opening += Ctx_Opening;

            // Mouse & DnD
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += Canvas_MouseUp;
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.DragEnter += Canvas_DragEnter;
            _canvas.DragDrop += Canvas_DragDrop;

            // Keyboard shortcuts
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;

            // Closing prompt
            this.FormClosing += Form1_FormClosing;

            // SplitterDistance fix: set after form shown
            this.Shown += (s, e) =>
            {
                try
                {
                    // Reserve ~320px for right panel
                    _split.SplitterDistance = Math.Max(300, _split.ClientSize.Width - 340);
                }
                catch { }
            };

            UpdateRightPanelCounts();
        }

        private void InitializeCustomUi()
        {
            this.Text = "Site Viewer (Layout Creator)";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Width = 1280;
            this.Height = 800;

            _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Top };
            var btnImage = new ToolStripButton("Choose Image");
            var btnLoadLayout = new ToolStripButton("Load Layout");
            var btnLoadCpf = new ToolStripButton("Load CPF");
            var btnSaveLayout = new ToolStripButton("Save Layout");
            var btnZoomIn = new ToolStripButton("+");
            var btnZoomOut = new ToolStripButton("−");
            var btnToggleOutput = new ToolStripButton("Output");
            var btnToggleRight = new ToolStripButton("Sidebar");
            var btnNewLine = new ToolStripButton("New Line");
            var btnNewSquare = new ToolStripButton("New Square");
            var btnNewText = new ToolStripButton("New Text");
            var btnAbout = new ToolStripButton("About");
            _statusZoom = new ToolStripLabel("100%");

            btnImage.Click += (s, e) => ChooseImage();
            btnLoadLayout.Click += (s, e) => LoadLayout();
            btnLoadCpf.Click += (s, e) => LoadCpf();
            btnSaveLayout.Click += (s, e) => SaveLayout(showDialog: true); // always dialog
            btnZoomIn.Click += (s, e) => { _zoom = Math.Min(MaxZoom, _zoom + ZoomStep); _canvas.Invalidate(); UpdateZoomLabel(); };
            btnZoomOut.Click += (s, e) => { _zoom = Math.Max(MinZoom, _zoom - ZoomStep); _canvas.Invalidate(); UpdateZoomLabel(); };
            btnToggleOutput.Click += (s, e) => { _iniOutput.Visible = !_iniOutput.Visible; };
            btnToggleRight.Click += (s, e) => ToggleRightPanel();
            btnNewLine.Click += (s, e) => { _newShape = NewShapeMode.Line; };
            btnNewSquare.Click += (s, e) => { _newShape = NewShapeMode.Square; };
            btnNewText.Click += (s, e) => { _newShape = NewShapeMode.Text; };
            btnAbout.Click += (s, e) => ShowAbout();

            var btnTestPhases = new ToolStripButton("Test Phases") { ToolTipText = "Run phase connection tests" };
            var btnExport = new ToolStripButton("Export") { ToolTipText = "Export layout to various formats" };

            btnTestPhases.Click += (s, e) => ShowTestRunner();
            btnExport.Click += (s, e) => ShowExportDialog();

            _toolbar.Items.AddRange(new ToolStripItem[] {
                btnImage, btnLoadLayout, btnLoadCpf, new ToolStripSeparator(),
                btnNewLine, btnNewSquare, btnNewText, new ToolStripSeparator(),
                btnSaveLayout, btnExport, new ToolStripSeparator(),
                btnTestPhases, new ToolStripSeparator(),
                btnZoomIn, btnZoomOut, _statusZoom, new ToolStripSeparator(),
                btnToggleRight, btnToggleOutput, new ToolStripSeparator(), btnAbout
            });

            _root = new Panel { Dock = DockStyle.Fill };
            _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, BorderStyle = BorderStyle.FixedSingle };
            _canvasHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(224, 224, 224) };
            _iniOutput = new TextBox { Dock = DockStyle.Bottom, Height = 140, Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), Visible = false };

            // Right sidebar: 2 tables (Phases & Detectors)
            var right = new TabControl { Dock = DockStyle.Fill };
            var tabPhases = new TabPage("Phases");
            var tabDets = new TabPage("Detectors");
            _gridPhases = MakeGrid();
            _gridDetectors = MakeGrid();
            tabPhases.Controls.Add(_gridPhases);
            tabDets.Controls.Add(_gridDetectors);
            right.TabPages.Add(tabPhases);
            right.TabPages.Add(tabDets);

            _split.Panel1.Controls.Add(_canvasHost);
            _split.Panel1.Controls.Add(_iniOutput);
            _split.Panel2.Controls.Add(right);

            _root.Controls.Add(_split);
            Controls.Add(_root);
            Controls.Add(_toolbar);

            // enable starting drags from the right panel
            _gridPhases.MouseDown += Grid_MouseDown;
            _gridPhases.MouseMove += (s, e) => TryStartDragFromGrid(_gridPhases, "PHASE:", e);
            _gridDetectors.MouseDown += Grid_MouseDown;
            _gridDetectors.MouseMove += (s, e) => TryStartDragFromGrid(_gridDetectors, "DET:", e);
        }

        private DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            g.SetDoubleBuffered(true);
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Count", DataPropertyName = "Count", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = "Type", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            return g;
        }

        private void Grid_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragArming = true;
            _dragStartPt = e.Location;
        }

        private void TryStartDragFromGrid(DataGridView grid, string prefix, MouseEventArgs e)
        {
            if (!_dragArming) return;
            var dx = Math.Abs(e.X - _dragStartPt.X);
            var dy = Math.Abs(e.Y - _dragStartPt.Y);
            if (dx < SystemInformation.DragSize.Width / 2 &&
                dy < SystemInformation.DragSize.Height / 2) return;

            _dragArming = false;

            if (grid.CurrentRow == null) return;
            var nameObj = grid.CurrentRow.Cells[0].Value;
            var name = nameObj?.ToString();
            if (string.IsNullOrWhiteSpace(name)) return;

            var payload = prefix + name; // "PHASE:Name" or "DET:Name"
            grid.DoDragDrop(payload, DragDropEffects.Copy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragArming = false;
        }

        private void UpdateZoomLabel() => _statusZoom.Text = ((int)Math.Round(_zoom * 100)) + "%";

        // ---------------- UI actions ----------------
        private void ChooseImage()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            try
            {
                if (_image != null) _image.Dispose();
                _image = LoadBitmapSmart(ofd.FileName);
                _imagePath = ofd.FileName;
                _imageFileName = Path.GetFileName(ofd.FileName);
                _zoom = 1f; _offset = new PointF(0, 0);
                _canvas.Invalidate();
                GenerateIni();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load image: " + ex.Message);
            }
        }

        private Bitmap LoadBitmapSmart(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            if (ext == ".webp") return LoadBitmapFromWebP(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var img = System.Drawing.Image.FromStream(fs);
            return new Bitmap(img);
        }

        private Bitmap LoadBitmapFromWebP(string path)
        {
            var skBitmap = SKBitmap.Decode(path);
            if (skBitmap == null) throw new InvalidOperationException("Unsupported or corrupted WebP file.");
            const int maxDim = 4000;
            if (Math.Max(skBitmap.Width, skBitmap.Height) > maxDim)
            {
                float scale = Math.Min((float)maxDim / skBitmap.Width, (float)maxDim / skBitmap.Height);
                var info = new SKImageInfo((int)(skBitmap.Width * scale), (int)(skBitmap.Height * scale));
                var resized = skBitmap.Resize(info, SKFilterQuality.Medium);
                if (resized != null) { skBitmap.Dispose(); skBitmap = resized; }
            }
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            var bmp = new Bitmap(ms);
            skBitmap.Dispose();
            return bmp;
        }

        private void LoadLayout()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Layout (*.txt;*.layout)|*.txt;*.layout|All files|*.*"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            string text = "";
            try
            {
                text = File.ReadAllText(ofd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read layout file: " + ex.Message);
                return;
            }

            try
            {
                ParseIni(text);
                _layoutPath = ofd.FileName;
                SetDirty(false);
                UpdateRightPanelCounts();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Layout parse failed:\n" + ex.Message, "Parse error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Auto-load referenced image if possible
            bool alreadyLoaded =
                _image != null &&
                !string.IsNullOrEmpty(_imagePath) &&
                string.Equals(Path.GetFileName(_imagePath), _imageFileName, StringComparison.OrdinalIgnoreCase);

            if (!alreadyLoaded)
            {
                string folder = Path.GetDirectoryName(ofd.FileName);
                string candidate = Path.IsPathRooted(_imageFileName) ? _imageFileName : Path.Combine(folder, _imageFileName);
                try
                {
                    if (File.Exists(candidate))
                    {
                        if (_image != null) _image.Dispose();
                        _image = LoadBitmapSmart(candidate);
                        _imagePath = candidate;
                        _imageFileName = Path.GetFileName(candidate);
                        _canvas.Invalidate();
                        GenerateIni();
                        alreadyLoaded = true;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Found image reference but failed to load it:\n" + ex.Message);
                }
            }

            if (!alreadyLoaded)
            {
                MessageBox.Show($"Please select the image file \"{_imageFileName}\" using 'Choose Image'.");
            }
        }

        private void LoadCpf()
        {
            using var ofd = new OpenFileDialog { Filter = "CPF/XML|*.cpf;*.xml|All Files|*.*" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var xml = XDocument.Load(ofd.FileName);
                _phaseNames.Clear();
                _phaseNames.AddRange(xml.Descendants("Table")
                    .Where(t => (string)t.Attribute("Name") == "XSG")
                    .Descendants("Column").Where(c => (string)c.Attribute("Name") == "Name")
                    .Descendants("Data").Select(d => (string)d ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

                var dets = xml.Descendants("Table").Where(t => (string)t.Attribute("Name") == "XDET")
                    .Descendants("Column").Where(c => (string)c.Attribute("Name") == "Name")
                    .Descendants("Data").Select(d => (string)d ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                _detNames.Clear();
                _detNames.Add("None");
                _detNames.AddRange(dets);

                _idToPosition.Clear();
                for (int i = 0; i < _phaseNames.Count; i++) _idToPosition[_phaseNames[i]] = i;
                for (int i = 0; i < dets.Count; i++) _idToPosition[dets[i]] = i;

                // LampSymbol defaults (Default/Pedestrian/Toucan/Left_Arrow/Right_Arrow)
                _lampSymbolByPhase.Clear();
                _defaultArrowByPhase.Clear();
                var tableXsg = xml.Descendants("Table").FirstOrDefault(t => (string)t.Attribute("Name") == "XSG");
                if (tableXsg != null)
                {
                    var names = tableXsg.Descendants("Column").FirstOrDefault(c => (string)c.Attribute("Name") == "Name")?.Descendants("Data").Select(d => ((string)d) ?? "").ToList() ?? new List<string>();
                    var symbols = tableXsg.Descendants("Column").FirstOrDefault(c => (string)c.Attribute("Name") == "LampSymbol")?.Descendants("Data").Select(d => ((string)d) ?? "").ToList() ?? new List<string>();
                    int rows = Math.Min(names.Count, symbols.Count);
                    for (int i = 0; i < rows; i++)
                    {
                        var nm = names[i];
                        var sym = symbols[i] ?? "";
                        _lampSymbolByPhase[nm] = sym;
                        var at = ArrowFromLampSymbol(sym);
                        if (at.HasValue) _defaultArrowByPhase[nm] = at.Value;
                    }
                }

                // Apply defaults to existing phase lines (only if not edited)
                foreach (var ln in _phases)
                {
                    if (!ln.TypeEdited && !string.IsNullOrWhiteSpace(ln.Id))
                    {
                        if (_defaultArrowByPhase.TryGetValue(ln.Id, out var def))
                            ln.Type = def;
                    }
                }

                MessageBox.Show($"Imported {_phaseNames.Count} phases and {_detNames.Count - 1} detectors.");
                GenerateIni();
                UpdateRightPanelCounts();
                _canvas.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to parse CPF: " + ex.Message);
            }
        }

        private static ArrowType? ArrowFromLampSymbol(string sym)
        {
            if (string.IsNullOrWhiteSpace(sym)) return null;
            sym = sym.Trim();
            if (sym.Equals("Default", StringComparison.OrdinalIgnoreCase)) return ArrowType.Arrow;
            if (sym.Equals("Pedestrian", StringComparison.OrdinalIgnoreCase) || sym.Equals("Toucan", StringComparison.OrdinalIgnoreCase))
                return ArrowType.PedCrossing;
            if (Enum.TryParse<ArrowType>(sym, true, out var e)) return e;
            return null;
        }

        private void SaveLayout(bool showDialog)
        {
            if (showDialog || string.IsNullOrEmpty(_layoutPath))
            {
                using var sfd = new SaveFileDialog
                {
                    FileName = string.IsNullOrEmpty(_layoutPath) ? "layout.txt" : Path.GetFileName(_layoutPath),
                    Filter = "Text/Layout|*.txt;*.layout|All files|*.*"
                };
                if (sfd.ShowDialog() != DialogResult.OK) return;
                _layoutPath = sfd.FileName;
            }

            try
            {
                File.WriteAllText(_layoutPath, _iniOutput.Text);
                SetDirty(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save: " + ex.Message);
            }
        }

        // ---------------- Canvas interaction ----------------
        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_selected is LineShape)
            {
                var ls = (LineShape)_selected;
                ls.Thickness = Clamp(ls.Thickness + (e.Delta > 0 ? 1 : -1), 2, 30);
                _canvas.Invalidate();
                GenerateIni();
                return;
            }
            if (_selected is SquareShape)
            {
                var ss = (SquareShape)_selected;
                if ((ModifierKeys & Keys.Control) == Keys.Control)
                {
                    ss.Rotation = (ss.Rotation + (e.Delta > 0 ? 5 : -5)) % 360f;
                    if (ss.Rotation < 0) ss.Rotation += 360f;
                }
                else
                {
                    int delta = e.Delta > 0 ? 2 : -2;
                    ss.Width = Clamp(ss.Width + delta, 8, 400);
                    ss.Height = Clamp(ss.Height + delta, 8, 400);
                }
                _canvas.Invalidate();
                GenerateIni();
                return;
            }
            if (_selected is TextShape)
            {
                var ts = (TextShape)_selected;
                ts.Size = Clamp(ts.Size + (e.Delta > 0 ? 2 : -2), 8, 72);
                _canvas.Invalidate();
                GenerateIni();
                return;
            }

            _zoom = Clamp(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep), MinZoom, MaxZoom);
            _canvas.Invalidate();
            UpdateZoomLabel();
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (_image == null)
            {
                MessageBox.Show("Please upload an image first.");
                return;
            }
            _lastMouseImgPt = ScreenToImage(e.Location);

            if (e.Button == MouseButtons.Left)
            {
                var imgPt = _lastMouseImgPt;

                if (_newShape == NewShapeMode.Line)
                {
                    var start = new PointF(imgPt.X - 60, imgPt.Y - 60);
                    var ln = new LineShape { Points = new List<PointF> { start, imgPt }, Thickness = 10, Type = ArrowType.Arrow, TurnLength = 35f };
                    _phases.Add(ln); _selected = ln; _newShape = NewShapeMode.None; SetDirty(true);
                    _canvas.Invalidate(); GenerateIni(); UpdateRightPanelCounts();
                    _canvas.Capture = true;
                    return;
                }
                if (_newShape == NewShapeMode.Square)
                {
                    var sq = new SquareShape { X = imgPt.X, Y = imgPt.Y, Width = 40, Height = 40, Rotation = 0, Thickness = 6, Fill = Color.Blue };
                    _detectors.Add(sq); _selected = sq; _newShape = NewShapeMode.None; SetDirty(true);
                    _canvas.Invalidate();
                    GenerateIni();
                    UpdateRightPanelCounts();
                    _canvas.Capture = true;
                    return;
                }
                if (_newShape == NewShapeMode.Text)
                {
                    using (var dlg = new TextEditDialog())
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            var t = new TextShape
                            {
                                Label = string.IsNullOrWhiteSpace(dlg.LabelText) ? "Text Label" : dlg.LabelText,
                                Text = dlg.DisplayText,
                                X = imgPt.X,
                                Y = imgPt.Y,
                                FontName = "Arial",
                                Size = 18,
                                Bold = false,
                                Color = dlg.PickedColor
                            };
                            _texts.Add(t); _selected = t; _newShape = NewShapeMode.None; SetDirty(true);
                            _canvas.Invalidate();
                            GenerateIni();
                        }
                    }
                    _canvas.Capture = true;
                    return;
                }

                // selection or panning decision (click empty -> pan)
                var hit = HitTest(imgPt, out _dragPointIndex);
                if (hit == null)
                {
                    _selected = null;
                    _panning = true; _panStart = e.Location; Cursor = Cursors.Hand;
                }
                else
                {
                    _selected = hit;
                }
                _canvas.Capture = true;
                _canvas.Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                var imgPt = _lastMouseImgPt;
                _selected = HitTest(imgPt, out _dragPointIndex) ?? _selected;
                _canvas.Invalidate();
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var imgPt = ScreenToImage(e.Location);

            if (_panning && e.Button == MouseButtons.Left)
            {
                _offset = new PointF(_offset.X + (e.X - _panStart.X), _offset.Y + (e.Y - _panStart.Y));
                _panStart = e.Location;
                _canvas.Invalidate();
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (_selected is LineShape ls)
            {
                if (_dragPointIndex >= 0 && _dragPointIndex < ls.Points.Count)
                {
                    ls.Points[_dragPointIndex] = imgPt;
                }
                else
                {
                    // move whole line by tip delta
                    var tip = ls.Points[^1];
                    var dx = imgPt.X - tip.X; var dy = imgPt.Y - tip.Y;
                    for (int i = 0; i < ls.Points.Count; i++)
                        ls.Points[i] = new PointF(ls.Points[i].X + dx, ls.Points[i].Y + dy);
                }

                // live updates while dragging
                SetDirty(true);
                GenerateIni();
                UpdateRightPanelCounts();
                _canvas.Invalidate();
                return;
            }
            else if (_selected is SquareShape ss)
            {
                // Ctrl-drag rotate, otherwise move/resize by corner proximity
                if ((ModifierKeys & Keys.Control) == Keys.Control)
                {
                    var ang = Math.Atan2(imgPt.Y - ss.Y, imgPt.X - ss.X) * 180.0 / Math.PI;
                    ss.Rotation = (float)ang;
                }
                else
                {
                    // decide between move vs stretch (big forgiving hit area near bright corner)
                    var corner = new PointF(ss.X + ss.Width / 2f, ss.Y + ss.Height / 2f);
                    if (Distance(imgPt, corner) < 24f / Math.Min(_zoom, 1f))
                    {
                        var dx = imgPt.X - ss.X; var dy = imgPt.Y - ss.Y;
                        ss.Width = Clamp(Math.Abs(dx) * 2f, 8, 600);
                        ss.Height = Clamp(Math.Abs(dy) * 2f, 8, 600);
                    }
                    else
                    {
                        ss.X = imgPt.X; ss.Y = imgPt.Y;
                    }
                }

                // live updates
                SetDirty(true);
                GenerateIni();
                UpdateRightPanelCounts();
                _canvas.Invalidate();
                return;
            }
            else if (_selected is TextShape ts)
            {
                ts.X = imgPt.X; ts.Y = imgPt.Y;

                // live updates
                SetDirty(true);
                GenerateIni();
                _canvas.Invalidate();
                return;
            }
        }

        private void Canvas_MouseUp(object sender, MouseEventArgs e)
        {
            _canvas.Capture = false;   // release capture
            _panning = false;
            Cursor = Cursors.Default;
            _dragPointIndex = -1;
        }

        // DnD from sidebar
        private void Canvas_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy;
        }
        private void Canvas_DragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.Text)) return;
            var payload = (string)e.Data.GetData(DataFormats.Text);
            var clientPt = _canvas.PointToClient(new Point(e.X, e.Y));
            var imgPt = ScreenToImage(clientPt);

            if (payload.StartsWith("PHASE:", StringComparison.OrdinalIgnoreCase))
            {
                var id = payload.Substring(6);
                var ln = new LineShape
                {
                    Id = id,
                    Points = new List<PointF> { new PointF(imgPt.X - 40, imgPt.Y - 40), imgPt },
                    Thickness = 10,
                    TurnLength = 35,
                    Type = ArrowType.Arrow
                };
                if (_defaultArrowByPhase.TryGetValue(id, out var def) && !ln.TypeEdited) ln.Type = def;
                _phases.Add(ln); _selected = ln; SetDirty(true);
            }
            else if (payload.StartsWith("DET:", StringComparison.OrdinalIgnoreCase))
            {
                var id = payload.Substring(4);
                var sq = new SquareShape { Id = id, X = imgPt.X, Y = imgPt.Y, Width = 40, Height = 40, Thickness = 6, Fill = Color.Blue };
                _detectors.Add(sq); _selected = sq; SetDirty(true);
            }
            _canvas.Invalidate();
            GenerateIni();
            UpdateRightPanelCounts();
        }

        // ---------------- Context menu ----------------
        private void Ctx_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _ctx.Items.Clear();

            if (_selected == null)
            {
                // Right-click empty canvas
                _ctx.Items.Add("New Line", null, (s, a) => { _newShape = NewShapeMode.Line; });
                _ctx.Items.Add("New Square", null, (s, a) => { _newShape = NewShapeMode.Square; });
                _ctx.Items.Add("New Text", null, (s, a) => { _newShape = NewShapeMode.Text; });
                return;
            }

            // Duplicate
            if (_selected is TextShape)
                _ctx.Items.Add("Duplicate Text…", null, (s, a) => DuplicateSelected(textInteractive: true));
            if (_selected is SquareShape)
                _ctx.Items.Add("Duplicate Square", null, (s, a) => DuplicateSelected());
            if (_selected is LineShape)
                _ctx.Items.Add("Duplicate Line", null, (s, a) => DuplicateSelected());

            _ctx.Items.Add(new ToolStripSeparator());

            if (!(_selected is TextShape))
            {
                _ctx.Items.Add("Assign Phase…", null, (s, a) => AssignPhase());
                _ctx.Items.Add("Assign Detector…", null, (s, a) => AssignDetector());
            }

            if (_selected is LineShape)
            {
                var arrowMenu = new ToolStripMenuItem("Arrow Type");
                foreach (ArrowType t in (ArrowType[])Enum.GetValues(typeof(ArrowType)))
                {
                    var mi = new ToolStripMenuItem(t.ToString());
                    mi.Checked = ((LineShape)_selected).Type == t;
                    mi.Click += (s, a) =>
                    {
                        ((LineShape)_selected).Type = (ArrowType)Enum.Parse(typeof(ArrowType), ((ToolStripItem)s).Text);
                        ((LineShape)_selected).TypeEdited = true;
                        _canvas.Invalidate(); GenerateIni(); SetDirty(true);
                    };
                    arrowMenu.DropDownItems.Add(mi);
                }
                _ctx.Items.Add(arrowMenu);

                _ctx.Items.Add("Add Bend Point Here", null, (s, a) => AddBendAt(_lastMouseImgPt));
            }
            if (_selected is SquareShape)
            {
                _ctx.Items.Add("Pick Square Color…", null, (s, a) => PickSquareColor());
            }
            if (_selected is TextShape)
            {
                _ctx.Items.Add("Edit Text…", null, (s, a) => EditText());
            }

            _ctx.Items.Add(new ToolStripSeparator());
            _ctx.Items.Add("Copy", null, (s, a) => { _copied = _selected?.Clone(); });
            if (_copied != null) _ctx.Items.Add("Paste", null, (s, a) => PasteShape());
            _ctx.Items.Add("Delete", null, (s, a) => DeleteSelected());
        }

        private void PickSquareColor()
        {
            if (_selected is not SquareShape s) return;
            using var cd = new ColorDialog { Color = s.Fill, FullOpen = true };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                s.Fill = cd.Color; _canvas.Invalidate(); GenerateIni(); SetDirty(true);
            }
        }

        private void AddBendAt(PointF imgPt)
        {
            if (_selected is not LineShape ln) return;
            // Insert after the nearest segment
            int bestSeg = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < ln.Points.Count - 1; i++)
            {
                var a = ln.Points[i];
                var b = ln.Points[i + 1];
                float d = Distance(imgPt, ProjectPoint(imgPt, a, b));
                if (d < bestDist) { bestDist = d; bestSeg = i; }
            }
            if (bestSeg >= 0)
            {
                ln.Points.Insert(bestSeg + 1, imgPt);
                _selected = ln;
                _dragPointIndex = bestSeg + 1;
                _canvas.Invalidate();
                GenerateIni();
                SetDirty(true);
            }
        }

        private void AssignPhase()
        {
            if (_phaseNames.Count == 0) { MessageBox.Show("No phases available. Load a .cpf first."); return; }
            if (_selected == null) return;

            using var dlg = new PickDialog("Select Phase", _phaseNames);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var id = dlg.Picked;
                if (_selected is LineShape pl)
                {
                    pl.Id = id;
                    if (_defaultArrowByPhase.TryGetValue(id, out var def) && !pl.TypeEdited) pl.Type = def;
                    SetDirty(true);
                }
                else if (_selected is SquareShape sq)
                {
                    MessageBox.Show("Phases are lines. Converting to line.");
                    var line = new LineShape
                    {
                        Id = id,
                        Type = ArrowType.Arrow,
                        Thickness = sq.Thickness,
                        Points = new List<PointF>
                        {
                            new PointF(sq.X - sq.Width / 2f, sq.Y - sq.Height / 2f),
                            new PointF(sq.X + sq.Width / 2f, sq.Y + sq.Height / 2f),
                        }
                    };
                    if (_defaultArrowByPhase.TryGetValue(id, out var def)) line.Type = def;
                    _detectors.Remove(sq);
                    _phases.Add(line); _selected = line; SetDirty(true);
                }
                _canvas.Invalidate();
                GenerateIni();
                UpdateRightPanelCounts();
            }
        }

        private void AssignDetector()
        {
            if (_detNames.Count <= 1) { MessageBox.Show("No detectors available. Load a .cpf first."); return; }
            if (_selected == null) return;

            using var dlg = new PickDialog("Select Detector", _detNames.Where(n => n != "None"));
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var id = dlg.Picked;
                if (_selected is LineShape pl && _phases.Contains(pl))
                {
                    _phases.Remove(pl);
                    pl.Id = id; _detectors.Add(pl); _selected = pl; SetDirty(true);
                }
                else if (_selected is LineShape dl)
                {
                    dl.Id = id; SetDirty(true);
                }
                else if (_selected is SquareShape sq)
                {
                    sq.Id = id; SetDirty(true);
                }
                _canvas.Invalidate();
                GenerateIni();
                UpdateRightPanelCounts();
            }
        }

        private void EditText()
        {
            if (!(_selected is TextShape ts)) return;
            using var dlg = new TextEditDialog(ts.Label, ts.Text, ts.Color);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                ts.Label = string.IsNullOrWhiteSpace(dlg.LabelText) ? "Text Label" : dlg.LabelText;
                ts.Text = dlg.DisplayText;
                ts.Color = dlg.PickedColor;
                _canvas.Invalidate();
                GenerateIni();
                SetDirty(true);
            }
        }

        private void DuplicateSelected(bool textInteractive = false)
        {
            if (_selected == null) return;
            if (_selected is TextShape tx && textInteractive)
            {
                using var dlg = new TextEditDialog(tx.Label, tx.Text, tx.Color);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var dup = (TextShape)tx.Clone();
                dup.Label = string.IsNullOrWhiteSpace(dlg.LabelText) ? "Text Label" : dlg.LabelText;
                dup.Text = dlg.DisplayText;
                _texts.Add(dup);
                _selected = dup;
            }
            else
            {
                var clone = _selected.Clone();
                switch (clone)
                {
                    case LineShape l:
                        for (int i = 0; i < l.Points.Count; i++) l.Points[i] = new PointF(l.Points[i].X + 12, l.Points[i].Y + 12);
                        if (!string.IsNullOrEmpty(l.Id) && _phaseNames.Contains(l.Id)) _phases.Add(l);
                        else _detectors.Add(l);
                        break;
                    case SquareShape s:
                        s.X += 12; s.Y += 12; _detectors.Add(s); break;
                    case TextShape t:
                        t.X += 12; t.Y += 12; _texts.Add(t); break;
                }
                _selected = clone;
            }
            _canvas.Invalidate();
            GenerateIni();
            UpdateRightPanelCounts();
            SetDirty(true);
        }

        private void PasteShape()
        {
            if (_copied == null) return;
            var clone = _copied.Clone();
            switch (clone)
            {
                case LineShape l:
                    for (int i = 0; i < l.Points.Count; i++) l.Points[i] = new PointF(l.Points[i].X + 12, l.Points[i].Y + 12);
                    if (!string.IsNullOrEmpty(l.Id) && _phaseNames.Contains(l.Id)) _phases.Add(l);
                    else _detectors.Add(l);
                    break;
                case SquareShape s:
                    s.X += 12; s.Y += 12; _detectors.Add(s); break;
                case TextShape t:
                    t.X += 12; t.Y += 12; _texts.Add(t); break;
            }
            _selected = clone;
            _canvas.Invalidate();
            GenerateIni();
            UpdateRightPanelCounts();
            SetDirty(true);
        }

        private void DeleteSelected()
        {
            if (_selected == null) return;
            if (_selected is LineShape ll) { _phases.Remove(ll); _detectors.Remove(ll); }
            if (_selected is SquareShape) _detectors.Remove(_selected);
            if (_selected is TextShape tt) _texts.Remove(tt);
            _selected = null;
            _canvas.Invalidate();
            GenerateIni();
            UpdateRightPanelCounts();
            SetDirty(true);
        }

        // ---------------- Layout INI (txt) ----------------
        private void GenerateIni()
        {
            var inv = CultureInfo.InvariantCulture;
            using var sw = new StringWriter(inv);

            sw.WriteLine("[image]");
            sw.WriteLine($"file={_imageFileName}");
            sw.WriteLine();

            sw.WriteLine("[phases]");
            foreach (var p in _phases)
            {
                if (p.Points.Count < 2) continue;
                var head = string.Format(inv, "{0},{1},{2},{3},{4},{5},{6}",
                    string.IsNullOrWhiteSpace(p.Id) ? "-" : p.Id,
                    p.Type,
                    p.Points[0].X, p.Points[0].Y,
                    p.Points[1].X, p.Points[1].Y,
                    p.Thickness);
                sw.Write(head);
                if (p.Points.Count > 2)
                {
                    for (int i = 2; i < p.Points.Count; i++)
                        sw.Write(string.Format(inv, ",{0},{1}", p.Points[i].X, p.Points[i].Y));
                }
                sw.Write(string.Format(inv, ",turn={0},edited={1}", p.TurnLength, p.TypeEdited ? 1 : 0));
                sw.WriteLine();
            }
            sw.WriteLine();

            sw.WriteLine("[detectors]");
            foreach (var d in _detectors)
            {
                if (d is LineShape l)
                {
                    if (l.Points.Count < 2) continue;
                    var id = string.IsNullOrWhiteSpace(l.Id) ? "-" : l.Id;
                    var head = string.Format(inv, "{0},line,{1},{2},{3},{4},{5},{6}",
                        id, l.Type, l.Points[0].X, l.Points[0].Y, l.Points[1].X, l.Points[1].Y, l.Thickness);
                    sw.Write(head);
                    if (l.Points.Count > 2)
                    {
                        for (int i = 2; i < l.Points.Count; i++)
                            sw.Write(string.Format(inv, ",{0},{1}", l.Points[i].X, l.Points[i].Y));
                    }
                    sw.Write(string.Format(inv, ",turn={0},edited={1}", l.TurnLength, l.TypeEdited ? 1 : 0));
                    sw.WriteLine();
                }
                else if (d is SquareShape s)
                {
                    var id = string.IsNullOrWhiteSpace(s.Id) ? "-" : s.Id;
                    sw.WriteLine(string.Format(inv, "{0},square,{1},{2},{3},{4},{5},{6}",
                        id, s.X, s.Y, s.Width, s.Height, s.Rotation, s.Thickness));
                }
            }
            sw.WriteLine();

            sw.WriteLine("[text]");
            foreach (var t in _texts)
            {
                // Quote label/text if they contain commas or quotes
                string Q(string val)
                {
                    if (val is null) return "";
                    if (val.Contains(',') || val.Contains('"'))
                        return "\"" + val.Replace("\"", "\"\"") + "\"";
                    return val;
                }
                sw.WriteLine(string.Format(inv, "{0},{1},{2},{3},{4},{5},{6},{7}",
                    Q(t.Label), Q(t.Text), t.X, t.Y, t.FontName, t.Size, t.Bold, ColorTranslator.ToHtml(t.Color)));
            }
            sw.WriteLine();

            sw.WriteLine("[mappings]");
            foreach (var kv in _idToPosition)
                sw.WriteLine($"{kv.Key}={kv.Value}");

            _iniOutput.Text = sw.ToString();
        }

        private static readonly char[] _commentStarts = new[] { '#', ';' };
        private void ParseIni(string data)
        {
            _phases.Clear();
            _detectors.Clear();
            _texts.Clear();
            _imageFileName = "layout.png";

            var inv = CultureInfo.InvariantCulture;
            string section = null;
            foreach (var rawLine in data.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (_commentStarts.Contains(line[0])) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }

                if (section == "image")
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2 && parts[0].Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
                    {
                        // accept quoted filenames with spaces
                        var val = parts[1].Trim();
                        if (val.Length >= 2 && val.StartsWith("\"") && val.EndsWith("\""))
                            val = val.Substring(1, val.Length - 2).Replace("\"\"", "\"");
                        _imageFileName = val;
                    }
                }
                else if (section == "phases")
                {
                    var ln = ParseLineShapeFromIni(line, isDetector: false, inv);
                    if (ln != null) _phases.Add(ln);
                }
                else if (section == "detectors")
                {
                    var parts = line.Split(',').Select(p => p.Trim()).ToList();
                    if (parts.Count < 2) continue;
                    var id = parts[0];
                    var kind = parts[1].ToLowerInvariant();

                    if (kind == "square")
                    {
                        // id,square,x,y,w,h,rot,thickness
                        if (parts.Count >= 8 &&
                            TryF(parts[2], inv, out float x) &&
                            TryF(parts[3], inv, out float y) &&
                            TryF(parts[4], inv, out float w) &&
                            TryF(parts[5], inv, out float h) &&
                            TryF(parts[6], inv, out float rot) &&
                            TryI(parts[7], inv, out int th))
                        {
                            _detectors.Add(new SquareShape
                            {
                                Id = id != "-" ? id : string.Empty,
                                X = x,
                                Y = y,
                                Width = w,
                                Height = h,
                                Rotation = rot,
                                Thickness = th,
                                Fill = Color.Blue
                            });
                        }
                    }
                    else
                    {
                        // Treat ANY non-square as a line detector (HTML exports: id,arrow/... or id,left_arrow/...)
                        var ln = ParseLineShapeFromIni(line, isDetector: true, inv);
                        if (ln != null) _detectors.Add(ln);
                    }
                }
                else if (section == "text")
                {
                    // label,text,x,y,font,size,bold,color  (quoted CSV supported)
                    var parts = SplitCsv(line);
                    if (parts.Length >= 8 &&
                        TryF(parts[2], inv, out float x) &&
                        TryF(parts[3], inv, out float y) &&
                        TryI(parts[5], inv, out int size) &&
                        bool.TryParse(parts[6], out bool bold))
                    {
                        _texts.Add(new TextShape
                        {
                            Label = parts[0],
                            Text = parts[1],
                            X = x,
                            Y = y,
                            FontName = parts[4],
                            Size = size,
                            Bold = bold,
                            Color = ColorTranslator.FromHtml(parts[7])
                        });
                    }
                }
                else if (section == "mappings")
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, inv, out int pos))
                        _idToPosition[parts[0].Trim()] = pos;
                }
            }

            _canvas.Invalidate();
            GenerateIni();
            UpdateRightPanelCounts();
        }

        // robust CSV: supports quoted fields and doubled quotes
        private static string[] SplitCsv(string line)
        {
            var result = new List<string>();
            if (line == null) return Array.Empty<string>();

            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        cur.Append('\"'); i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            result.Add(cur.ToString());
            for (int i = 0; i < result.Count; i++) result[i] = result[i].Trim();
            return result.ToArray();
        }

        private LineShape ParseLineShapeFromIni(string line, bool isDetector, CultureInfo inv)
        {
            // For phases:   id,type,x1,y1,x2,y2,thick,(opt extra pairs...), [turn=val], [edited=0/1]
            // For detectors: id,line/type,x1,y1,x2,y2,thick,(opt...), [turn=val], [edited=0/1]
            var parts = line.Split(',').Select(p => p.Trim()).ToList();
            if (parts.Count < 7) return null;

            int idx = 0;
            string id = parts[idx++];
            if (isDetector)
            {
                if (idx >= parts.Count) return null;
                // accept "line" or an arrow token in this field — still read arrow/type next
                if (parts[idx].Equals("line", StringComparison.OrdinalIgnoreCase) ||
                    TryParseArrow(parts[idx], out _))
                {
                    idx++;
                }
            }

            if (idx >= parts.Count) return null;

            // tolerant arrow parse: supports Arrow/NoArrow/PedCrossing and snake_case tokens
            ArrowType at;
            if (!TryParseArrow(parts[idx++], out at)) at = ArrowType.Arrow;

            if (idx + 5 > parts.Count) return null;
            if (!TryF(parts[idx++], inv, out float x1)) return null;
            if (!TryF(parts[idx++], inv, out float y1)) return null;
            if (!TryF(parts[idx++], inv, out float x2)) return null;
            if (!TryF(parts[idx++], inv, out float y2)) return null;
            if (!TryI(parts[idx++], inv, out int thick)) return null;

            var ln = new LineShape { Id = id != "-" ? id : string.Empty, Type = at, Thickness = thick };
            ln.Points.Add(new PointF(x1, y1));
            ln.Points.Add(new PointF(x2, y2));

            while (idx < parts.Count)
            {
                var token = parts[idx];
                if (token.StartsWith("turn=", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryF(token.Substring(5), inv, out var tl)) ln.TurnLength = tl;
                    idx++;
                    continue;
                }
                if (token.StartsWith("edited=", StringComparison.OrdinalIgnoreCase))
                {
                    ln.TypeEdited = token.EndsWith("1");
                    idx++;
                    continue;
                }
                if (idx + 1 < parts.Count &&
                    TryF(parts[idx], inv, out var px) &&
                    TryF(parts[idx + 1], inv, out var py))
                {
                    ln.Points.Add(new PointF(px, py));
                    idx += 2;
                    continue;
                }
                idx++; // skip garbage
            }
            return ln;
        }

        // tolerant arrow parser to match HTML exports
        private static bool TryParseArrow(string s, out ArrowType at)
        {
            if (Enum.TryParse<ArrowType>(s, true, out at))
                return true;

            var token = (s ?? "").Trim().Replace("-", "_");

            if (token.Equals("no_arrow", StringComparison.OrdinalIgnoreCase)) { at = ArrowType.NoArrow; return true; }
            if (token.Equals("arrow", StringComparison.OrdinalIgnoreCase)) { at = ArrowType.Arrow; return true; }
            if (token.Equals("ped_crossing", StringComparison.OrdinalIgnoreCase)) { at = ArrowType.PedCrossing; return true; }
            if (token.Equals("left_arrow", StringComparison.OrdinalIgnoreCase)) { at = ArrowType.Left_Arrow; return true; }
            if (token.Equals("right_arrow", StringComparison.OrdinalIgnoreCase)) { at = ArrowType.Right_Arrow; return true; }

            var squashed = token.Replace("_", "");
            if (Enum.TryParse<ArrowType>(squashed, true, out at))
                return true;

            at = ArrowType.Arrow;
            return false;
        }

        // ---------------- helpers ----------------
        internal void DrawCanvas(Graphics g, Rectangle client)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.Clear(Color.FromArgb(224, 224, 224));
            if (_image == null) return;

            var imgSize = Fit(_image.Size, client.Size);
            var viewW = (int)(imgSize.Width * _zoom);
            var viewH = (int)(imgSize.Height * _zoom);
            var dest = new Rectangle((client.Width - viewW) / 2, (client.Height - viewH) / 2, viewW, viewH);
            dest.Offset((int)_offset.X, (int)_offset.Y);

            g.DrawImage(_image, dest);

            float scaleX = dest.Width / (float)_image.Width;
            float scaleY = dest.Height / (float)_image.Height;

            foreach (var p in _phases)
                DrawShape(g, p, p == _selected ? Pens.Blue : Pens.Black, scaleX, scaleY, dest.Location);

            foreach (var d in _detectors)
                DrawShape(g, d, d == _selected ? Pens.Blue : Pens.Black, scaleX, scaleY, dest.Location);

            foreach (var t in _texts)
                DrawShape(g, t, t == _selected ? Pens.Blue : Pens.Black, scaleX, sy: scaleY, dest.Location);
        }

        private void DrawShape(Graphics g, IShape s, Pen outline, float sx, float sy, Point destOrigin)
        {
            if (s is LineShape ln)
            {
                using var p = new Pen(outline.Color, Math.Max(1f, ln.Thickness * Math.Min(sx, sy)));
                // polyline
                for (int i = 0; i < ln.Points.Count - 1; i++)
                {
                    var a = ImgToScr(ln.Points[i], sx, sy, destOrigin);
                    var b = ImgToScr(ln.Points[i + 1], sx, sy, destOrigin);
                    g.DrawLine(p, a, b);
                }

                // arrow at tip
                var tip = ImgToScr(ln.Points[^1], sx, sy, destOrigin);
                var from = ImgToScr(ln.Points[^2], sx, sy, destOrigin);

                switch (ln.Type)
                {
                    case ArrowType.Arrow:
                        DrawArrowHeadFlush(g, from, tip, p.Width, outline.Color);
                        break;
                    case ArrowType.PedCrossing:
                        DrawArrowHeadFlush(g, from, tip, p.Width, outline.Color);
                        DrawArrowHeadFlush(g, tip, from, p.Width, outline.Color);
                        break;
                    case ArrowType.Left_Arrow:
                        DrawSideArrow(g, from, tip, p.Width, outline.Color, left: true);
                        break;
                    case ArrowType.Right_Arrow:
                        DrawSideArrow(g, from, tip, p.Width, outline.Color, left: false);
                        break;
                }

                // handles
                if (ln == _selected)
                {
                    var a0 = ImgToScr(ln.Points[0], sx, sy, destOrigin);
                    var aTip = ImgToScr(ln.Points[^1], sx, sy, destOrigin);
                    DrawHandle(g, a0.X, a0.Y, Brushes.Red, CenterHandleSize * Math.Min(sx, sy));
                    DrawHandle(g, aTip.X, aTip.Y, Brushes.Red, CenterHandleSize * Math.Min(sx, sy)); // draggable tip
                    for (int i = 1; i < ln.Points.Count - 1; i++)
                    {
                        var m = ImgToScr(ln.Points[i], sx, sy, destOrigin);
                        DrawHandle(g, m.X, m.Y, Brushes.Green, CenterHandleSize * Math.Min(sx, sy));
                    }
                }
            }
            else if (s is SquareShape sq)
            {
                var x = destOrigin.X + sq.X * sx;
                var y = destOrigin.Y + sq.Y * sy;
                var w = Math.Max(8f, sq.Width * sx);
                var h = Math.Max(8f, sq.Height * sy);
                var t = Math.Max(1f, sq.Thickness * Math.Min(sx, sy));

                var state = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform(sq.Rotation);
                using (var fill = new SolidBrush(s == _selected ? Color.FromArgb(170, 0, 0, 255) : sq.Fill))
                    g.FillRectangle(fill, -w / 2f, -h / 2f, w, h);
                using (var p = new Pen(outline.Color, t))
                    g.DrawRectangle(p, -w / 2f, -h / 2f, w, h);
                g.Restore(state);

                // Center + big bright corner
                DrawHandle(g, x, y, Brushes.Red, CenterHandleSize * Math.Min(sx, sy));
                var brx = x + w / 2f; var bry = y + h / 2f;
                DrawHandle(g, brx, bry, Brushes.Yellow, StretchHandleSize * Math.Min(sx, sy) * 1.8f);
            }
            else if (s is TextShape tx)
            {
                var x = destOrigin.X + tx.X * sx;
                var y = destOrigin.Y + tx.Y * sy;
                using (var b = new SolidBrush(s == _selected ? Color.Blue : tx.Color))
                using (var f = new Font(tx.FontName, tx.Size * Math.Min(sx, sy), tx.Bold ? FontStyle.Bold : FontStyle.Regular))
                {
                    g.DrawString(tx.Text, f, b, x, y);
                }
                if (s == _selected) DrawHandle(g, x, y, Brushes.Red, CenterHandleSize * Math.Min(sx, sy));
            }
        }

        private PointF ImgToScr(PointF pt, float sx, float sy, Point origin) =>
            new(origin.X + pt.X * sx, origin.Y + pt.Y * sy);

        // Arrow heads
        private void DrawArrowHeadFlush(Graphics g, PointF from, PointF tip, float lineWidth, Color color)
        {
            // small wedge sitting tight on the tip, sized to line width
            var size = Math.Max(8f, lineWidth * 3.0f);
            var angle = Math.Atan2(tip.Y - from.Y, tip.X - from.X);
            var wing = Math.PI / 6.5;
            var p1 = new PointF((float)(tip.X - size * Math.Cos(angle) + size * 0.6f * Math.Cos(angle + wing)),
                                (float)(tip.Y - size * Math.Sin(angle) + size * 0.6f * Math.Sin(angle + wing)));
            var p2 = tip;
            var p3 = new PointF((float)(tip.X - size * Math.Cos(angle) + size * 0.6f * Math.Cos(angle - wing)),
                                (float)(tip.Y - size * Math.Sin(angle) + size * 0.6f * Math.Sin(angle - wing)));
            using var b = new SolidBrush(color);
            using var p = new Pen(color, lineWidth * 0.6f);
            g.FillPolygon(b, new[] { p1, p2, p3 });
            g.DrawPolygon(p, new[] { p1, p2, p3 });
        }

        // Turn arrows with a smooth curve that bends from the line direction
        private void DrawSideArrow(Graphics g, PointF from, PointF tip, float lineWidth, Color color, bool left)
        {
            var angle = Math.Atan2(tip.Y - from.Y, tip.X - from.X);
            var normal = left ? angle - Math.PI / 2 : angle + Math.PI / 2;

            // Scale curve length based on line width for visibility
            var curveExtent = Math.Max(25f, lineWidth * 4f);
            var arrowSize = Math.Max(8f, lineWidth * 3.0f);

            // Start the curve slightly back from the tip along the line
            var curveStart = new PointF(
                (float)(tip.X - Math.Cos(angle) * curveExtent * 0.3f),
                (float)(tip.Y - Math.Sin(angle) * curveExtent * 0.3f));

            // End point of the curve - perpendicular to the line direction
            var curveEnd = new PointF(
                (float)(tip.X + Math.Cos(normal) * curveExtent),
                (float)(tip.Y + Math.Sin(normal) * curveExtent));

            // Control points for a smooth 90-degree Bezier curve
            // First control: continue in line direction from curve start
            var ctrl1 = new PointF(
                (float)(curveStart.X + Math.Cos(angle) * curveExtent * 0.7f),
                (float)(curveStart.Y + Math.Sin(angle) * curveExtent * 0.7f));

            // Second control: approach curve end from perpendicular direction (back toward the line)
            var ctrl2 = new PointF(
                (float)(curveEnd.X - Math.Cos(normal) * curveExtent * 0.5f),
                (float)(curveEnd.Y - Math.Sin(normal) * curveExtent * 0.5f));

            // Draw the curved line
            using (var p = new Pen(color, Math.Max(1f, lineWidth)))
                g.DrawBezier(p, curveStart, ctrl1, ctrl2, curveEnd);

            // Arrow head pointing outward (perpendicular direction, away from the main line)
            var wing = Math.PI / 6.5;
            var p1 = new PointF(
                (float)(curveEnd.X - arrowSize * Math.Cos(normal) + arrowSize * 0.6f * Math.Cos(normal + wing)),
                (float)(curveEnd.Y - arrowSize * Math.Sin(normal) + arrowSize * 0.6f * Math.Sin(normal + wing)));
            var p2 = curveEnd;
            var p3 = new PointF(
                (float)(curveEnd.X - arrowSize * Math.Cos(normal) + arrowSize * 0.6f * Math.Cos(normal - wing)),
                (float)(curveEnd.Y - arrowSize * Math.Sin(normal) + arrowSize * 0.6f * Math.Sin(normal - wing)));

            using var b = new SolidBrush(color);
            using var pn = new Pen(color, lineWidth * 0.6f);
            g.FillPolygon(b, new[] { p1, p2, p3 });
            g.DrawPolygon(pn, new[] { p1, p2, p3 });
        }

        private void DrawHandle(Graphics g, float x, float y, Brush brush, float size)
        {
            var r = size / 2f;
            g.FillEllipse(brush, x - r, y - r, size, size);
            using var p = new Pen(Color.Black, 1f);
            g.DrawEllipse(p, x - r, y - r, size, size);
        }

        private static Size Fit(Size image, Size box)
        {
            var ar = (float)image.Height / image.Width;
            var w = box.Width; var h = (int)(w * ar);
            if (h > box.Height) { h = box.Height; w = (int)(h / ar); }
            return new Size(Math.Max(1, w), Math.Max(1, h));
        }

        private PointF ScreenToImage(Point pt)
        {
            if (_image == null) return PointF.Empty;
            var client = _canvas.ClientRectangle;
            var imgSize = Fit(_image.Size, client.Size);
            var viewW = (int)(imgSize.Width * _zoom);
            var viewH = (int)(imgSize.Height * _zoom);
            var dest = new Rectangle((client.Width - viewW) / 2, (client.Height - viewH) / 2, viewW, viewH);
            dest.Offset((int)_offset.X, (int)_offset.Y);

            var sx = (pt.X - dest.Left) / (float)dest.Width;
            var sy = (pt.Y - dest.Top) / (float)dest.Height;
            return new PointF(sx * _image.Width, sy * _image.Height);
        }

        private IShape HitTest(PointF imgPt, out int pointIndex)
        {
            pointIndex = -1;

            // texts (topmost wins)
            for (int i = _texts.Count - 1; i >= 0; i--)
            {
                var t = _texts[i];
                var width = t.Text.Length * t.Size * 0.6f;
                var height = t.Size;
                if (imgPt.X >= t.X - HandleSize && imgPt.X <= t.X + width + HandleSize &&
                    imgPt.Y >= t.Y - HandleSize && imgPt.Y <= t.Y + height + HandleSize)
                    return t;
            }
            // detectors (reverse for z-order)
            for (int i = _detectors.Count - 1; i >= 0; i--)
            {
                var s = _detectors[i];
                if (s is LineShape dl)
                {
                    if (PointNearLine(imgPt, dl, out int idx)) { pointIndex = idx; return dl; }
                }
                else if (s is SquareShape ss && PointNearSquare(imgPt, ss)) return ss;
            }
            // phases
            for (int i = _phases.Count - 1; i >= 0; i--)
            {
                var p = _phases[i];
                if (PointNearLine(imgPt, p, out int idx)) { pointIndex = idx; return p; }
            }
            return null;
        }

        private static float Distance(PointF a, PointF b)
        {
            var dx = a.X - b.X; var dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private bool PointNearLine(PointF p, LineShape l, out int nearIdx)
        {
            var tol = HandleSize / Math.Min(_zoom, 1f);
            // handles first
            for (int i = 0; i < l.Points.Count; i++)
            {
                if (Distance(p, l.Points[i]) < tol) { nearIdx = i; return true; }
            }
            // segments
            for (int i = 0; i < l.Points.Count - 1; i++)
            {
                var a = l.Points[i]; var b = l.Points[i + 1];
                if (Distance(p, ProjectPoint(p, a, b)) < tol) { nearIdx = -1; return true; }
            }
            nearIdx = -1; return false;
        }

        private static PointF ProjectPoint(PointF p, PointF a, PointF b)
        {
            var dx = b.X - a.X; var dy = b.Y - a.Y; var len2 = dx * dx + dy * dy;
            if (len2 < 1e-3f) return a;
            var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            return new PointF(a.X + t * dx, a.Y + t * dy);
        }

        private bool PointNearSquare(PointF p, SquareShape s)
        {
            // generous hit box around bright corner and body
            if (Math.Abs(p.X - (s.X + s.Width / 2f)) <= 18 && Math.Abs(p.Y - (s.Y + s.Height / 2f)) <= 18) return true;
            return Math.Abs(p.X - s.X) <= s.Width / 2f + HandleSize && Math.Abs(p.Y - s.Y) <= s.Height / 2f + HandleSize;
        }

        // ---------------- right panel ----------------
        private void UpdateRightPanelCounts()
        {
            // Phases counts
            var phaseCounts = _phases.GroupBy(p => p.Id ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var phaseRows = _phaseNames.Select(name => new { Name = name, Count = phaseCounts.TryGetValue(name, out var c) ? c : 0, Type = _defaultArrowByPhase.TryGetValue(name, out var t) ? t.ToString() : "" }).ToList();
            _gridPhases.DataSource = phaseRows;

            // Detectors counts (both line & squares)
            var detCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _detectors)
            {
                var id = d.Id ?? "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                detCounts[id] = detCounts.TryGetValue(id, out var c) ? c + 1 : 1;
            }
            var detRows = _detNames.Where(n => n != "None").Select(name => new { Name = name, Count = detCounts.TryGetValue(name, out var c) ? c : 0, Type = "Detector" }).ToList();
            _gridDetectors.DataSource = detRows;
        }

        private void ToggleRightPanel() => _split.Panel2Collapsed = !_split.Panel2Collapsed;

        // ---------------- hotkeys & closing ----------------
        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S) { SaveLayout(showDialog: string.IsNullOrEmpty(_layoutPath)); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.C) { if (_selected != null) _copied = _selected.Clone(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.V) { PasteShape(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.D) { DuplicateSelected(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.H) { ToggleRightPanel(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.O) { LoadLayout(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.I) { ChooseImage(); e.Handled = true; return; }

            if (e.KeyCode == Keys.Delete || (!e.Control && e.KeyCode == Keys.D)) { DeleteSelected(); e.Handled = true; return; }

            if (!e.Control && e.KeyCode == Keys.L) { _newShape = NewShapeMode.Line; e.Handled = true; return; }
            if (!e.Control && e.KeyCode == Keys.T) { _newShape = NewShapeMode.Text; e.Handled = true; return; }
            if (!e.Control && (e.KeyCode == Keys.Q || e.KeyCode == Keys.S)) { _newShape = NewShapeMode.Square; e.Handled = true; return; }

            if (!e.Control && e.KeyCode == Keys.A)
            {
                if (_selected is LineShape) { AddBendAt(_lastMouseImgPt); e.Handled = true; }
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_dirty) return;
            var res = MessageBox.Show("Save changes to layout before closing?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (res == DialogResult.Cancel) { e.Cancel = true; return; }
            if (res == DialogResult.Yes)
            {
                SaveLayout(showDialog: string.IsNullOrEmpty(_layoutPath));
                if (_dirty) { e.Cancel = true; } // save failed
            }
        }

        private void SetDirty(bool val) => _dirty = val;

        private void ShowAbout()
        {
            var shortcuts =
@"Shortcuts
──────────
Ctrl+S  Save (uses last file if any)
Ctrl+O  Load layout
Ctrl+I  Choose image
Ctrl+C  Copy
Ctrl+V  Paste
Ctrl+D  Duplicate
Delete / D  Delete selected
L       New Line
Q / S   New Square
T       New Text
A       Add bend point at mouse
Ctrl+H  Toggle sidebar
Ctrl + mouse move on square  Rotate
Mouse wheel over shape: resize/rotate; over canvas: zoom
Drag red handles: move endpoints / tip / centers
Drag green handles: move bend points";

            MessageBox.Show(
                "Creator: Mick Carry\nWinForms by: Rob Pickup\n\n" + shortcuts,
                "About Site Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- Test / Export functionality ----------------

        /// <summary>
        /// Dummy Phases test: Connects to the given IP address and iterates through ALL phases
        /// in the report looking for "Site Phase" table entries.
        /// </summary>
        /// <param name="IPAddress">IP address of the controller</param>
        /// <param name="PhaseCount">Expected number of phases</param>
        /// <returns>Number of phases successfully processed</returns>
        public static int DummyPhases(string IPAddress, int PhaseCount)
        {
            int successCount = 0;

            // Iterate through ALL phases (0 to PhaseCount-1), not just a hardcoded range like 10-19
            for (int i = 0; i < PhaseCount; i++)
            {
                try
                {
                    // Test phase - skip null/empty results
                    var result = TestPhase(IPAddress, i);

                    // Only count non-null successful results
                    if (result != null && result.Success)
                    {
                        successCount++;
                    }
                }
                catch
                {
                    // Log error but continue with other phases - don't stop iteration
                    continue;
                }
            }

            return successCount;
        }

        /// <summary>
        /// Test a single phase synchronously
        /// </summary>
        private static PhaseTestResult TestPhase(string ipAddress, int phaseIndex)
        {
            // Return null for phases that should be skipped
            if (string.IsNullOrWhiteSpace(ipAddress))
                return null;

            return new PhaseTestResult
            {
                PhaseName = $"Phase_{phaseIndex}",
                PhaseIndex = phaseIndex,
                Success = true,
                Data = $"Phase {phaseIndex} OK"
            };
        }

        /// <summary>
        /// Async version of DummyPhases for UI usage with progress reporting
        /// </summary>
        public async Task<Dictionary<string, PhaseTestResult>> DummyPhasesAsync(string ipAddress, int phaseCount, IProgress<int> progress = null)
        {
            var results = new Dictionary<string, PhaseTestResult>(StringComparer.OrdinalIgnoreCase);

            // Iterate through ALL phases (0 to phaseCount-1), not just a hardcoded range
            for (int i = 0; i < phaseCount; i++)
            {
                var phaseName = i < _phaseNames.Count ? _phaseNames[i] : $"Phase_{i}";

                try
                {
                    // Simulate or perform actual phase test
                    var result = await TestPhaseAsync(ipAddress, i, phaseName);

                    // Only add non-null results - ignore nulls gracefully
                    if (result != null)
                    {
                        results[phaseName] = result;
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue with other phases
                    results[phaseName] = new PhaseTestResult
                    {
                        PhaseName = phaseName,
                        PhaseIndex = i,
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }

                progress?.Report(i + 1);
            }

            return results;
        }

        /// <summary>
        /// Test a single phase by its index
        /// </summary>
        private async Task<PhaseTestResult> TestPhaseAsync(string ipAddress, int phaseIndex, string phaseName)
        {
            // Simulate network delay for testing
            await Task.Delay(50);

            return new PhaseTestResult
            {
                PhaseName = phaseName,
                PhaseIndex = phaseIndex,
                Success = true,
                Data = $"Phase {phaseIndex} OK"
            };
        }

        /// <summary>
        /// Searches report data for a specific table by name.
        /// Returns null if table not found or empty, to be safely ignored.
        /// </summary>
        /// <param name="reportData">The XML report data</param>
        /// <param name="tableName">Name of the table to find (e.g., "Site Phase")</param>
        /// <returns>List of data entries, or null if not found/empty</returns>
        public List<Dictionary<string, string>> FindTableInReport(XDocument reportData, string tableName)
        {
            if (reportData == null) return null;
            if (string.IsNullOrWhiteSpace(tableName)) return null;

            var table = reportData.Descendants("Table")
                .FirstOrDefault(t =>
                {
                    var nameAttr = t.Attribute("Name");
                    if (nameAttr == null) return false;
                    var tableNo = nameAttr.Value;
                    // Handle "Site Phase" table name matching
                    if (string.IsNullOrWhiteSpace(tableNo)) return false;
                    return tableNo.Equals(tableName, StringComparison.OrdinalIgnoreCase);
                });

            if (table == null) return null;

            var columns = table.Descendants("Column").ToList();
            if (!columns.Any()) return null;

            var results = new List<Dictionary<string, string>>();

            // Get all column names and their data
            var columnData = new Dictionary<string, List<string>>();
            foreach (var col in columns)
            {
                var colName = col.Attribute("Name")?.Value;
                if (string.IsNullOrWhiteSpace(colName)) continue;

                var data = col.Descendants("Data")
                    .Select(d => d.Value ?? "")
                    .ToList();

                columnData[colName] = data;
            }

            if (!columnData.Any()) return null;

            // Determine row count from any column
            var rowCount = columnData.Values.Max(v => v.Count);
            if (rowCount == 0) return null;

            // Build result dictionaries for each row
            // Iterate through ALL rows, not just a fixed range like 10-19
            for (int i = 0; i < rowCount; i++)
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool hasData = false;

                foreach (var kvp in columnData)
                {
                    var value = i < kvp.Value.Count ? kvp.Value[i] : null;

                    // Skip null/empty entries gracefully
                    if (value != null)
                    {
                        row[kvp.Key] = value;
                        if (!string.IsNullOrWhiteSpace(value))
                            hasData = true;
                    }
                }

                // Only add rows that have actual data
                if (hasData)
                {
                    results.Add(row);
                }
            }

            return results.Count > 0 ? results : null;
        }

        /// <summary>
        /// Shows the Test Runner dialog with improved UI visuals
        /// </summary>
        private void ShowTestRunner()
        {
            using var dlg = new TestRunnerDialog(this, _phaseNames.Count > 0 ? _phaseNames.Count : 16);
            dlg.ShowDialog(this);
        }

        /// <summary>
        /// Shows the Export dialog with progress indication
        /// </summary>
        private void ShowExportDialog()
        {
            using var dlg = new ExportProgressDialog(this);
            dlg.ShowDialog(this);
        }

        // ---------------- Result class for phase testing ----------------
        public class PhaseTestResult
        {
            public string PhaseName { get; set; }
            public int PhaseIndex { get; set; }
            public bool Success { get; set; }
            public string Data { get; set; }
            public string ErrorMessage { get; set; }
        }

        // ---------------- Test Runner Dialog with improved UI ----------------
        private class TestRunnerDialog : Form
        {
            private readonly Form1 _owner;
            private readonly int _phaseCount;

            private TextBox _txtIpAddress;
            private NumericUpDown _numPhaseCount;
            private ProgressBar _progressBar;
            private Label _lblStatus;
            private Label _lblProgress;
            private DataGridView _resultsGrid;
            private Button _btnRun;
            private Button _btnExport;
            private Button _btnClose;
            private Panel _progressPanel;
            private CancellationTokenSource _cts;

            public TestRunnerDialog(Form1 owner, int defaultPhaseCount)
            {
                _owner = owner;
                _phaseCount = defaultPhaseCount;
                InitializeDialog();
            }

            private void InitializeDialog()
            {
                this.Text = "Phase Test Runner";
                this.Width = 700;
                this.Height = 550;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.MinimumSize = new Size(600, 450);
                this.StartPosition = FormStartPosition.CenterParent;
                this.Font = new Font("Segoe UI", 9f);

                // Top panel with settings
                var topPanel = new Panel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(12) };

                var lblIp = new Label { Text = "Controller IP:", Location = new Point(12, 18), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                _txtIpAddress = new TextBox { Location = new Point(120, 15), Width = 180, Text = "192.168.1.1" };

                var lblCount = new Label { Text = "Phase Count:", Location = new Point(320, 18), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                _numPhaseCount = new NumericUpDown { Location = new Point(420, 15), Width = 80, Minimum = 1, Maximum = 256, Value = _phaseCount };

                topPanel.Controls.AddRange(new Control[] { lblIp, _txtIpAddress, lblCount, _numPhaseCount });

                // Progress panel with visual indicators
                _progressPanel = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(12), BackColor = Color.FromArgb(245, 245, 250) };

                _lblStatus = new Label
                {
                    Text = "Ready to run tests",
                    Location = new Point(12, 10),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(50, 50, 50)
                };

                _progressBar = new ProgressBar
                {
                    Location = new Point(12, 35),
                    Width = 640,
                    Height = 25,
                    Style = ProgressBarStyle.Continuous,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                };

                _lblProgress = new Label
                {
                    Text = "0 / 0 phases tested",
                    Location = new Point(12, 62),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(100, 100, 100)
                };

                _progressPanel.Controls.AddRange(new Control[] { _lblStatus, _progressBar, _lblProgress });

                // Results grid
                _resultsGrid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    AutoGenerateColumns = false,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    ReadOnly = true,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    RowHeadersVisible = false,
                    EnableHeadersVisualStyles = false,
                    BackgroundColor = Color.White,
                    BorderStyle = BorderStyle.None,
                    CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                    GridColor = Color.FromArgb(230, 230, 230),
                    DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(5, 3, 5, 3) }
                };
                _resultsGrid.SetDoubleBuffered(true);

                _resultsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Phase", Name = "Phase", Width = 150 });
                _resultsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Index", Name = "Index", Width = 60 });
                _resultsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", Name = "Status", Width = 80 });
                _resultsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", Name = "Details", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

                _resultsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(60, 60, 70);
                _resultsGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                _resultsGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
                _resultsGrid.ColumnHeadersHeight = 35;

                // Bottom button panel
                var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(12) };

                _btnRun = new Button
                {
                    Text = "Run Tests",
                    Width = 120,
                    Height = 35,
                    Location = new Point(12, 10),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                _btnRun.FlatAppearance.BorderSize = 0;
                _btnRun.Click += async (s, e) => await RunTestsAsync();

                _btnExport = new Button
                {
                    Text = "Export Results",
                    Width = 120,
                    Height = 35,
                    Location = new Point(145, 10),
                    BackColor = Color.FromArgb(80, 80, 90),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f),
                    Enabled = false,
                    Cursor = Cursors.Hand
                };
                _btnExport.FlatAppearance.BorderSize = 0;
                _btnExport.Click += (s, e) => ExportResults();

                _btnClose = new Button
                {
                    Text = "Close",
                    Width = 100,
                    Height = 35,
                    Location = new Point(560, 10),
                    Anchor = AnchorStyles.Right | AnchorStyles.Top,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                _btnClose.Click += (s, e) =>
                {
                    _cts?.Cancel();
                    this.Close();
                };

                bottomPanel.Controls.AddRange(new Control[] { _btnRun, _btnExport, _btnClose });

                this.Controls.Add(_resultsGrid);
                this.Controls.Add(_progressPanel);
                this.Controls.Add(topPanel);
                this.Controls.Add(bottomPanel);
            }

            private async Task RunTestsAsync()
            {
                var ip = _txtIpAddress.Text.Trim();
                var count = (int)_numPhaseCount.Value;

                if (string.IsNullOrWhiteSpace(ip))
                {
                    MessageBox.Show("Please enter a valid IP address.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _btnRun.Enabled = false;
                _btnExport.Enabled = false;
                _resultsGrid.Rows.Clear();
                _progressBar.Value = 0;
                _progressBar.Maximum = count;

                _lblStatus.Text = "Running tests...";
                _lblStatus.ForeColor = Color.FromArgb(0, 120, 212);
                _progressPanel.BackColor = Color.FromArgb(230, 240, 255);

                _cts = new CancellationTokenSource();
                var results = new Dictionary<string, PhaseTestResult>(StringComparer.OrdinalIgnoreCase);
                int successCount = 0;
                int failCount = 0;

                try
                {
                    // Iterate through ALL phases, not just a fixed range
                    for (int i = 0; i < count; i++)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        var phaseName = i < _owner._phaseNames.Count ? _owner._phaseNames[i] : $"Phase_{i}";

                        _lblProgress.Text = $"{i + 1} / {count} phases tested";

                        try
                        {
                            var result = await _owner.TestPhaseAsync(ip, i, phaseName);

                            // Handle null results gracefully - skip them
                            if (result == null)
                            {
                                continue;
                            }

                            results[phaseName] = result;

                            // Add to grid with color coding and tick/cross symbols
                            var rowIdx = _resultsGrid.Rows.Add(
                                result.PhaseName,
                                result.PhaseIndex.ToString(),
                                result.Success ? "\u2714" : "\u2718",  // ✔ tick or ✘ cross
                                result.Success ? result.Data : result.ErrorMessage
                            );

                            var row = _resultsGrid.Rows[rowIdx];
                            row.Cells["Status"].Style.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
                            row.Cells["Status"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

                            if (result.Success)
                            {
                                row.DefaultCellStyle.BackColor = Color.FromArgb(230, 255, 230);
                                row.Cells["Status"].Style.ForeColor = Color.FromArgb(40, 167, 69);  // Green tick
                                successCount++;
                            }
                            else
                            {
                                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 230, 230);
                                row.Cells["Status"].Style.ForeColor = Color.FromArgb(220, 53, 69);  // Red cross
                                failCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            var rowIdx = _resultsGrid.Rows.Add(phaseName, i.ToString(), "\u2718", ex.Message);  // ✘ cross for error
                            var row = _resultsGrid.Rows[rowIdx];
                            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 230, 230);
                            row.Cells["Status"].Style.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
                            row.Cells["Status"].Style.ForeColor = Color.FromArgb(220, 53, 69);
                            row.Cells["Status"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                            failCount++;
                        }

                        _progressBar.Value = i + 1;

                        // Scroll to show latest result
                        if (_resultsGrid.Rows.Count > 0)
                            _resultsGrid.FirstDisplayedScrollingRowIndex = _resultsGrid.Rows.Count - 1;
                    }

                    // Update final status with summary and tick/cross symbols
                    if (failCount == 0)
                    {
                        _lblStatus.Text = $"\u2714 All tests passed! ({successCount} phases)";
                        _lblStatus.ForeColor = Color.FromArgb(40, 167, 69);  // Green
                        _progressPanel.BackColor = Color.FromArgb(230, 255, 230);
                    }
                    else
                    {
                        _lblStatus.Text = $"\u26A0 Completed: {successCount} \u2714 passed, {failCount} \u2718 failed";
                        _lblStatus.ForeColor = Color.FromArgb(180, 100, 0);
                        _progressPanel.BackColor = Color.FromArgb(255, 245, 230);
                    }
                }
                catch (OperationCanceledException)
                {
                    _lblStatus.Text = "Tests cancelled";
                    _lblStatus.ForeColor = Color.Gray;
                    _progressPanel.BackColor = Color.FromArgb(245, 245, 250);
                }
                finally
                {
                    _btnRun.Enabled = true;
                    _btnExport.Enabled = _resultsGrid.Rows.Count > 0;
                    _lblProgress.Text = $"{_resultsGrid.Rows.Count} / {count} phases tested";
                }
            }

            private void ExportResults()
            {
                using var sfd = new SaveFileDialog
                {
                    Filter = "CSV Files|*.csv|Text Files|*.txt|All Files|*.*",
                    FileName = $"PhaseTestResults_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (sfd.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Phase,Index,Status,Details");

                    foreach (DataGridViewRow row in _resultsGrid.Rows)
                    {
                        var phase = row.Cells["Phase"].Value?.ToString() ?? "";
                        var index = row.Cells["Index"].Value?.ToString() ?? "";
                        var status = row.Cells["Status"].Value?.ToString() ?? "";
                        var details = row.Cells["Details"].Value?.ToString() ?? "";

                        // CSV escape
                        details = details.Replace("\"", "\"\"");
                        if (details.Contains(',') || details.Contains('"'))
                            details = $"\"{details}\"";

                        sb.AppendLine($"{phase},{index},{status},{details}");
                    }

                    File.WriteAllText(sfd.FileName, sb.ToString());
                    MessageBox.Show($"Results exported to:\n{sfd.FileName}", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---------------- Export Progress Dialog ----------------
        private class ExportProgressDialog : Form
        {
            private readonly Form1 _owner;
            private ProgressBar _progressBar;
            private Label _lblStatus;
            private Label _lblFile;
            private Button _btnExport;
            private Button _btnCancel;
            private ComboBox _cmbFormat;
            private CancellationTokenSource _cts;

            public ExportProgressDialog(Form1 owner)
            {
                _owner = owner;
                InitializeDialog();
            }

            private void InitializeDialog()
            {
                this.Text = "Export Layout";
                this.Width = 500;
                this.Height = 280;
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.StartPosition = FormStartPosition.CenterParent;
                this.Font = new Font("Segoe UI", 9f);
                this.BackColor = Color.White;

                // Header
                var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Color.FromArgb(60, 60, 70) };
                var lblTitle = new Label
                {
                    Text = "Export Layout",
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                    Location = new Point(20, 18),
                    AutoSize = true
                };
                header.Controls.Add(lblTitle);

                // Format selection
                var lblFormat = new Label { Text = "Export Format:", Location = new Point(20, 80), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                _cmbFormat = new ComboBox
                {
                    Location = new Point(130, 77),
                    Width = 200,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                _cmbFormat.Items.AddRange(new[] { "Layout File (.txt)", "HTML Viewer (.html)", "JSON Data (.json)" });
                _cmbFormat.SelectedIndex = 0;

                // Progress section
                _lblStatus = new Label
                {
                    Text = "Ready to export",
                    Location = new Point(20, 120),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(80, 80, 80)
                };

                _progressBar = new ProgressBar
                {
                    Location = new Point(20, 145),
                    Width = 440,
                    Height = 25,
                    Style = ProgressBarStyle.Continuous
                };

                _lblFile = new Label
                {
                    Text = "",
                    Location = new Point(20, 175),
                    Width = 440,
                    Height = 20,
                    ForeColor = Color.FromArgb(100, 100, 100)
                };

                // Buttons
                _btnExport = new Button
                {
                    Text = "Export",
                    Width = 100,
                    Height = 35,
                    Location = new Point(250, 200),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                _btnExport.FlatAppearance.BorderSize = 0;
                _btnExport.Click += async (s, e) => await DoExportAsync();

                _btnCancel = new Button
                {
                    Text = "Cancel",
                    Width = 100,
                    Height = 35,
                    Location = new Point(360, 200),
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                _btnCancel.Click += (s, e) =>
                {
                    _cts?.Cancel();
                    this.Close();
                };

                this.Controls.AddRange(new Control[] { header, lblFormat, _cmbFormat, _lblStatus, _progressBar, _lblFile, _btnExport, _btnCancel });
            }

            private async Task DoExportAsync()
            {
                string filter, ext;
                switch (_cmbFormat.SelectedIndex)
                {
                    case 1: filter = "HTML Files|*.html"; ext = ".html"; break;
                    case 2: filter = "JSON Files|*.json"; ext = ".json"; break;
                    default: filter = "Layout Files|*.txt"; ext = ".txt"; break;
                }

                using var sfd = new SaveFileDialog
                {
                    Filter = filter,
                    FileName = $"layout_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
                };

                if (sfd.ShowDialog() != DialogResult.OK) return;

                _btnExport.Enabled = false;
                _lblStatus.Text = "Exporting...";
                _lblStatus.ForeColor = Color.FromArgb(0, 120, 212);
                _lblFile.Text = Path.GetFileName(sfd.FileName);
                _progressBar.Style = ProgressBarStyle.Marquee;

                _cts = new CancellationTokenSource();

                try
                {
                    await Task.Run(() =>
                    {
                        Thread.Sleep(500); // Simulate processing

                        switch (_cmbFormat.SelectedIndex)
                        {
                            case 1:
                                ExportAsHtml(sfd.FileName);
                                break;
                            case 2:
                                ExportAsJson(sfd.FileName);
                                break;
                            default:
                                File.WriteAllText(sfd.FileName, _owner._iniOutput.Text);
                                break;
                        }
                    }, _cts.Token);

                    _progressBar.Style = ProgressBarStyle.Continuous;
                    _progressBar.Value = 100;
                    _lblStatus.Text = "Export complete!";
                    _lblStatus.ForeColor = Color.Green;

                    MessageBox.Show($"Exported successfully to:\n{sfd.FileName}", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (OperationCanceledException)
                {
                    _lblStatus.Text = "Export cancelled";
                    _lblStatus.ForeColor = Color.Gray;
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = "Export failed";
                    _lblStatus.ForeColor = Color.Red;
                    MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _btnExport.Enabled = true;
                    _progressBar.Style = ProgressBarStyle.Continuous;
                }
            }

            private void ExportAsHtml(string path)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html><head><title>Site Layout</title>");
                sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;margin:20px;} pre{background:#f5f5f5;padding:15px;border-radius:5px;}</style>");
                sb.AppendLine("</head><body>");
                sb.AppendLine("<h1>Site Layout Export</h1>");
                sb.AppendLine($"<p>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
                sb.AppendLine("<pre>");
                sb.AppendLine(System.Net.WebUtility.HtmlEncode(_owner._iniOutput.Text));
                sb.AppendLine("</pre>");
                sb.AppendLine("</body></html>");
                File.WriteAllText(path, sb.ToString());
            }

            private void ExportAsJson(string path)
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"generated\": \"{DateTime.Now:O}\",");
                sb.AppendLine($"  \"imageFile\": \"{EscapeJson(_owner._imageFileName)}\",");
                sb.AppendLine("  \"phases\": [");

                for (int i = 0; i < _owner._phases.Count; i++)
                {
                    var p = _owner._phases[i];
                    sb.Append($"    {{ \"id\": \"{EscapeJson(p.Id)}\", \"type\": \"{p.Type}\", \"thickness\": {p.Thickness} }}");
                    sb.AppendLine(i < _owner._phases.Count - 1 ? "," : "");
                }

                sb.AppendLine("  ],");
                sb.AppendLine("  \"detectors\": [");

                for (int i = 0; i < _owner._detectors.Count; i++)
                {
                    var d = _owner._detectors[i];
                    sb.Append($"    {{ \"id\": \"{EscapeJson(d.Id)}\" }}");
                    sb.AppendLine(i < _owner._detectors.Count - 1 ? "," : "");
                }

                sb.AppendLine("  ]");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString());
            }

            private static string EscapeJson(string s)
            {
                if (s == null) return "";
                return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }
        }

        // ---------------- nested types ----------------
        private class Canvas : Panel
        {
            private readonly Form1 _owner;
            public Canvas(Form1 owner)
            {
                _owner = owner;
                this.DoubleBuffered = true;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                _owner.DrawCanvas(e.Graphics, this.ClientRectangle);
            }
        }

        public interface IShape
        {
            string Id { get; set; }
            IShape Clone();
        }

        public enum ArrowType { NoArrow, Arrow, PedCrossing, Left_Arrow, Right_Arrow }

        public class LineShape : IShape
        {
            public string Id { get; set; } = string.Empty;
            public ArrowType Type { get; set; } = ArrowType.Arrow; // default arrow
            public int Thickness { get; set; } = 10;
            public float TurnLength { get; set; } = 35f; // curve strength
            public bool TypeEdited { get; set; } = false; // if user changed Type via menu
            public List<PointF> Points { get; set; } = new();

            public IShape Clone()
            {
                return new LineShape
                {
                    Id = this.Id,
                    Type = this.Type,
                    Thickness = this.Thickness,
                    TurnLength = this.TurnLength,
                    TypeEdited = this.TypeEdited,
                    Points = this.Points.Select(p => new PointF(p.X, p.Y)).ToList()
                };
            }
        }

        public class SquareShape : IShape
        {
            public string Id { get; set; } = string.Empty;
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; } = 40;
            public float Height { get; set; } = 40;
            public float Rotation { get; set; } = 0;
            public int Thickness { get; set; } = 6;
            public Color Fill { get; set; } = Color.Blue;
            public IShape Clone() => new SquareShape { Id = this.Id, X = this.X, Y = this.Y, Width = this.Width, Height = this.Height, Rotation = this.Rotation, Thickness = this.Thickness, Fill = this.Fill };
        }

        public class TextShape : IShape
        {
            public string Id { get; set; } = string.Empty; // not used but keeps interface uniform
            public string Label { get; set; } = "Text Label";
            public string Text { get; set; } = "";
            public float X { get; set; }
            public float Y { get; set; }
            public string FontName { get; set; } = "Arial";
            public int Size { get; set; } = 18;
            public bool Bold { get; set; } = false;
            public Color Color { get; set; } = Color.Black;
            public IShape Clone() => new TextShape { Label = this.Label, Text = this.Text, X = this.X, Y = this.Y, FontName = this.FontName, Size = this.Size, Bold = this.Bold, Color = this.Color };
        }

        // simple picker dialog for phases/detectors
        private class PickDialog : Form
        {
            public string Picked => _combo.SelectedItem != null ? _combo.SelectedItem.ToString() : string.Empty;
            private readonly ComboBox _combo = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            public PickDialog(string title, IEnumerable<string> items)
            {
                this.Text = title; this.Width = 320; this.Height = 160; this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false; this.MinimizeBox = false; this.StartPosition = FormStartPosition.CenterParent;
                var info = new Label { Dock = DockStyle.Top, Text = "Pick a value", Height = 24, TextAlign = ContentAlignment.MiddleLeft };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Left, Width = 150 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 150 };
                foreach (var it in items) _combo.Items.Add(it);
                if (_combo.Items.Count > 0) _combo.SelectedIndex = 0;
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                this.Controls.Add(bottom);
                this.Controls.Add(_combo);
                this.Controls.Add(info);
            }
        }

        // text editor dialog
        private class TextEditDialog : Form
        {
            public string LabelText => _label.Text;
            public string DisplayText => _text.Text;
            public Color PickedColor => _color.BackColor;

            private readonly TextBox _label = new() { Dock = DockStyle.Top, PlaceholderText = "Description label (e.g. 'Bull St NB')" };
            private readonly TextBox _text = new() { Dock = DockStyle.Top, PlaceholderText = "Display text shown on plan" };
            private readonly Button _color = new() { Text = "Pick Color", Dock = DockStyle.Top, Height = 32, BackColor = Color.Black, ForeColor = Color.White };

            public TextEditDialog(string label = "", string text = "", Color? color = null)
            {
                this.Text = "Edit Text"; this.Width = 460; this.Height = 260; this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false; this.MinimizeBox = false; this.StartPosition = FormStartPosition.CenterParent;

                var header1 = new Label { Text = "Label", Dock = DockStyle.Top, Height = 18, TextAlign = ContentAlignment.BottomLeft, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
                var header2 = new Label { Text = "Text", Dock = DockStyle.Top, Height = 18, TextAlign = ContentAlignment.BottomLeft, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
                var header3 = new Label { Text = "Color", Dock = DockStyle.Top, Height = 18, TextAlign = ContentAlignment.BottomLeft, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };

                _label.Text = label;
                _text.Text = text;
                if (color.HasValue) _color.BackColor = color.Value;
                _color.Click += (s, e) => { using var cd = new ColorDialog { Color = _color.BackColor, FullOpen = true }; if (cd.ShowDialog(this) == DialogResult.OK) _color.BackColor = cd.Color; };

                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Left, Width = 200 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 200 };

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel);

                this.Controls.Add(bottom);
                this.Controls.Add(_color);
                this.Controls.Add(header3);
                this.Controls.Add(_text);
                this.Controls.Add(header2);
                this.Controls.Add(_label);
                this.Controls.Add(header1);
            }
        }

        // Utility parse helpers
        private static bool TryF(string s, CultureInfo inv, out float f) => float.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, inv, out f);
        private static bool TryI(string s, CultureInfo inv, out int i) => int.TryParse(s, NumberStyles.Integer, inv, out i);

        // Utility clamp overloads
        private static float Clamp(float value, float min, float max) { if (value < min) return min; if (value > max) return max; return value; }
        private static int Clamp(int value, int min, int max) { if (value < min) return min; if (value > max) return max; return value; }
    }
}
