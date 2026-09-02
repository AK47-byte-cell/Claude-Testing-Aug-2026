using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
            public Batch(List<string> files, string? outputFolder, string? tempFolderToDelete)
            {
                Files = files;
                OutputFolder = outputFolder;
                TempFolderToDelete = tempFolderToDelete;
            }

            public List<string> Files { get; }
            public string? OutputFolder { get; }
            public string? TempFolderToDelete { get; }
        }

        private readonly AppSettings _settings;
        private readonly Queue<Batch> _queue = new Queue<Batch>();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private string? _browserPath;

        private DropZonePanel _dropZone = null!;
        private ComboBox _namingCombo = null!;
        private CheckBox _chkAppendPdf = null!;
        private CheckBox _chkSaveOthers = null!;
        private CheckBox _chkEmbedded = null!;
        private CheckBox _chkSeparators = null!;
        private CheckBox _chkNumbers = null!;
        private CheckBox _chkRemote = null!;
        private CheckBox _chkProvenance = null!;
        private CheckBox _chkOpenFolder = null!;
        private Button _btnFiles = null!;
        private Button _btnOutlook = null!;
        private Button _btnCancel = null!;
        private Button _btnClear = null!;
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

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            Text = "CourrielPDF — Conversion de courriels en PDF";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 600);
            Size = new Size(860, 680);
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10, 10, 10, 6) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            _dropZone = new DropZonePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            _dropZone.DragEnter += OnDragEnter;
            _dropZone.DragOver += OnDragEnter;
            _dropZone.DragLeave += (s, e) => _dropZone.Active = false;
            _dropZone.DragDrop += OnDragDrop;
            _dropZone.Click += (s, e) => ChooseFiles();
            root.Controls.Add(_dropZone, 0, 0);


            var options = new GroupBox { Text = "Options", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 6, 10, 8) };
            var optLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
            optLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            optLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var lbl = new Label { Text = "Nom des fichiers PDF :", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4) };
            _namingCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) };
            _namingCombo.Items.AddRange(new object[]
            {
                "Même nom que le fichier du courriel (Objet.msg → Objet.pdf)",
                "Date et objet (2026-09-02 14h35 - Objet.pdf)",
                "Date, expéditeur et objet (2026-09-02 14h35 - Expéditeur - Objet.pdf)",
            });
            optLayout.Controls.Add(lbl, 0, 0);
            optLayout.Controls.Add(_namingCombo, 1, 0);

            var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
            _chkAppendPdf = MakeCheck("Joindre les pièces jointes PDF à la suite du courriel");
            _chkSaveOthers = MakeCheck("Enregistrer les autres pièces jointes dans un sous-dossier");
            _chkEmbedded = MakeCheck("Convertir aussi les courriels joints");
            _chkSeparators = MakeCheck("Page de séparation avant chaque pièce jointe");
            _chkNumbers = MakeCheck("Numéroter les pages");
            _chkRemote = MakeCheck("Télécharger les images externes (Internet)");
            _chkProvenance = MakeCheck("Indiquer le fichier source et la date de conversion");
            _chkOpenFolder = MakeCheck("Ouvrir le dossier une fois terminé");
            checks.Controls.AddRange(new Control[] { _chkAppendPdf, _chkSaveOthers, _chkEmbedded, _chkSeparators, _chkNumbers, _chkRemote, _chkProvenance, _chkOpenFolder });
            optLayout.Controls.Add(checks, 0, 1);
            optLayout.SetColumnSpan(checks, 2);
            options.Controls.Add(optLayout);
            root.Controls.Add(options, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
            _btnFiles = MakeButton("Choisir des fichiers…", (s, e) => ChooseFiles());
            _btnOutlook = MakeButton("Convertir la sélection Outlook…", (s, e) => ConvertOutlookSelection(null));
            _btnCancel = MakeButton("Annuler", (s, e) => _cts?.Cancel());
            _btnCancel.Enabled = false;
            _btnClear = MakeButton("Vider la liste", (s, e) => _log.Items.Clear());
            actions.Controls.AddRange(new Control[] { _btnFiles, _btnOutlook, _btnCancel, _btnClear });
            root.Controls.Add(actions, 0, 2);

            _log = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false, HideSelection = false, Margin = new Padding(0, 0, 0, 8) };
            _log.Columns.Add("Courriel", 300);
            _log.Columns.Add("Résultat", 480);
            _log.Resize += (s, e) => ResizeLogColumns();
            _log.DoubleClick += (s, e) => OpenSelectedPdf();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Ouvrir le PDF", null, (s, e) => OpenSelectedPdf());
            menu.Items.Add("Afficher dans le dossier", null, (s, e) => RevealSelected());
            menu.Items.Add("Copier le message", null, (s, e) => CopySelected());
            _log.ContextMenuStrip = menu;
            root.Controls.Add(_log, 0, 3);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status = new Label { Text = "Prêt.", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
            _progress = new ProgressBar { Width = 260, Height = 18, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0), Visible = false };
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(_progress, 1, 0);
            root.Controls.Add(bottom, 0, 4);

            // The whole window accepts drops, not only the dashed zone.
            WireDropTarget(this);
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

        private void LoadSettingsIntoUi()
        {
            _namingCombo.SelectedIndex = Math.Max(0, Math.Min(2, (int)_settings.Naming));
            _chkAppendPdf.Checked = _settings.AppendPdfAttachments;
            _chkSaveOthers.Checked = _settings.SaveOtherAttachments;
            _chkEmbedded.Checked = _settings.ConvertEmbeddedMessages;
            _chkSeparators.Checked = _settings.SeparatorPages;
            _chkNumbers.Checked = _settings.NumberPages;
            _chkRemote.Checked = _settings.AllowRemoteImages;
            _chkProvenance.Checked = _settings.IncludeProvenance;
            _chkOpenFolder.Checked = _settings.OpenFolderAfter;
            if (_settings.WindowWidth >= MinimumSize.Width && _settings.WindowHeight >= MinimumSize.Height)
                Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        }

        private void SaveSettings()
        {
            _settings.Naming = (NamingPattern)Math.Max(0, _namingCombo.SelectedIndex);
            _settings.AppendPdfAttachments = _chkAppendPdf.Checked;
            _settings.SaveOtherAttachments = _chkSaveOthers.Checked;
            _settings.ConvertEmbeddedMessages = _chkEmbedded.Checked;
            _settings.SeparatorPages = _chkSeparators.Checked;
            _settings.NumberPages = _chkNumbers.Checked;
            _settings.AllowRemoteImages = _chkRemote.Checked;
            _settings.IncludeProvenance = _chkProvenance.Checked;
            _settings.OpenFolderAfter = _chkOpenFolder.Checked;
            if (WindowState == FormWindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.Save();
        }

        private void DetectBrowser()
        {
            _browserPath = BrowserLocator.Find();
            if (_browserPath == null)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = "Microsoft Edge est introuvable : la conversion ne pourra pas fonctionner.";
                AddLog("Configuration", "Microsoft Edge (ou Google Chrome) est introuvable sur cet ordinateur. Installez Microsoft Edge pour permettre la conversion.", true);
            }
            else
            {
                _status.Text = "Prêt. Rendu des courriels avec " + Path.GetFileName(_browserPath) + ".";
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
                BeginInvoke(new Action(() => ConvertOutlookSelection(names)));
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
            Enqueue(new Batch(files, null, null));
        }

        private void ChooseFiles()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Choisir les courriels à convertir",
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

        private async void ConvertOutlookSelection(List<string>? draggedNames)
        {
            // Give Outlook a moment to finish its own drag operation before we call into it.
            await Task.Delay(250);

            string folder;
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
                _status.Text = "Prêt.";
                MessageBox.Show(this, ex.Message, "Outlook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                _status.Text = "Prêt.";
                MessageBox.Show(this, "La communication avec Outlook a échoué : " + ex.Message, "Outlook", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            UseWaitCursor = false;

            foreach (var w in warnings) AddLog("Outlook", w, true);
            if (saved.Count == 0)
            {
                _status.Text = "Prêt.";
                AddLog("Outlook", "Aucun courriel sélectionné dans Outlook. Sélectionnez un ou plusieurs messages dans la liste, puis réessayez.", true);
                return;
            }
            if (draggedNames != null && draggedNames.Count > 0 && draggedNames.Count != saved.Count)
                AddLog("Outlook", saved.Count + " élément(s) lus dans la sélection Outlook alors que " + draggedNames.Count + " élément(s) ont été glissés. Vérifiez le résultat.", true);

            Enqueue(new Batch(saved, folder, temp));
        }

        // ------------------------------------------------------------------
        // Conversion
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
            var totalOk = 0;
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
                                _status.Text = "Conversion " + (i + 1) + " de " + batch.Files.Count + " : " + Path.GetFileName(file);
                                var ct = _cts.Token;
                                var result = await Task.Run(() => service.ConvertFile(file, batch.OutputFolder, ct), ct).ConfigureAwait(true);
                                ReportResult(result, batch.OutputFolder != null);
                                if (result.Success)
                                {
                                    totalOk++;
                                    var dir = Path.GetDirectoryName(result.OutputPdfPath!);
                                    if (dir != null && !outputFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) outputFolders.Add(dir);
                                }
                                else if (result.Error != "Conversion annulée.") totalFailed++;
                                else cancelled = true;
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
                        AddLog("Conversion", "Erreur imprévue : " + ex.Message, true);
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

            _status.ForeColor = totalFailed > 0 ? Color.Firebrick : SystemColors.ControlText;
            _status.Text = (cancelled ? "Annulé. " : "Terminé. ") + totalOk + " courriel(s) converti(s)" + (totalFailed > 0 ? ", " + totalFailed + " erreur(s)." : ".");

            if (_chkOpenFolder.Checked && !cancelled && outputFolders.Count > 0)
            {
                foreach (var f in outputFolders.Take(3))
                    TryOpen(f);
            }
        }

        private void SetBusyUi(bool busy)
        {
            _btnCancel.Enabled = busy;
            _btnFiles.Enabled = true;
            _btnOutlook.Enabled = !busy;
            _namingCombo.Enabled = !busy;
            foreach (var c in new[] { _chkAppendPdf, _chkSaveOthers, _chkEmbedded, _chkSeparators, _chkNumbers, _chkRemote, _chkProvenance })
                c.Enabled = !busy;
            _dropZone.Title = busy ? "Conversion en cours…" : "Glissez-déposez ici vos courriels";
            _dropZone.Subtitle = busy ? "Vous pouvez déposer d'autres courriels; ils seront traités à la suite." : "Fichiers .msg ou .eml, dossiers complets, ou messages glissés directement depuis Outlook";
            _dropZone.Invalidate();
        }

        private void ReportResult(ConversionResult r, bool fromOutlook)
        {
            var source = Path.GetFileName(r.Source);
            if (r.Success)
            {
                var text = "PDF créé : " + Path.GetFileName(r.OutputPdfPath) + " (" + r.PageCount + (r.PageCount == 1 ? " page" : " pages") + ")";
                if (r.AppendedAttachments.Count > 0) text += " · " + r.AppendedAttachments.Count + " pièce(s) jointe(s) fusionnée(s)";
                if (r.SavedAttachmentPaths.Count > 0) text += " · " + r.SavedAttachmentPaths.Count + " pièce(s) jointe(s) enregistrée(s) dans un sous-dossier";
                var item = AddLog(source, text, false);
                item.Tag = r.OutputPdfPath;
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
            var item = new ListViewItem(new[] { source, text });
            if (isProblem) item.ForeColor = color ?? Color.Firebrick;
            _log.Items.Add(item);
            item.EnsureVisible();
            return item;
        }

        // ------------------------------------------------------------------
        // Log actions
        // ------------------------------------------------------------------

        private string? SelectedPdf()
        {
            if (_log.SelectedItems.Count == 0) return null;
            return _log.SelectedItems[0].Tag as string;
        }

        private void OpenSelectedPdf()
        {
            var pdf = SelectedPdf();
            if (pdf != null && File.Exists(pdf)) TryOpen(pdf);
        }

        private void RevealSelected()
        {
            var pdf = SelectedPdf();
            if (pdf == null || !File.Exists(pdf)) return;
            try { Process.Start("explorer.exe", "/select,\"" + pdf + "\""); } catch { }
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
