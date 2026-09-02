using System;
using System.IO;
using System.Text;
using MimeKit;
using MsgKit;
using PdfSharp.Pdf;

namespace CourrielPDF.Core.Tests
{
    /// <summary>Builds realistic .eml and .msg files on the fly so the tests never depend on external samples.</summary>
    public static class Fixtures
    {
        public static readonly DateTimeOffset SentOn = new DateTimeOffset(2026, 9, 2, 14, 35, 0, TimeSpan.FromHours(-4));

        public const string Subject = "Réclamation 2024-123 : vices cachés – rapport d'expertise";

        public static readonly byte[] Png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

        public static byte[] FakeDocx() => Encoding.ASCII.GetBytes("PK not really a docx, just bytes for the tests");

        public static byte[] BlankPdf(int pages)
        {
            using (var doc = new PdfDocument())
            {
                for (var i = 0; i < pages; i++) doc.AddPage();
                using (var ms = new MemoryStream())
                {
                    doc.Save(ms, false);
                    return ms.ToArray();
                }
            }
        }

        public const string HtmlBody =
            "<html><head><meta charset=\"utf-8\"><style>@page WordSection1 { size: 8.5in 11in; margin: 1in } div.WordSection1 { page: WordSection1 } p { margin: 0 }</style></head>" +
            "<body lang=\"fr-CA\" style=\"font-family:Calibri\"><div class=\"WordSection1\">" +
            "<p>Bonjour Maître,</p>" +
            "<p>Vous trouverez ci-joint le <b>rapport d'expertise</b> concernant les infiltrations d'eau.</p>" +
            "<img src=\"cid:logo1\" width=\"60\" height=\"60\" alt=\"Logo\">" +
            "<script>alert('xss')</script>" +
            "<img src=\"https://example.com/pixel.gif\" width=\"1\" height=\"1\">" +
            "<img src=\"https://example.com/banniere.png\" width=\"300\" height=\"60\" alt=\"Bannière\">" +
            "<p onclick=\"evil()\">Cordialement,<br>Jean Tremblay</p>" +
            "</div></body></html>";

        /// <summary>An .eml with HTML + inline CID image, a 2-page PDF, a .docx and a forwarded e-mail (which itself carries a 1-page PDF).</summary>
        public static string WriteEml(string directory, string fileName = "Courriel test.eml")
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress("Jean Tremblay", "jean.tremblay@example.com"));
            msg.To.Add(new MailboxAddress("Me Julie Gagnon", "jgagnon@cabinet-exemple.ca"));
            msg.To.Add(new MailboxAddress(string.Empty, "greffe@exemple.qc.ca"));
            msg.Cc.Add(new MailboxAddress("Marc Roy", "marc.roy@example.com"));
            msg.Bcc.Add(new MailboxAddress("Archives", "archives@cabinet-exemple.ca"));
            msg.Subject = Subject;
            msg.Date = SentOn;
            msg.Headers.Add("Importance", "High");

            var builder = new BodyBuilder { HtmlBody = HtmlBody, TextBody = "Bonjour Maître, voir le rapport ci-joint." };
            var logo = builder.LinkedResources.Add("logo.png", Png1x1, ContentType.Parse("image/png"));
            logo.ContentId = "logo1";
            builder.Attachments.Add("Rapport d'expertise.pdf", BlankPdf(2), ContentType.Parse("application/pdf"));
            builder.Attachments.Add("Notes.docx", FakeDocx(), ContentType.Parse("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));

            var inner = new MimeMessage();
            inner.From.Add(new MailboxAddress("Expert Inc.", "expert@example.com"));
            inner.To.Add(new MailboxAddress("Jean Tremblay", "jean.tremblay@example.com"));
            inner.Subject = "TR : Annexe photographique";
            inner.Date = SentOn.AddDays(-3);
            var innerBuilder = new BodyBuilder { HtmlBody = "<p>Voici l'annexe demandée.</p>" };
            innerBuilder.Attachments.Add("Annexe.pdf", BlankPdf(1), ContentType.Parse("application/pdf"));
            inner.Body = innerBuilder.ToMessageBody();

            var forwarded = new MessagePart("rfc822")
            {
                Message = inner,
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "Courriel transféré.eml" },
            };

            var mixed = new Multipart("mixed") { builder.ToMessageBody(), forwarded };
            msg.Body = mixed;

            var path = Path.Combine(directory, fileName);
            msg.WriteTo(path);
            return path;
        }

        /// <summary>A .msg (built with MsgKit) with HTML + inline CID image, a PDF and a .docx.</summary>
        public static string WriteMsg(string directory, string fileName = "Courriel test.msg")
        {
            using (var email = new Email(new Sender("jean.tremblay@example.com", "Jean Tremblay"), Subject))
            {
                email.Recipients.AddTo("jgagnon@cabinet-exemple.ca", "Me Julie Gagnon");
                email.Recipients.AddCc("marc.roy@example.com", "Marc Roy");
                email.Recipients.AddBcc("archives@cabinet-exemple.ca", "Archives");
                email.SentOn = SentOn.UtcDateTime;
                email.ReceivedOn = SentOn.UtcDateTime.AddMinutes(1);
                email.BodyHtml = HtmlBody;
                email.BodyText = "Bonjour Maître, voir le rapport ci-joint.";
                email.Attachments.Add(new MemoryStream(Png1x1), "logo.png", -1, true, "logo1");
                email.Attachments.Add(new MemoryStream(BlankPdf(2)), "Rapport d'expertise.pdf");
                email.Attachments.Add(new MemoryStream(FakeDocx()), "Notes.docx");
                var path = Path.Combine(directory, fileName);
                email.Save(path);
                return path;
            }
        }

        /// <summary>A .msg whose body only exists as RTF (no HTML part).</summary>
        public static string WriteRtfMsg(string directory, string fileName = "Courriel RTF.msg")
        {
            using (var email = new Email(new Sender("jean.tremblay@example.com", "Jean Tremblay"), "Message en RTF seulement"))
            {
                email.Recipients.AddTo("jgagnon@cabinet-exemple.ca", "Me Julie Gagnon");
                email.SentOn = SentOn.UtcDateTime;
                email.BodyRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22 Bonjour en \b RTF\b0 , avec un accent : \'e9t\'e9.\par}";
                email.BodyText = "Bonjour en RTF, avec un accent : été.";
                var path = Path.Combine(directory, fileName);
                email.Save(path);
                return path;
            }
        }

        public static string NewTempDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "CourrielPDF-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
