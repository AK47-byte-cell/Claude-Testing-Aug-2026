using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Reading;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    public class ReaderTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Eml_is_parsed_with_headers_body_and_attachments()
        {
            var path = Fixtures.WriteEml(_dir);
            var m = EmailLoader.Load(path);

            Assert.Equal(Fixtures.Subject, m.Subject);
            Assert.Equal("Jean Tremblay", m.From!.Name);
            Assert.Equal("jean.tremblay@example.com", m.From.Address);
            Assert.Equal(2, m.To.Count);
            Assert.Single(m.Cc);
            Assert.Single(m.Bcc);
            Assert.Equal(Fixtures.SentOn, m.SentOn);
            Assert.Equal("Haute", m.Importance);
            Assert.True(m.HasHtmlBody);
            Assert.Contains("rapport d'expertise", m.HtmlBody);
            Assert.Equal("eml", m.SourceFormat);
            Assert.Equal(path, m.SourcePath);

            Assert.Equal(4, m.Attachments.Count);
            var logo = m.Attachments.Single(a => a.FileName == "logo.png");
            Assert.Equal("logo1", logo.ContentId);
            Assert.True(logo.IsInline);
            Assert.True(logo.IsImage);

            var pdf = m.Attachments.Single(a => a.FileName == "Rapport d'expertise.pdf");
            Assert.True(pdf.IsPdf);
            Assert.False(pdf.IsInline);

            var docx = m.Attachments.Single(a => a.FileName == "Notes.docx");
            Assert.False(docx.IsPdf);

            var fwd = m.Attachments.Single(a => a.EmbeddedMessage != null);
            Assert.Equal("Courriel transféré.eml", fwd.FileName);
            Assert.Equal("TR : Annexe photographique", fwd.EmbeddedMessage!.Subject);
            Assert.Single(fwd.EmbeddedMessage.Attachments);
            Assert.True(fwd.EmbeddedMessage.Attachments[0].IsPdf);
            Assert.NotEmpty(fwd.Data);
        }

        [Fact]
        public void Msg_is_parsed_with_headers_body_and_attachments()
        {
            var path = Fixtures.WriteMsg(_dir);
            var m = EmailLoader.Load(path);

            Assert.Equal(Fixtures.Subject, m.Subject);
            Assert.Equal("Jean Tremblay", m.From!.Name);
            Assert.Equal("jean.tremblay@example.com", m.From.Address);
            Assert.Single(m.To);
            Assert.Equal("Me Julie Gagnon", m.To[0].Name);
            Assert.Single(m.Cc);
            Assert.Single(m.Bcc);
            Assert.NotNull(m.SentOn);
            Assert.Equal(Fixtures.SentOn.UtcDateTime, m.SentOn!.Value.UtcDateTime);
            Assert.True(m.HasHtmlBody);
            Assert.Contains("rapport d'expertise", m.HtmlBody);
            Assert.Equal("msg", m.SourceFormat);

            Assert.Equal(3, m.Attachments.Count);
            var logo = m.Attachments.Single(a => a.FileName == "logo.png");
            Assert.Equal("logo1", logo.ContentId);
            Assert.True(logo.IsImage);
            Assert.True(m.Attachments.Single(a => a.FileName == "Rapport d'expertise.pdf").IsPdf);
            Assert.Contains(m.Attachments, a => a.FileName == "Notes.docx");
        }

        [Fact]
        public void Msg_with_rtf_only_body_exposes_rtf()
        {
            var path = Fixtures.WriteRtfMsg(_dir);
            var m = EmailLoader.Load(path);
            Assert.False(m.HasHtmlBody);
            Assert.True(m.HasRtfBody);
            Assert.Contains("RTF", m.RtfBody);
        }

        [Fact]
        public void Loader_detects_msg_by_signature_even_with_wrong_extension()
        {
            var path = Fixtures.WriteMsg(_dir, "sans-extension.dat");
            var m = EmailLoader.Load(path);
            Assert.Equal("msg", m.SourceFormat);
            Assert.Equal(Fixtures.Subject, m.Subject);
        }

        [Fact]
        public void Loader_rejects_missing_file()
        {
            Assert.Throws<FileNotFoundException>(() => EmailLoader.Load(Path.Combine(_dir, "inexistant.msg")));
        }
    }
}
