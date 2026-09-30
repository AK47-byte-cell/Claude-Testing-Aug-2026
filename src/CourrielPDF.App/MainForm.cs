using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CourrielPDF.App.Rendering;
using CourrielPDF.Core;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Naming;
using CourrielPDF.Core.Rendering;

namespace CourrielPDF.App
{
    internal sealed class MainForm : Form
    {
        private sealed class Batch
        {
            public Batch(List<string> files, string? outputFolder, string? tempFolderToDelete, ConversionMode mode)
            {
                Files = files;
                OutputFolder = outputFolder;
                TempFolderToDelete = tempFolderToDelete;
                Mode = mode;
            }

            public List<string> Files { get; }
            /// <summary>Destination folder; null means "beside each source file" (PDF mode only).</summary>
            public string? OutputFolder { get; }
            public string? TempFolderToDelete { get; }
            public ConversionMode Mode { get; }
        }

        private const string DropTitle = "Glissez-déposez ici vos courriels";
        private const string DropSubtitlePdf = "Fichiers .msg ou .eml, dossiers complets, ou messages glissés directement depuis Outlook : chaque courriel devient un PDF enregistré à côté du fichier d'origine";

        private readonly AppSettings _settings;
        private readonly Queue<Batch> _queue = new Queue<Batch>();
        private readonly List<string> _recentProblems = new List<string>();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loadingSettings;
        private string? _browserPath;
        private string? _webView2Version;
        private string _engineSummary = "Prêt.";
        private bool _engineMissing;

        private DropZonePanel _dropZone = null!;
        private RadioButton _rbPdf = null!;
        private RadioButton _rbAttachments = null!;
        private TableLayoutPanel _pdfOptions = null!;
        private TableLayoutPanel _attachmentOptions = null!;
        private ComboBox _namingCombo = null!;
        private CheckBox _chkAppendPdf = null!;
        private CheckBox _chkSaveOthers = null!;
        private CheckBox _chkEmbedded = null!;
        private CheckBox _chkSeparators = null!;
        private CheckBox _chkNumbers = null!;
        private CheckBox _chkRemote = null!;
        private CheckBox _chkProvenance = null!;
        private CheckBox _chkOpenFolder = null!;
        private TextBox _txtAttachmentsFolder = null!;
        private Button _btnBrowseFolder = null!;
        private Button _btnOpenFolder = null!;
        private CheckBox _chkInlineImages = null!;
        private Button _btnFiles = null!;
        private Button _btnOutlook = null!;
        private Button _btnCancel = null!;
        private Button _btnClear = null!;
        private Button _btnDiagnostics = null!;
        private ListView _log = null!;
        private ProgressBar _progress = null!;
        private Label _status = null!;

        public MainForm()
        {
            _settings = AppSettings.Load();
            BuildUi();
            LoadSettingsIntoUi();
            Shown += (s, e) => DetectBrowser();
            FormClosing += (s, e) => SaveSettings();
        }

