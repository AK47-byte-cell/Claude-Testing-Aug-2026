using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Html;
using CourrielPDF.Core.Naming;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    public class ConversionTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static int PageCount(string pdf)
        {
            using (var d = PdfReader.Open(pdf, PdfDocumentOpenMode.InformationOnly))
                return d.PageCount;
        }

        [BrowserFact]
        public void Eml_is_converted_with_merged_pdfs_saved_attachments_and_forwarded_mail()
        {
            var source = Fixtures.WriteEml(_dir);
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ConvertFile(source);
                Assert.True(r.Success, r.Error);
                Assert.Equal(Path.Combine(_dir, "Courriel test.pdf"), r.OutputPdfPath);
                Assert.True(File.Exists(r.OutputPdfPath));

                // body (1) + separator + Rapport (2) + separator + forwarded mail (body 1 + separator + Annexe 1) = 8
                Assert.Equal(8, r.PageCount);
                Assert.Equal(8, PageCount(r.OutputPdfPath!));

                Assert.Equal(new[] { "Rapport d'expertise.pdf", "[Courriel transféré.eml] Annexe.pdf", "Courriel transféré.eml" }, r.AppendedAttachments);
                Assert.Single(r.SavedAttachmentPaths);
                Assert.Equal(Path.Combine(_dir, "Courriel test - pièces jointes", "Notes.docx"), r.SavedAttachmentPaths[0]);
                Assert.True(File.Exists(r.SavedAttachmentPaths[0]));

                using (var doc = PdfReader.Open(r.OutputPdfPath!, PdfDocumentOpenMode.InformationOnly))
                {
                    Assert.Equal(Fixtures.Subject, doc.Info.Title);
                    Assert.Contains("Jean Tremblay", doc.Info.Author);
                }
            }
        }

        [BrowserFact]
        public void Msg_is_converted_next_to_the_source_file()
        {
            var source = Fixtures.WriteMsg(_dir);
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ConvertFile(source);
                Assert.True(r.Success, r.Error);
                Assert.Equal(Path.Combine(_dir, "Courriel test.pdf"), r.OutputPdfPath);
                // body (1) + separator + Rapport (2) = 4
                Assert.Equal(4, r.PageCount);
                Assert.Single(r.AppendedAttachments);
                Assert.Single(r.SavedAttachmentPaths);
                Assert.EndsWith("Notes.docx", r.SavedAttachmentPaths[0]);
            }
        }

        [BrowserFact]
        public void Options_disable_merging_separators_and_saving()
        {
            var source = Fixtures.WriteMsg(_dir);
            var options = new ConversionOptions
            {
                AppendPdfAttachments = false,
                SaveOtherAttachments = false,
                SeparatorPages = false,
                NumberPages = false,
                IncludeProvenance = false,
            };
            using (var service = new ConversionService(options))
            {
                var r = service.ConvertFile(source);
                Assert.True(r.Success, r.Error);
                Assert.Equal(1, r.PageCount);
                Assert.Empty(r.AppendedAttachments);
                Assert.Empty(r.SavedAttachmentPaths);
                Assert.False(Directory.Exists(Path.Combine(_dir, "Courriel test - pièces jointes")));
            }
        }

        [BrowserFact]
        public void Naming_pattern_and_output_directory_are_honoured_and_collisions_avoided()
        {
            var source = Fixtures.WriteEml(_dir);
            var outDir = Path.Combine(_dir, "sortie");
            var options = new ConversionOptions { Naming = NamingPattern.DateSenderSubject, ConvertEmbeddedMessages = false };
            using (var service = new ConversionService(options))
            {
                var r1 = service.ConvertFile(source, outDir);
                var r2 = service.ConvertFile(source, outDir);
                Assert.True(r1.Success, r1.Error);
                Assert.True(r2.Success, r2.Error);
                var expected = FrenchFormats.FileDate(Fixtures.SentOn) + " - Jean Tremblay - Réclamation 2024-123 - vices cachés – rapport d'expertise";
                Assert.Equal(Path.Combine(outDir, expected + ".pdf"), r1.OutputPdfPath);
                Assert.Equal(Path.Combine(outDir, expected + " (2).pdf"), r2.OutputPdfPath);
                // The forwarded e-mail is kept as a raw .eml when it is not converted.
                Assert.Contains(r1.SavedAttachmentPaths, p => p.EndsWith("Courriel transféré.eml"));
                Assert.True(Directory.Exists(Path.Combine(outDir, expected + " - pièces jointes")));
                Assert.True(Directory.Exists(Path.Combine(outDir, expected + " (2) - pièces jointes")));
            }
        }

        [BrowserFact]
        public void Rtf_only_message_is_converted()
        {
            var source = Fixtures.WriteRtfMsg(_dir);
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ConvertFile(source);
                Assert.True(r.Success, r.Error);
                Assert.Equal(1, r.PageCount);
            }
        }

        [Fact]
        public void Unreadable_file_reports_an_error_instead_of_throwing()
        {
            var bogus = Path.Combine(_dir, "faux.msg");
            File.WriteAllText(bogus, "ceci n'est pas un courriel");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ConvertFile(bogus);
                Assert.False(r.Success);
                Assert.NotNull(r.Error);
                Assert.Empty(Directory.GetFiles(_dir, "*.pdf"));
            }
        }

        [Fact]
        public void Missing_browser_is_reported_in_french_and_nothing_is_written()
        {
            var source = Fixtures.WriteMsg(_dir);
            var options = new ConversionOptions { BrowserPath = Path.Combine(_dir, "nul.exe") };
            var previous = Environment.GetEnvironmentVariable("COURRIELPDF_BROWSER");
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", null);
                Environment.SetEnvironmentVariable("PATH", _dir);
                using (var service = new ConversionService(options))
                {
                    var r = service.ConvertFile(source);
                    Assert.False(r.Success);
                    Assert.Contains("Microsoft Edge", r.Error);
                    Assert.Empty(Directory.GetFiles(_dir, "*.pdf"));
                    Assert.Empty(Directory.GetDirectories(_dir));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", previous);
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }

        [Fact]
        public void Input_expander_walks_folders_and_ignores_other_files()
        {
            var sub = Path.Combine(_dir, "sous-dossier");
            Directory.CreateDirectory(sub);
            var a = Fixtures.WriteMsg(_dir, "b.msg");
            var b = Fixtures.WriteEml(sub, "a.eml");
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
            var files = InputExpander.Expand(new[] { _dir, a });
            Assert.Equal(2, files.Count);
            Assert.Contains(a, files);
            Assert.Contains(b, files);
        }
    }
}
