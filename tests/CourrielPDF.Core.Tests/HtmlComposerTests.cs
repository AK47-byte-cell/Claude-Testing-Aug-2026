using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Html;
using CourrielPDF.Core.Model;
using CourrielPDF.Core.Reading;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    public class HtmlComposerTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private EmailMessage LoadEml() => EmailLoader.Load(Fixtures.WriteEml(_dir));

        [Fact]
        public void Cid_images_become_data_uris_and_are_marked_inline()
        {
            var m = LoadEml();
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            Assert.Contains("data:image/png;base64,", body.InnerHtml);
            Assert.DoesNotContain("cid:", body.InnerHtml);
            Assert.Single(body.InlinedAttachments);
            Assert.Equal("logo.png", body.InlinedAttachments.First().FileName);
        }

        [Fact]
        public void Scripts_and_event_handlers_are_removed()
        {
            var m = LoadEml();
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            Assert.DoesNotContain("<script", body.InnerHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alert(", body.InnerHtml);
            Assert.DoesNotContain("onclick", body.InnerHtml, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Remote_images_are_replaced_unless_allowed()
        {
            var m = LoadEml();
            var blocked = HtmlComposer.PrepareBody(m, new HtmlComposerOptions { AllowRemoteImages = false });
            Assert.DoesNotContain("banniere.png", blocked.InnerHtml);
            Assert.DoesNotContain("pixel.gif", blocked.InnerHtml);
            Assert.Contains("Image externe non téléchargée", blocked.InnerHtml);
            Assert.Contains("Bannière", blocked.InnerHtml);
            Assert.NotEmpty(blocked.Warnings);

            var allowed = HtmlComposer.PrepareBody(m, new HtmlComposerOptions { AllowRemoteImages = true });
            Assert.Contains("banniere.png", allowed.InnerHtml);
        }

        [Fact]
        public void Word_page_rules_are_stripped_from_styles()
        {
            var m = LoadEml();
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            var css = string.Join("\n", body.StyleSheets);
            Assert.DoesNotContain("@page", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("page:", css.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("margin: 0", css);
        }

        [Fact]
        public void Document_has_french_header_with_all_fields()
        {
            var m = LoadEml();
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            var items = new[]
            {
                new AttachmentDisplayItem("Rapport d'expertise.pdf", 12345, "jointe ci-après"),
                new AttachmentDisplayItem("Notes.docx", 2048, "enregistrée dans le sous-dossier"),
            };
            var html = HtmlComposer.ComposeDocument(m, body, items, new HtmlComposerOptions { SourceFileName = "Courriel test.eml" });

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("<html lang=\"fr\">", html);
            foreach (var label in new[] { "De :", "Envoyé :", "À :", "Cc :", "Cci :", "Objet :", "Importance :", "Pièces jointes :" })
                Assert.Contains(label, html);
            Assert.Contains("Jean Tremblay &lt;jean.tremblay@example.com&gt;", html);
            Assert.Contains("Me Julie Gagnon &lt;jgagnon@cabinet-exemple.ca&gt;; greffe@exemple.qc.ca", html);
            Assert.Contains("Archives &lt;archives@cabinet-exemple.ca&gt;", html);
            Assert.Contains("Haute", html);
            Assert.Contains("12 Ko", html);
            Assert.Contains("2 Ko", html);
            Assert.Contains("jointe ci-après", html);
            Assert.Contains("Fichier source : Courriel test.eml", html);
            Assert.Contains(FrenchFormats.LongDateTime(Fixtures.SentOn), html);
            Assert.Contains("size: 8.5in 11in", html);
            Assert.Contains("Bonjour Maître,", html);
        }

        [Fact]
        public void Plain_text_bodies_are_escaped_and_linkified()
        {
            var m = new EmailMessage { Subject = "Texte", TextBody = "Voir <ici> : https://www.example.com/dossier?x=1\nMerci." };
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            Assert.True(body.IsPlainText);
            Assert.Contains("&lt;ici&gt;", body.InnerHtml);
            Assert.Contains("<a href=\"https://www.example.com/dossier?x=1\">", body.InnerHtml);
            Assert.Contains("cpdf-plain", body.InnerHtml);
        }

        [Fact]
        public void Rtf_bodies_are_converted_to_html()
        {
            var m = EmailLoader.Load(Fixtures.WriteRtfMsg(_dir));
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            Assert.Contains("RTF", body.InnerHtml);
            Assert.Contains("été", body.InnerHtml);
            Assert.False(body.IsPlainText);
        }

        [Fact]
        public void Missing_body_yields_placeholder_text()
        {
            var m = new EmailMessage { Subject = "Vide" };
            var body = HtmlComposer.PrepareBody(m, new HtmlComposerOptions());
            Assert.Contains("aucun texte", body.InnerHtml);
        }

        [Fact]
        public void Sizes_and_dates_use_quebec_french_conventions()
        {
            Assert.Equal("512 octets", FrenchFormats.FileSize(512));
            Assert.Equal("245 Ko", FrenchFormats.FileSize(245 * 1024));
            Assert.Equal("1,5 Mo", FrenchFormats.FileSize((long)(1.5 * 1024 * 1024)));
            var text = FrenchFormats.LongDateTime(Fixtures.SentOn);
            Assert.Matches(@"^\w+ \d{1,2} \w+ 2026 \d{2} h \d{2}$", text);
            Assert.Matches(@"^2026-09-0[23] \d{2}h\d{2}$", FrenchFormats.FileDate(Fixtures.SentOn));
        }
    }
}