        private ConversionMode CurrentMode => _rbAttachments.Checked ? ConversionMode.AttachmentsOnly : ConversionMode.ConvertToPdf;

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            Text = "CourrielPDF — Conversion de courriels en PDF";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 680);
            Size = new Size(900, 780);
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10, 10, 10, 6) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            _dropZone = new DropZonePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            _dropZone.DragEnter += OnDragEnter;
            _dropZone.DragOver += OnDragEnter;
            _dropZone.DragLeave += (s, e) => _dropZone.Active = false;
            _dropZone.DragDrop += OnDragDrop;
            _dropZone.Click += (s, e) => ChooseFiles();
            root.Controls.Add(_dropZone, 0, 0);

            // ---- Mode ----
            var modeGroup = new GroupBox { Text = "Que faire des courriels déposés ?", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 6, 10, 8) };
            var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _rbPdf = new RadioButton { Text = "Convertir en PDF (les pièces jointes PDF sont ajoutées à la suite du courriel, les autres enregistrées dans un sous-dossier)", AutoSize = true, Margin = new Padding(0, 2, 0, 2), Checked = true };
            _rbAttachments = new RadioButton { Text = "Enregistrer seulement les pièces jointes, sans créer de PDF (tous les fichiers vont dans un même dossier)", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            _rbPdf.CheckedChanged += (s, e) => { if (_rbPdf.Checked) OnModeChanged(); };
            _rbAttachments.CheckedChanged += (s, e) => { if (_rbAttachments.Checked) OnModeChanged(); };
            modeFlow.Controls.Add(_rbPdf);
            modeFlow.Controls.Add(_rbAttachments);
            modeGroup.Controls.Add(modeFlow);
            root.Controls.Add(modeGroup, 0, 1);

            // ---- Options ----
            var options = new GroupBox { Text = "Options", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 6, 10, 8) };
            var optRoot = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
            optRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            optRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            optRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _pdfOptions = BuildPdfOptions();
            _attachmentOptions = BuildAttachmentOptions();
            var common = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0) };
            _chkOpenFolder = MakeCheck("Ouvrir le dossier une fois terminé");
            common.Controls.Add(_chkOpenFolder);
            optRoot.Controls.Add(_pdfOptions, 0, 0);
            optRoot.Controls.Add(_attachmentOptions, 0, 1);
            optRoot.Controls.Add(common, 0, 2);
            options.Controls.Add(optRoot);
            root.Controls.Add(options, 0, 2);

            // ---- Actions ----
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
            _btnFiles = MakeButton("Choisir des fichiers…", (s, e) => ChooseFiles());
            _btnOutlook = MakeButton("Convertir la sélection Outlook…", (s, e) => ProcessOutlookSelection(null));
            _btnCancel = MakeButton("Annuler", (s, e) => _cts?.Cancel());
            _btnCancel.Enabled = false;
            _btnClear = MakeButton("Vider la liste", (s, e) => _log.Items.Clear());
            _btnDiagnostics = MakeButton("Diagnostic…", (s, e) => { using (var d = new DiagnosticsForm(_recentProblems)) d.ShowDialog(this); });
            actions.Controls.AddRange(new Control[] { _btnFiles, _btnOutlook, _btnCancel, _btnClear, _btnDiagnostics });
            root.Controls.Add(actions, 0, 3);

            // ---- Results ----
            _log = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false, HideSelection = false, Margin = new Padding(0, 0, 0, 8) };
            _log.Columns.Add("Courriel", 300);
            _log.Columns.Add("Résultat", 480);
            _log.Resize += (s, e) => ResizeLogColumns();
            _log.DoubleClick += (s, e) => OpenSelected();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Ouvrir (PDF ou dossier)", null, (s, e) => OpenSelected());
            menu.Items.Add("Afficher dans le dossier", null, (s, e) => RevealSelected());
            menu.Items.Add("Copier le message", null, (s, e) => CopySelected());
            _log.ContextMenuStrip = menu;
            root.Controls.Add(_log, 0, 4);

            // ---- Status ----
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status = new Label { Text = "Prêt.", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
            _progress = new ProgressBar { Width = 260, Height = 18, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0), Visible = false };
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(_progress, 1, 0);
            root.Controls.Add(bottom, 0, 5);

            // The whole window accepts drops, not only the dashed zone.
            WireDropTarget(this);
        }

        private TableLayoutPanel BuildPdfOptions()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lbl = new Label { Text = "Nom des fichiers PDF :", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4) };
            _namingCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) };
            _namingCombo.Items.AddRange(new object[]
            {
                "Même nom que le fichier du courriel (Objet.msg → Objet.pdf)",
                "Date et objet (2026-09-02 14h35 - Objet.pdf)",
                "Date, expéditeur et objet (2026-09-02 14h35 - Expéditeur - Objet.pdf)",
            });
            layout.Controls.Add(lbl, 0, 0);
            layout.Controls.Add(_namingCombo, 1, 0);

            var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
            _chkAppendPdf = MakeCheck("Joindre les pièces jointes PDF à la suite du courriel");
            _chkSaveOthers = MakeCheck("Enregistrer les autres pièces jointes dans un sous-dossier");
            _chkEmbedded = MakeCheck("Convertir aussi les courriels joints");
            _chkSeparators = MakeCheck("Page de séparation avant chaque pièce jointe");
            _chkNumbers = MakeCheck("Numéroter les pages");
            _chkRemote = MakeCheck("Télécharger les images externes (Internet)");
            _chkProvenance = MakeCheck("Indiquer le fichier source et la date de conversion");
            checks.Controls.AddRange(new Control[] { _chkAppendPdf, _chkSaveOthers, _chkEmbedded, _chkSeparators, _chkNumbers, _chkRemote, _chkProvenance });
            layout.Controls.Add(checks, 0, 1);
            layout.SetColumnSpan(checks, 2);
            return layout;
        }

        private TableLayoutPanel BuildAttachmentOptions()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 3, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lbl = new Label { Text = "Dossier de destination :", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4) };
            _txtAttachmentsFolder = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 5, 8, 4) };
            _txtAttachmentsFolder.TextChanged += (s, e) => { if (!_loadingSettings) UpdateDropZoneText(); };
            _btnBrowseFolder = MakeButton("Parcourir…", (s, e) => BrowseAttachmentsFolder());
            _btnBrowseFolder.Margin = new Padding(0, 3, 6, 3);
            _btnOpenFolder = MakeButton("Ouvrir", (s, e) => { var f = ResolveAttachmentsFolderOrReport(); if (f != null) TryOpen(f); });
            _btnOpenFolder.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(lbl, 0, 0);
            layout.Controls.Add(_txtAttachmentsFolder, 1, 0);
            layout.Controls.Add(_btnBrowseFolder, 2, 0);
            layout.Controls.Add(_btnOpenFolder, 3, 0);

            _chkInlineImages = MakeCheck("Inclure aussi les images intégrées au texte (logos de signature, images collées)");
            _chkInlineImages.Margin = new Padding(0, 4, 0, 2);
            layout.Controls.Add(_chkInlineImages, 0, 1);
            layout.SetColumnSpan(_chkInlineImages, 4);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(95, 105, 120),
                Margin = new Padding(0, 2, 0, 2),
                Text = "Tous les fichiers vont ensemble dans ce dossier, sous leur nom d'origine (numérotés en cas de doublon)." + Environment.NewLine +
                       "Les courriels joints sont enregistrés (.msg ou .eml) et leurs propres pièces jointes sont aussi extraites." + Environment.NewLine +
                       "Les logos de signature et petites images collées dans le texte sont ignorés, sauf si l'option est cochée.",
            };
            layout.Controls.Add(hint, 0, 2);
            layout.SetColumnSpan(hint, 4);
            return layout;
        }

        private void WireDropTarget(Control control)
        {
            if (control != _dropZone && !(control is TextBox) && !(control is ComboBox))
            {
                control.AllowDrop = true;
                control.DragEnter += OnDragEnter;
                control.DragOver += OnDragEnter;
                control.DragLeave += (s, e) => _dropZone.Active = false;
                control.DragDrop += OnDragDrop;
            }
            foreach (Control child in control.Controls)
                WireDropTarget(child);
        }

        private static CheckBox MakeCheck(string text) => new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 2, 18, 2) };

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0), MinimumSize = new Size(0, 30) };
            b.Click += onClick;
            return b;
        }

        private void ResizeLogColumns()
        {
            if (_log.Columns.Count < 2) return;
            var w = Math.Max(200, _log.ClientSize.Width - 4);
            _log.Columns[0].Width = (int)(w * 0.38);
            _log.Columns[1].Width = w - _log.Columns[0].Width;
        }

        // ------------------------------------------------------------------
        // Settings and mode
        // ------------------------------------------------------------------

        private void LoadSettingsIntoUi()
        {
            _loadingSettings = true;
            try
            {
                if (_settings.Mode == ConversionMode.AttachmentsOnly) _rbAttachments.Checked = true;
                else _rbPdf.Checked = true;
                _namingCombo.SelectedIndex = Math.Max(0, Math.Min(2, (int)_settings.Naming));
                _chkAppendPdf.Checked = _settings.AppendPdfAttachments;
                _chkSaveOthers.Checked = _settings.SaveOtherAttachments;
                _chkEmbedded.Checked = _settings.ConvertEmbeddedMessages;
                _chkSeparators.Checked = _settings.SeparatorPages;
                _chkNumbers.Checked = _settings.NumberPages;
                _chkRemote.Checked = _settings.AllowRemoteImages;
                _chkProvenance.Checked = _settings.IncludeProvenance;
                _chkOpenFolder.Checked = _settings.OpenFolderAfter;
                _txtAttachmentsFolder.Text = _settings.ResolvedAttachmentsFolder;
                _chkInlineImages.Checked = _settings.AttachmentsIncludeInlineImages;
                if (_settings.WindowWidth >= MinimumSize.Width && _settings.WindowHeight >= MinimumSize.Height)
                    Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
            }
            finally
            {
                _loadingSettings = false;
            }
            ApplyModeToUi();
        }

        private void SaveSettings()
        {
            _settings.Mode = CurrentMode;
            _settings.Naming = (NamingPattern)Math.Max(0, _namingCombo.SelectedIndex);
            _settings.AppendPdfAttachments = _chkAppendPdf.Checked;
            _settings.SaveOtherAttachments = _chkSaveOthers.Checked;
            _settings.ConvertEmbeddedMessages = _chkEmbedded.Checked;
            _settings.SeparatorPages = _chkSeparators.Checked;
            _settings.NumberPages = _chkNumbers.Checked;
            _settings.AllowRemoteImages = _chkRemote.Checked;
            _settings.IncludeProvenance = _chkProvenance.Checked;
            _settings.OpenFolderAfter = _chkOpenFolder.Checked;
            var folder = _txtAttachmentsFolder.Text.Trim();
            // Keep following the system's Downloads folder unless the user picked something else.
            _settings.AttachmentsFolder = string.Equals(folder, KnownFolders.Downloads, StringComparison.OrdinalIgnoreCase) ? string.Empty : folder;
            _settings.AttachmentsIncludeInlineImages = _chkInlineImages.Checked;
            if (WindowState == FormWindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.Save();
        }

        private void OnModeChanged()
        {
            if (_loadingSettings) return;
            ApplyModeToUi();
            SaveSettings();
        }

        private void ApplyModeToUi()
        {
            var attachments = CurrentMode == ConversionMode.AttachmentsOnly;
            _pdfOptions.Visible = !attachments;
            _attachmentOptions.Visible = attachments;
            _btnOutlook.Text = attachments ? "Extraire les pièces jointes de la sélection Outlook…" : "Convertir la sélection Outlook…";
            UpdateDropZoneText();
            UpdateReadyStatus();
        }

        private void UpdateDropZoneText()
        {
            if (_busy)
            {
                _dropZone.Title = "Traitement en cours…";
                _dropZone.Subtitle = "Vous pouvez déposer d'autres courriels; ils seront traités à la suite.";
            }
            else if (CurrentMode == ConversionMode.AttachmentsOnly)
            {
                var folder = _txtAttachmentsFolder.Text.Trim();
                if (folder.Length == 0) folder = KnownFolders.Downloads;
                if (folder.Length > 70) folder = folder.Substring(0, 30) + "…" + folder.Substring(folder.Length - 36);
                _dropZone.Title = DropTitle;
                _dropZone.Subtitle = "Pièces jointes seulement : les fichiers seront enregistrés dans " + folder;
            }
            else
            {
                _dropZone.Title = DropTitle;
                _dropZone.Subtitle = DropSubtitlePdf;
            }
            _dropZone.Invalidate();
        }

        private void UpdateReadyStatus()
        {
            if (_busy) return;
            if (CurrentMode == ConversionMode.AttachmentsOnly)
            {
                _status.ForeColor = SystemColors.ControlText;
                _status.Text = "Prêt. Mode : pièces jointes seulement (aucun moteur de rendu requis).";
            }
            else
            {
                _status.ForeColor = _engineMissing ? Color.Firebrick : SystemColors.ControlText;
                _status.Text = _engineSummary;
            }
        }

        private void DetectBrowser()
        {
            _browserPath = BrowserLocator.Find();
            _webView2Version = WebView2Bootstrap.GetRuntimeVersion(out var webView2Error);

            if (_webView2Version != null)
            {
                _engineSummary = "Prêt. Moteur de rendu : WebView2 (Microsoft Edge " + _webView2Version + ")" + (_browserPath != null ? ", avec Edge en arrière-plan en secours." : ".");
            }
            else if (_browserPath != null)
            {
                _engineSummary = "Prêt. Moteur de rendu : Microsoft Edge en arrière-plan (WebView2 indisponible).";
                AddLog("Configuration", "Le composant WebView2 n'est pas utilisable (" + webView2Error + "). Microsoft Edge sera lancé en arrière-plan à la place.", true, Color.FromArgb(150, 90, 0));
            }
            else
            {
                _engineMissing = true;
                _engineSummary = "Aucun moteur de rendu : la conversion en PDF ne pourra pas fonctionner (l'enregistrement des pièces jointes reste possible).";
                AddLog("Configuration", "Ni le composant WebView2 (" + webView2Error + ") ni Microsoft Edge ne sont disponibles sur cet ordinateur. Installez Microsoft Edge pour permettre la conversion en PDF.", true);
            }
            UpdateReadyStatus();
        }

        private IPdfRenderer CreateRenderer(ConversionOptions options)
        {
            var host = this;
            return new FallbackPdfRenderer(
                () => _webView2Version != null ? new WebView2PdfRenderer(host, options.AllowRemoteImages) { Timeout = options.RenderTimeout } : null,
                () => new ChromiumPdfRenderer(BrowserLocator.Find(_browserPath) ?? throw new BrowserNotFoundException()) { Timeout = options.RenderTimeout, AllowNetwork = options.AllowRemoteImages },
                message => AddLog("Moteur", message, true, Color.FromArgb(150, 90, 0)));
        }

        private void BrowseAttachmentsFolder()
        {
            var current = _txtAttachmentsFolder.Text.Trim();
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Choisissez le dossier où enregistrer les pièces jointes.",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(current) ? current : KnownFolders.Downloads,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;
                _txtAttachmentsFolder.Text = dlg.SelectedPath;
                SaveSettings();
            }
        }

        /// <summary>Destination folder for attachments-only mode, created if needed; null (after a log entry) when unusable.</summary>
        private string? ResolveAttachmentsFolderOrReport()
        {
            var folder = _txtAttachmentsFolder.Text.Trim();
            if (folder.Length == 0)
            {
                folder = KnownFolders.Downloads;
                _txtAttachmentsFolder.Text = folder;
            }
            try
            {
                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception ex)
            {
                AddLog("Dossier", "Le dossier de destination « " + folder + " » est invalide ou inaccessible : " + ex.Message, true);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Drag and drop
        // ------------------------------------------------------------------

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data == null) { e.Effect = DragDropEffects.None; return; }
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || OutlookBridge.IsOutlookData(e.Data))
            {
                e.Effect = DragDropEffects.Copy;
                _dropZone.Active = true;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            _dropZone.Active = false;
            if (e.Data == null) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0) return;
                // Leave the drag-and-drop operation first, then process.
                BeginInvoke(new Action(() => HandleDroppedPaths(paths)));
            }
            else if (OutlookBridge.IsOutlookData(e.Data))
            {
                var names = OutlookBridge.ReadDraggedNames(e.Data);
                BeginInvoke(new Action(() => ProcessOutlookSelection(names)));
            }
        }

        private void HandleDroppedPaths(string[] paths)
        {
            var files = InputExpander.Expand(paths);
            var ignored = paths.Where(p => File.Exists(p) && !CourrielPDF.Core.Reading.EmailLoader.IsSupported(p)).ToList();
            foreach (var p in ignored)
                AddLog(Path.GetFileName(p), "Ignoré : seuls les fichiers .msg et .eml sont pris en charge.", true);
            if (files.Count == 0)
            {
                if (ignored.Count == 0)
                    AddLog("Dépôt", "Aucun courriel (.msg ou .eml) trouvé dans les éléments déposés.", true);
                return;
            }

            var mode = CurrentMode;
            string? outputFolder = null;
            if (mode == ConversionMode.AttachmentsOnly)
            {
                outputFolder = ResolveAttachmentsFolderOrReport();
                if (outputFolder == null) return;
            }
            Enqueue(new Batch(files, outputFolder, null, mode));
        }

        private void ChooseFiles()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Choisir les courriels à traiter",
                Filter = "Courriels (*.msg;*.eml)|*.msg;*.eml|Tous les fichiers (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                HandleDroppedPaths(dlg.FileNames);
            }
        }

        // ------------------------------------------------------------------
        // Outlook
        // ------------------------------------------------------------------

        private async void ProcessOutlookSelection(List<string>? draggedNames)
        {
            // Give Outlook a moment to finish its own drag operation before we call into it.
            await Task.Delay(250);

            var mode = CurrentMode;
            string folder;
            if (mode == ConversionMode.AttachmentsOnly)
            {
                var resolved = ResolveAttachmentsFolderOrReport();
                if (resolved == null) return;
                folder = resolved;
            }
            else
            {
                using (var dlg = new FolderBrowserDialog
                {
                    Description = "Choisissez le dossier où enregistrer les PDF des courriels sélectionnés dans Outlook.",
                    ShowNewFolderButton = true,
                    SelectedPath = Directory.Exists(_settings.LastOutputFolder) ? _settings.LastOutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;
                    folder = dlg.SelectedPath;
                }
                _settings.LastOutputFolder = folder;
                _settings.Save();
            }

            var temp = Path.Combine(Path.GetTempPath(), ProductInfo.Name, "outlook-" + Guid.NewGuid().ToString("N"));
            var warnings = new List<string>();
            List<string> saved;
            UseWaitCursor = true;
            _status.Text = "Lecture de la sélection dans Outlook…";
            try
            {
                saved = OutlookBridge.SaveSelectionAsMsg(temp, warnings);
            }
            catch (OutlookUnavailableException ex)
            {
                UseWaitCursor = false;
                UpdateReadyStatus();
                MessageBox.Show(this, ex.Message, "Outlook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                UpdateReadyStatus();
                MessageBox.Show(this, "La communication avec Outlook a échoué : " + ex.Message, "Outlook", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            UseWaitCursor = false;

            foreach (var w in warnings) AddLog("Outlook", w, true);
            if (saved.Count == 0)
            {
                UpdateReadyStatus();
                AddLog("Outlook", "Aucun courriel sélectionné dans Outlook. Sélectionnez un ou plusieurs messages dans la liste, puis réessayez.", true);
                TryDeleteDirectory(temp);
                return;
            }
            if (draggedNames != null && draggedNames.Count > 0 && draggedNames.Count != saved.Count)
                AddLog("Outlook", saved.Count + " élément(s) lus dans la sélection Outlook alors que " + draggedNames.Count + " élément(s) ont été glissés. Vérifiez le résultat.", true);

            Enqueue(new Batch(saved, folder, temp, mode));
        }

        // ------------------------------------------------------------------
        // Processing
        // ------------------------------------------------------------------

        private void Enqueue(Batch batch)
        {
            _queue.Enqueue(batch);
            if (!_busy) _ = RunQueueAsync();
            else _status.Text = "Ajouté à la file d'attente (" + _queue.Sum(b => b.Files.Count) + " courriel(s) en attente).";
        }

        private async Task RunQueueAsync()
        {
            _busy = true;
            _cts = new CancellationTokenSource();
            SetBusyUi(true);
            var pdfOk = 0;
            var extractedOk = 0;
            var totalAttachments = 0;
            var totalFailed = 0;
            var outputFolders = new List<string>();
            var cancelled = false;

            try
            {
                while (_queue.Count > 0 && !_cts.IsCancellationRequested)
                {
                    var batch = _queue.Dequeue();
                    SaveSettings();
                    var options = _settings.ToConversionOptions();
                    options.BrowserPath = _browserPath;
                    options.RendererFactory = () => CreateRenderer(options);
                    var extracting = batch.Mode == ConversionMode.AttachmentsOnly;

                    _progress.Visible = true;
                    _progress.Minimum = 0;
                    _progress.Maximum = batch.Files.Count;
                    _progress.Value = 0;

                    try
                    {
                        using (var service = new ConversionService(options))
                        {
                            for (var i = 0; i < batch.Files.Count; i++)
                            {
                                if (_cts.IsCancellationRequested) { cancelled = true; break; }
                                var file = batch.Files[i];
                                _status.ForeColor = SystemColors.ControlText;
                                _status.Text = (extracting ? "Extraction " : "Conversion ") + (i + 1) + " de " + batch.Files.Count + " : " + Path.GetFileName(file);
                                var ct = _cts.Token;
                                var result = extracting
                                    ? await Task.Run(() => service.ExtractAttachments(file, batch.OutputFolder!, ct), ct).ConfigureAwait(true)
                                    : await Task.Run(() => service.ConvertFile(file, batch.OutputFolder, ct), ct).ConfigureAwait(true);
                                ReportResult(result, batch.OutputFolder);
                                if (result.Success)
                                {
                                    if (extracting)
                                    {
                                        extractedOk++;
                                        totalAttachments += result.SavedAttachmentPaths.Count;
                                        AddFolder(outputFolders, batch.OutputFolder);
                                    }
                                    else
                                    {
                                        pdfOk++;
                                        AddFolder(outputFolders, Path.GetDirectoryName(result.OutputPdfPath!));
                                    }
                                }
                                else if (result.Cancelled) cancelled = true;
                                else totalFailed++;
                                _progress.Value = Math.Min(_progress.Maximum, i + 1);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                    catch (Exception ex)
                    {
                        totalFailed++;
                        AddLog("Traitement", "Erreur imprévue : " + ex.Message, true);
                    }
                    finally
                    {
                        if (batch.TempFolderToDelete != null) TryDeleteDirectory(batch.TempFolderToDelete);
                    }
                }
            }
            finally
            {
                if (cancelled) _queue.Clear();
                _busy = false;
                SetBusyUi(false);
                _progress.Visible = false;
                _cts.Dispose();
                _cts = null;
            }

            var parts = new List<string>();
            if (pdfOk > 0) parts.Add(pdfOk + " courriel(s) converti(s) en PDF");
            if (extractedOk > 0) parts.Add(totalAttachments + " pièce(s) jointe(s) enregistrée(s) à partir de " + extractedOk + " courriel(s)");
            if (totalFailed > 0) parts.Add(totalFailed + " erreur(s)");
            if (parts.Count == 0) parts.Add("aucun courriel traité");
            _status.ForeColor = totalFailed > 0 ? Color.Firebrick : SystemColors.ControlText;
            _status.Text = (cancelled ? "Annulé. " : "Terminé. ") + string.Join(", ", parts) + ".";

            if (_chkOpenFolder.Checked && !cancelled && outputFolders.Count > 0)
            {
                foreach (var f in outputFolders.Take(3))
                    TryOpen(f);
            }
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (!folders.Contains(folder!, StringComparer.OrdinalIgnoreCase)) folders.Add(folder!);
        }

        private void SetBusyUi(bool busy)
        {
            _btnCancel.Enabled = busy;
            _btnFiles.Enabled = true;
            _btnOutlook.Enabled = !busy;
            _rbPdf.Enabled = !busy;
            _rbAttachments.Enabled = !busy;
            _namingCombo.Enabled = !busy;
            foreach (var c in new[] { _chkAppendPdf, _chkSaveOthers, _chkEmbedded, _chkSeparators, _chkNumbers, _chkRemote, _chkProvenance, _chkInlineImages })
                c.Enabled = !busy;
            _txtAttachmentsFolder.Enabled = !busy;
            _btnBrowseFolder.Enabled = !busy;
            UpdateDropZoneText();
        }

        private void ReportResult(ConversionResult r, string? outputFolder)
        {
            var source = Path.GetFileName(r.Source);
            if (r.Success)
            {
                if (r.Mode == ConversionMode.AttachmentsOnly)
                {
                    string text;
                    if (r.SavedAttachmentPaths.Count == 0)
                    {
                        text = "Aucune pièce jointe à enregistrer" + (r.SkippedInlineImages > 0 ? " (" + r.SkippedInlineImages + " image(s) intégrée(s) au texte ignorée(s))" : string.Empty) + ".";
                    }
                    else
                    {
                        var names = r.SavedAttachmentPaths.Select(Path.GetFileName).ToList();
                        var shown = string.Join(", ", names.Take(4));
                        if (names.Count > 4) shown += " et " + (names.Count - 4) + " autre(s)";
                        text = names.Count + " pièce(s) jointe(s) enregistrée(s) : " + shown;
                        if (r.SkippedInlineImages > 0) text += " · " + r.SkippedInlineImages + " image(s) intégrée(s) ignorée(s)";
                    }
                    var item = AddLog(source, text, false);
                    item.Tag = outputFolder;
                }
                else
                {
                    var text = "PDF créé : " + Path.GetFileName(r.OutputPdfPath) + " (" + r.PageCount + (r.PageCount == 1 ? " page" : " pages") + ")";
                    if (r.AppendedAttachments.Count > 0) text += " · " + r.AppendedAttachments.Count + " pièce(s) jointe(s) fusionnée(s)";
                    if (r.SavedAttachmentPaths.Count > 0) text += " · " + r.SavedAttachmentPaths.Count + " pièce(s) jointe(s) enregistrée(s) dans un sous-dossier";
                    if (!string.IsNullOrEmpty(r.Engine)) text += " · " + r.Engine;
                    var item = AddLog(source, text, false);
                    item.Tag = r.OutputPdfPath;
                }
            }
            else
            {
                AddLog(source, "Erreur : " + r.Error, true);
            }
            foreach (var w in r.Warnings)
                AddLog(source, "Avertissement : " + w, true, Color.FromArgb(150, 90, 0));
        }

        private ListViewItem AddLog(string source, string text, bool isProblem, Color? color = null)
        {
            if (InvokeRequired)
            {
                ListViewItem? created = null;
                Invoke(new Action(() => created = AddLog(source, text, isProblem, color)));
                return created!;
            }
            var item = new ListViewItem(new[] { source, text });
            if (isProblem)
            {
                item.ForeColor = color ?? Color.Firebrick;
                _recentProblems.Add(source + " — " + text);
                if (_recentProblems.Count > 20) _recentProblems.RemoveAt(0);
            }
            _log.Items.Add(item);
            item.EnsureVisible();
            return item;
        }

        // ------------------------------------------------------------------
        // Log actions
        // ------------------------------------------------------------------

        private string? SelectedPath()
        {
            if (_log.SelectedItems.Count == 0) return null;
            return _log.SelectedItems[0].Tag as string;
        }

        private void OpenSelected()
        {
            var path = SelectedPath();
            if (path == null) return;
            if (Directory.Exists(path) || File.Exists(path)) TryOpen(path);
        }

        private void RevealSelected()
        {
            var path = SelectedPath();
            if (path == null) return;
            if (Directory.Exists(path)) { TryOpen(path); return; }
            if (!File.Exists(path)) return;
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        private void CopySelected()
        {
            if (_log.SelectedItems.Count == 0) return;
            try { Clipboard.SetText(_log.SelectedItems[0].SubItems[0].Text + " — " + _log.SelectedItems[0].SubItems[1].Text); } catch { }
        }

        private static void TryOpen(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
