using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CourrielPDF.Core.Html;
using CourrielPDF.Core.Model;
using CourrielPDF.Core.Naming;
using CourrielPDF.Core.Pdf;
using CourrielPDF.Core.Reading;
using CourrielPDF.Core.Rendering;
using PdfSharp.Pdf.IO;

namespace CourrielPDF.Core.Conversion
{
    /// <summary>
    /// Orchestrates the work on one e-mail. In PDF mode: load the message, plan the attachments, compose and render
    /// the HTML, merge the PDF attachments, save the other attachments, and write the final PDF. In attachments-only
    /// mode: load the message and write its attachments (and those of forwarded e-mails) into one folder.
    /// </summary>
    public sealed class ConversionService : IDisposable
    {
        public const string AttachmentFolderSuffix = " - pièces jointes";
        private const int MaxEmbeddedDepth = 3;

        private readonly ConversionOptions _options;
        private readonly string _workRoot;
        private IPdfRenderer? _renderer;
        private string? _browserPath;
        private bool _disposed;

        public ConversionService(ConversionOptions options)
        {
            _options = (options ?? new ConversionOptions()).Clone();
            _workRoot = Path.Combine(Path.GetTempPath(), ProductInfo.Name, "travail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workRoot);
            ProductInfo.EnsureInitialised();
        }

        public ConversionOptions Options => _options;

        /// <summary>Path of the browser that will be used; throws <see cref="BrowserNotFoundException"/> when none is available.</summary>
        public string ResolveBrowserPath()
        {
            if (_browserPath == null)
                _browserPath = BrowserLocator.Find(_options.BrowserPath) ?? throw new BrowserNotFoundException();
            return _browserPath;
        }

        private IPdfRenderer Renderer
        {
            get
            {
                if (_renderer == null)
                {
                    if (_options.RendererFactory != null)
                    {
                        _renderer = _options.RendererFactory() ?? throw new PdfRenderException("Aucun moteur de rendu n'a été fourni.");
                    }
                    else
                    {
                        _renderer = new ChromiumPdfRenderer(ResolveBrowserPath(), _workRoot)
                        {
                            Timeout = _options.RenderTimeout,
                            AllowNetwork = _options.AllowRemoteImages,
                        };
                    }
                }
                return _renderer;
            }
        }

        /// <summary>Makes sure a rendering engine is available before any output is written.</summary>
        private void EnsureRendererAvailable()
        {
            if (_options.RendererFactory != null)
            {
                var _ = Renderer;
                return;
            }
            ResolveBrowserPath();
        }

        // ------------------------------------------------------------------
        // PDF mode
        // ------------------------------------------------------------------

        /// <summary>Converts one .msg/.eml file. The PDF goes beside the file unless <paramref name="outputDirectory"/> is given.</summary>
        public ConversionResult ConvertFile(string sourcePath, string? outputDirectory = null, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ConversionResult(sourcePath) { Mode = ConversionMode.ConvertToPdf };
            try
            {
                Log("Lecture de " + sourcePath);
                var message = EmailLoader.Load(sourcePath);
                var dir = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory();
                ConvertCore(message, Path.GetFileName(sourcePath), dir, result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Conversion annulée.";
            }
            catch (Exception ex)
            {
                result.Error = Describe(ex);
                Log(ex.ToString());
            }
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>Converts an already loaded message (used for messages that do not exist as files).</summary>
        public ConversionResult ConvertMessage(EmailMessage message, string displayName, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ConversionResult(displayName) { Mode = ConversionMode.ConvertToPdf };
            try
            {
                ConvertCore(message, displayName, outputDirectory, result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Conversion annulée.";
            }
            catch (Exception ex)
            {
                result.Error = Describe(ex);
                Log(ex.ToString());
            }
            result.Duration = sw.Elapsed;
            return result;
        }

        private void ConvertCore(EmailMessage message, string sourceFileName, string outputDirectory, ConversionResult result, CancellationToken ct)
        {
            EnsureRendererAvailable();
            Directory.CreateDirectory(outputDirectory);
            var baseName = OutputNaming.BuildBaseName(message, _options.Naming, sourceFileName);
            var pdfPath = OutputNaming.MakeUniquePath(outputDirectory, baseName, ".pdf");
            var folderName = Path.GetFileNameWithoutExtension(pdfPath) + AttachmentFolderSuffix;
            string? attachmentsFolder = null;
            Func<string> folderProvider = () =>
            {
                if (attachmentsFolder == null)
                {
                    attachmentsFolder = OutputNaming.MakeUniqueDirectory(outputDirectory, folderName);
                    Directory.CreateDirectory(attachmentsFolder);
                }
                return attachmentsFolder;
            };

            var work = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var built = BuildPdf(message, sourceFileName, work, folderProvider, result, 0, ct);
                // Re-check for collisions: another conversion may have produced the same name meanwhile.
                if (File.Exists(pdfPath))
                    pdfPath = OutputNaming.MakeUniquePath(outputDirectory, baseName, ".pdf");
                File.Copy(built, pdfPath, false);
                result.OutputPdfPath = pdfPath;
                Log("PDF créé : " + pdfPath);
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        }

        /// <summary>Builds the PDF of one message (recursively for embedded e-mails) inside <paramref name="workDir"/>.</summary>
        private string BuildPdf(EmailMessage message, string sourceFileName, string workDir, Func<string> folderProvider, ConversionResult result, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var composerOptions = new HtmlComposerOptions
            {
                AllowRemoteImages = _options.AllowRemoteImages,
                IncludeProvenance = _options.IncludeProvenance && depth == 0,
                SourceFileName = sourceFileName,
                ConvertedAt = DateTimeOffset.Now,
            };

            var prepared = HtmlComposer.PrepareBody(message, composerOptions);
            foreach (var w in prepared.Warnings.Distinct())
                result.Warnings.Add(Prefix(depth, sourceFileName, w));

            var display = new List<AttachmentDisplayItem>();
            var parts = new List<PdfAttachmentPart>();
            var toSave = new List<EmailAttachment>();

            try
            {
                var partIndex = 0;
                foreach (var a in message.Attachments)
                {
                    ct.ThrowIfCancellationRequested();
                    if (AttachmentClassifier.IsRenderedInBody(a, prepared)) continue;

                    if (a.EmbeddedMessage != null && _options.ConvertEmbeddedMessages && depth < MaxEmbeddedDepth)
                    {
                        try
                        {
                            var subDir = Path.Combine(workDir, "pj-" + (++partIndex));
                            Directory.CreateDirectory(subDir);
                            var subPdf = BuildPdf(a.EmbeddedMessage, a.FileName, subDir, folderProvider, result, depth + 1, ct);
                            var doc = PdfReader.Open(subPdf, PdfDocumentOpenMode.Import);
                            var pdfName = Path.GetFileNameWithoutExtension(a.FileName) + ".pdf";
                            parts.Add(new PdfAttachmentPart(pdfName, new FileInfo(subPdf).Length, doc, "Courriel joint"));
                            display.Add(new AttachmentDisplayItem(a.FileName, a.Size, "courriel joint, converti et reproduit ci-après"));
                            result.AppendedAttachments.Add(Prefix(depth, sourceFileName, a.FileName));
                            continue;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            result.Warnings.Add(Prefix(depth, sourceFileName, "Le courriel joint « " + a.FileName + " » n'a pu être converti (" + Shorten(ex.Message) + "); il est enregistré tel quel."));
                        }
                    }

                    if (a.IsPdf && _options.AppendPdfAttachments)
                    {
                        var doc = PdfAssembler.TryOpenForImport(a.Data, out var reason);
                        if (doc != null)
                        {
                            parts.Add(new PdfAttachmentPart(a.FileName, a.Size, doc));
                            display.Add(new AttachmentDisplayItem(a.FileName, a.Size, "jointe ci-après"));
                            result.AppendedAttachments.Add(Prefix(depth, sourceFileName, a.FileName));
                            continue;
                        }
                        result.Warnings.Add(Prefix(depth, sourceFileName, "La pièce jointe « " + a.FileName + " » n'a pas été fusionnée : " + reason + "."));
                    }

                    if (_options.SaveOtherAttachments)
                    {
                        if (a.Data.Length == 0 && a.EmbeddedMessage == null)
                        {
                            display.Add(new AttachmentDisplayItem(a.FileName, a.Size, "vide, non enregistrée"));
                            continue;
                        }
                        toSave.Add(a);
                        display.Add(new AttachmentDisplayItem(a.FileName, a.Size, "enregistrée dans le sous-dossier"));
                    }
                    else
                    {
                        display.Add(new AttachmentDisplayItem(a.FileName, a.Size, "non enregistrée"));
                    }
                }

                var html = HtmlComposer.ComposeDocument(message, prepared, display, composerOptions);
                var htmlPath = Path.Combine(workDir, "courriel.html");
                File.WriteAllText(htmlPath, html, new UTF8Encoding(false));

                var bodyPdf = Path.Combine(workDir, "corps.pdf");
                var renderer = Renderer;
                Log("Rendu du courriel avec " + renderer.Description);
                renderer.RenderFile(htmlPath, bodyPdf, ct);
                result.Engine = renderer.Description;

                foreach (var a in toSave)
                {
                    ct.ThrowIfCancellationRequested();
                    if (a.Data.Length == 0) continue;
                    var saved = SaveAttachment(a, folderProvider());
                    result.SavedAttachmentPaths.Add(saved);
                    Log("Pièce jointe enregistrée : " + saved);
                }

                var finalPdf = Path.Combine(workDir, "final.pdf");
                var asm = new PdfAssemblyOptions
                {
                    SeparatorPages = _options.SeparatorPages,
                    NumberPages = _options.NumberPages && depth == 0,
                    Title = string.IsNullOrWhiteSpace(message.Subject) ? "(sans objet)" : message.Subject,
                    Author = message.From?.Display,
                    Subject = "Courriel converti en PDF",
                    CreationDate = message.BestDate?.ToLocalTime().DateTime,
                };
                var pages = PdfAssembler.Assemble(bodyPdf, parts, asm, finalPdf);
                if (depth == 0) result.PageCount = pages;
                return finalPdf;
            }
            finally
            {
                foreach (var p in parts) p.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Attachments-only mode
        // ------------------------------------------------------------------

        /// <summary>
        /// Saves the attachments of one .msg/.eml file into <paramref name="outputDirectory"/>, without creating any PDF.
        /// Forwarded e-mails are saved as files and their own attachments extracted as well. No rendering engine is used.
        /// </summary>
        public ConversionResult ExtractAttachments(string sourcePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ConversionResult(sourcePath) { Mode = ConversionMode.AttachmentsOnly };
            try
            {
                Log("Lecture de " + sourcePath);
                var message = EmailLoader.Load(sourcePath);
                ExtractCore(message, Path.GetFileName(sourcePath), outputDirectory, result, 0, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Extraction annulée.";
            }
            catch (Exception ex)
            {
                result.Error = Describe(ex);
                Log(ex.ToString());
            }
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>Same as <see cref="ExtractAttachments(string,string,CancellationToken)"/> for an already loaded message.</summary>
        public ConversionResult ExtractAttachments(EmailMessage message, string displayName, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ConversionResult(displayName) { Mode = ConversionMode.AttachmentsOnly };
            try
            {
                ExtractCore(message, displayName, outputDirectory, result, 0, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Error = "Extraction annulée.";
            }
            catch (Exception ex)
            {
                result.Error = Describe(ex);
                Log(ex.ToString());
            }
            result.Duration = sw.Elapsed;
            return result;
        }

        private void ExtractCore(EmailMessage message, string sourceFileName, string outputDirectory, ConversionResult result, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);

            // The body is analysed only to learn which pictures it displays; nothing is rendered.
            PreparedBody? prepared = null;
            if (!_options.AttachmentsIncludeInlineImages)
            {
                try
                {
                    prepared = HtmlComposer.PrepareBody(message, new HtmlComposerOptions { AllowRemoteImages = true, IncludeProvenance = false });
                }
                catch (Exception ex)
                {
                    Log("Analyse du corps impossible (" + Shorten(ex.Message) + "); les images sont classées d'après leurs indicateurs seulement.");
                }
            }

            foreach (var a in message.Attachments)
            {
                ct.ThrowIfCancellationRequested();

                if (a.EmbeddedMessage != null)
                {
                    if (_options.AttachmentsSaveForwardedMessages)
                    {
                        if (a.Data.Length > 0)
                        {
                            var savedMessage = SaveAttachment(a, outputDirectory);
                            result.SavedAttachmentPaths.Add(savedMessage);
                            Log("Courriel joint enregistré : " + savedMessage);
                        }
                        else
                        {
                            result.Warnings.Add(Prefix(depth, sourceFileName, "Le courriel joint « " + a.FileName + " » n'a pu être enregistré tel quel; seules ses pièces jointes le sont."));
                        }
                    }
                    if (_options.AttachmentsExtractForwardedMessages && depth < MaxEmbeddedDepth)
                        ExtractCore(a.EmbeddedMessage, a.FileName, outputDirectory, result, depth + 1, ct);
                    continue;
                }

                if (!_options.AttachmentsIncludeInlineImages && AttachmentClassifier.IsDecorativeInlineImage(a, prepared))
                {
                    result.SkippedInlineImages++;
                    continue;
                }

                if (a.Data.Length == 0)
                {
                    result.Warnings.Add(Prefix(depth, sourceFileName, "La pièce jointe « " + a.FileName + " » est vide et n'a pas été enregistrée."));
                    continue;
                }

                var saved = SaveAttachment(a, outputDirectory);
                result.SavedAttachmentPaths.Add(saved);
                Log("Pièce jointe enregistrée : " + saved);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string SaveAttachment(EmailAttachment a, string folder)
        {
            var name = OutputNaming.Sanitize(a.FileName, 150);
            if (name.Length == 0) name = "piece-jointe";
            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrEmpty(stem)) stem = "piece-jointe";
            if (string.IsNullOrEmpty(ext)) ext = a.EmbeddedMessageExtension ?? ".bin";
            var path = OutputNaming.MakeUniquePath(folder, stem, ext);
            File.WriteAllBytes(path, a.Data);
            return path;
        }

        private static string Prefix(int depth, string sourceFileName, string text)
        {
            return depth == 0 ? text : "[" + sourceFileName + "] " + text;
        }

        private static string Shorten(string s)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 100 ? s.Substring(0, 97) + "…" : s;
        }

        private static string Describe(Exception ex)
        {
            switch (ex)
            {
                case BrowserNotFoundException b: return b.Message;
                case PdfRenderException r: return "Erreur de rendu : " + r.Message;
                case FileNotFoundException f: return "Fichier introuvable : " + (f.FileName ?? f.Message);
                case UnauthorizedAccessException u: return "Accès refusé : " + u.Message;
                case PathTooLongException: return "Le chemin du fichier est trop long pour Windows.";
                case IOException io: return "Erreur d'écriture ou de lecture : " + io.Message;
                default: return "Le courriel n'a pu être traité (" + ex.GetType().Name + " : " + Shorten(ex.Message) + ").";
            }
        }

        private void Log(string message)
        {
            try { _options.Log?.Invoke(message); } catch { }
        }

        private static void TryDeleteDirectory(string dir)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderer?.Dispose();
            TryDeleteDirectory(_workRoot);
        }
    }
}
