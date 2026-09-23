using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Model;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    /// <summary>Attachments-only mode: no PDF, every attachment of every message into one folder.</summary>
    public class AttachmentExtractionTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string[] Names(ConversionResult r) => r.SavedAttachmentPaths.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

        [Fact]
        public void Eml_attachments_forwarded_mail_and_its_attachments_land_flat_in_the_destination()
        {
            var source = Fixtures.WriteEml(_dir);
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(source, destination);
                Assert.True(r.Success, r.Error);
                Assert.Equal(ConversionMode.AttachmentsOnly, r.Mode);
                Assert.Null(r.OutputPdfPath);
                Assert.Equal(new[] { "Annexe.pdf", "Courriel transféré.eml", "Notes.docx", "Rapport d'expertise.pdf" }, Names(r));
                Assert.Equal(1, r.SkippedInlineImages);
                Assert.All(r.SavedAttachmentPaths, p => Assert.Equal(destination, Path.GetDirectoryName(p)));
                Assert.All(r.SavedAttachmentPaths, p => Assert.True(File.Exists(p)));
                Assert.Empty(Directory.GetDirectories(destination));
                Assert.False(File.Exists(Path.Combine(destination, "Courriel test.pdf")));
                Assert.Empty(Directory.GetFiles(_dir, "*.pdf"));
                Assert.Empty(r.Warnings);
            }
        }

        [Fact]
        public void Msg_attachments_are_saved_and_the_signature_logo_is_skipped()
        {
            var source = Fixtures.WriteMsg(_dir);
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(source, destination);
                Assert.True(r.Success, r.Error);
                Assert.Equal(new[] { "Notes.docx", "Rapport d'expertise.pdf" }, Names(r));
                Assert.Equal(1, r.SkippedInlineImages);
            }
        }

        [Fact]
        public void Same_message_twice_produces_numbered_copies_instead_of_overwriting()
        {
            var source = Fixtures.WriteEml(_dir);
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var first = service.ExtractAttachments(source, destination);
                var second = service.ExtractAttachments(source, destination);
                Assert.True(first.Success, first.Error);
                Assert.True(second.Success, second.Error);
                Assert.Contains(Path.Combine(destination, "Rapport d'expertise (2).pdf"), second.SavedAttachmentPaths);
                Assert.Contains(Path.Combine(destination, "Notes (2).docx"), second.SavedAttachmentPaths);
                Assert.Equal(8, Directory.GetFiles(destination).Length);
            }
        }

        [Fact]
        public void Extraction_needs_no_browser_and_never_asks_for_a_renderer()
        {
            var source = Fixtures.WriteMsg(_dir);
            var destination = Path.Combine(_dir, "pj");
            var options = new ConversionOptions
            {
                BrowserPath = Path.Combine(_dir, "nul.exe"),
                RendererFactory = () => throw new InvalidOperationException("Le moteur de rendu ne doit pas être sollicité en mode pièces jointes."),
            };
            var previous = Environment.GetEnvironmentVariable("COURRIELPDF_BROWSER");
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", null);
                Environment.SetEnvironmentVariable("PATH", _dir);
                using (var service = new ConversionService(options))
                {
                    var r = service.ExtractAttachments(source, destination);
                    Assert.True(r.Success, r.Error);
                    Assert.Equal(2, r.SavedAttachmentPaths.Count);
                    Assert.Null(r.Engine);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", previous);
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }

        [Fact]
        public void Message_without_attachments_succeeds_with_nothing_saved()
        {
            var source = Fixtures.WriteRtfMsg(_dir);
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(source, destination);
                Assert.True(r.Success, r.Error);
                Assert.Empty(r.SavedAttachmentPaths);
                Assert.Equal(0, r.SkippedInlineImages);
            }
        }

        [Fact]
        public void Inline_images_are_saved_when_asked_for()
        {
            var source = Fixtures.WriteEml(_dir);
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions { AttachmentsIncludeInlineImages = true }))
            {
                var r = service.ExtractAttachments(source, destination);
                Assert.True(r.Success, r.Error);
                Assert.Contains("logo.png", Names(r));
                Assert.Equal(0, r.SkippedInlineImages);
            }
        }

        [Fact]
        public void Forwarded_mail_handling_can_be_narrowed()
        {
            var source = Fixtures.WriteEml(_dir);
            using (var service = new ConversionService(new ConversionOptions { AttachmentsExtractForwardedMessages = false }))
            {
                var r = service.ExtractAttachments(source, Path.Combine(_dir, "a"));
                Assert.True(r.Success, r.Error);
                Assert.Equal(new[] { "Courriel transféré.eml", "Notes.docx", "Rapport d'expertise.pdf" }, Names(r));
            }
            using (var service = new ConversionService(new ConversionOptions { AttachmentsSaveForwardedMessages = false }))
            {
                var r = service.ExtractAttachments(source, Path.Combine(_dir, "b"));
                Assert.True(r.Success, r.Error);
                Assert.Equal(new[] { "Annexe.pdf", "Notes.docx", "Rapport d'expertise.pdf" }, Names(r));
            }
        }

        [Fact]
        public void Large_pictures_shown_in_the_body_are_real_attachments_small_logos_are_not()
        {
            // A 200 KB "photo" with a JPEG header announcing 4096 x 3072 pixels.
            var photo = new byte[200 * 1024];
            new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x0C, 0x00, 0x10, 0x00 }.CopyTo(photo, 0);

            var message = new EmailMessage
            {
                Subject = "Photos du sinistre",
                HtmlBody = "<html><body><p>Voir les photos.</p><img src=\"cid:photo1\"><img src=\"cid:logo1\"></body></html>",
            };
            message.Attachments.Add(new EmailAttachment("IMG_0001.jpg", photo) { ContentId = "photo1", MimeType = "image/jpeg", IsInline = true });
            message.Attachments.Add(new EmailAttachment("logo.png", Fixtures.Png1x1) { ContentId = "logo1", MimeType = "image/png", IsInline = true, IsHidden = true });
            message.Attachments.Add(new EmailAttachment("Rapport.pdf", Fixtures.BlankPdf(1)));

            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(message, "photos.msg", destination);
                Assert.True(r.Success, r.Error);
                Assert.Equal(new[] { "IMG_0001.jpg", "Rapport.pdf" }, Names(r));
                Assert.Equal(1, r.SkippedInlineImages);
            }
        }

        [Fact]
        public void Empty_attachments_are_reported_not_written()
        {
            var message = new EmailMessage { Subject = "Vide", TextBody = "x" };
            message.Attachments.Add(new EmailAttachment("rien.docx", Array.Empty<byte>()));
            var destination = Path.Combine(_dir, "pj");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(message, "vide.msg", destination);
                Assert.True(r.Success, r.Error);
                Assert.Empty(r.SavedAttachmentPaths);
                Assert.Single(r.Warnings);
                Assert.Contains("rien.docx", r.Warnings[0]);
            }
        }

        [Fact]
        public void Unreadable_file_reports_an_error()
        {
            var bogus = Path.Combine(_dir, "faux.msg");
            File.WriteAllText(bogus, "ceci n'est pas un courriel");
            using (var service = new ConversionService(new ConversionOptions()))
            {
                var r = service.ExtractAttachments(bogus, Path.Combine(_dir, "pj"));
                Assert.False(r.Success);
                Assert.NotNull(r.Error);
            }
        }
    }

    public class ImageSnifferTests
    {
        [Fact]
        public void Png_dimensions_are_read_from_the_header()
        {
            Assert.Equal("image/png", ImageSniffer.DetectMimeType(Fixtures.Png1x1));
            Assert.True(ImageSniffer.TryGetDimensions(Fixtures.Png1x1, out var w, out var h));
            Assert.Equal(1, w);
            Assert.Equal(1, h);
        }

        [Fact]
        public void Jpeg_dimensions_come_from_the_start_of_frame_segment()
        {
            var jpeg = new byte[64];
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC2, 0x00, 0x11, 0x08, 0x02, 0x58, 0x03, 0x20 }.CopyTo(jpeg, 0);
            Assert.Equal("image/jpeg", ImageSniffer.DetectMimeType(jpeg));
            Assert.True(ImageSniffer.TryGetDimensions(jpeg, out var w, out var h));
            Assert.Equal(800, w);
            Assert.Equal(600, h);
        }

        [Fact]
        public void Gif_dimensions_are_little_endian()
        {
            var gif = new byte[32];
            System.Text.Encoding.ASCII.GetBytes("GIF89a").CopyTo(gif, 0);
            gif[6] = 0x40; gif[7] = 0x01; // 320
            gif[8] = 0xC8; gif[9] = 0x00; // 200
            Assert.True(ImageSniffer.TryGetDimensions(gif, out var w, out var h));
            Assert.Equal(320, w);
            Assert.Equal(200, h);
        }

        [Fact]
        public void Unknown_content_is_not_an_image()
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 not an image at all, really not");
            Assert.Null(ImageSniffer.DetectMimeType(bytes));
            Assert.False(ImageSniffer.TryGetDimensions(bytes, out _, out _));
            Assert.False(ImageSniffer.IsBrowserDisplayable("image/tiff"));
            Assert.True(ImageSniffer.IsBrowserDisplayable("image/png"));
        }
    }
}
