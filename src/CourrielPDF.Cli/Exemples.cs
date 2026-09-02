using System;
using System.IO;
using System.Text;
using MimeKit;
using MsgKit;
using PdfSharp.Pdf;

namespace CourrielPDF.Cli
{
    /// <summary>Writes a few representative .eml/.msg files, used by the build pipeline to exercise the desktop application.</summary>
    internal static class Exemples
    {
        private static readonly DateTimeOffset SentOn = new DateTimeOffset(2026, 9, 2, 14, 35, 0, TimeSpan.FromHours(-4));
        private static readonly byte[] Png1x1 = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

        private const string HtmlBody =
            "<html><head><style>p { margin: 0 0 6pt 0 }</style></head><body style=\"font-family:Calibri\">" +
            "<p>Bonjour Maître,</p><p>Vous trouverez ci-joint le <b>rapport d'expertise</b> concernant les infiltrations d'eau.</p>" +
            "<img src=\"cid:logo1\" width=\"60\" height=\"60\" alt=\"Logo\">" +
            "<img src=\"https://example.com/banniere.png\" width=\"300\" height=\"60\" alt=\"Bannière\">" +
            "<p>Cordialement,<br>Jean Tremblay</p></body></html>";

        public static int Generate(string directory)
        {
            Directory.CreateDirectory(directory);
            var eml = WriteEml(directory);
            var msg = WriteMsg(directory);
            var rtf = WriteRtfMsg(directory);
            Console.WriteLine("Exemples écrits :");
            Console.WriteLine("  " + eml);
            Console.WriteLine("  " + msg);
            Console.WriteLine("  " + rtf);
            return 0;
        }

        private static byte[] BlankPdf(int pages)
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

        private static string WriteEml(string directory)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Jean Tremblay", "jean.tremblay@example.com"));
            message.To.Add(new MailboxAddress("Me Julie Gagnon", "jgagnon@cabinet-exemple.ca"));
            message.Cc.Add(new MailboxAddress("Marc Roy", "marc.roy@example.com"));
            message.Subject = "Exemple .eml : rapport d'expertise";
            message.Date = SentOn;
            var builder = new BodyBuilder { HtmlBody = HtmlBody, TextBody = "Bonjour Maître, voir le rapport ci-joint." };
            var logo = builder.LinkedResources.Add("logo.png", Png1x1, ContentType.Parse("image/png"));
            logo.ContentId = "logo1";
            builder.Attachments.Add("Rapport d'expertise.pdf", BlankPdf(2), ContentType.Parse("application/pdf"));
            builder.Attachments.Add("Notes.docx", Encoding.ASCII.GetBytes("PK exemple"), ContentType.Parse("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
            message.Body = builder.ToMessageBody();
            var path = Path.Combine(directory, "Exemple courriel.eml");
            message.WriteTo(path);
            return path;
        }

        private static string WriteMsg(string directory)
        {
            using (var email = new Email(new Sender("jean.tremblay@example.com", "Jean Tremblay"), "Exemple .msg : rapport d'expertise"))
            {
                email.Recipients.AddTo("jgagnon@cabinet-exemple.ca", "Me Julie Gagnon");
                email.Recipients.AddCc("marc.roy@example.com", "Marc Roy");
                email.SentOn = SentOn.UtcDateTime;
                email.BodyHtml = HtmlBody;
                email.BodyText = "Bonjour Maître, voir le rapport ci-joint.";
                email.Attachments.Add(new MemoryStream(Png1x1), "logo.png", -1, true, "logo1");
                email.Attachments.Add(new MemoryStream(BlankPdf(2)), "Rapport d'expertise.pdf");
                var path = Path.Combine(directory, "Exemple courriel.msg");
                email.Save(path);
                return path;
            }
        }

        private static string WriteRtfMsg(string directory)
        {
            using (var email = new Email(new Sender("jean.tremblay@example.com", "Jean Tremblay"), "Exemple .msg en RTF"))
            {
                email.Recipients.AddTo("jgagnon@cabinet-exemple.ca", "Me Julie Gagnon");
                email.SentOn = SentOn.UtcDateTime;
                email.BodyRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22 Bonjour en \b RTF\b0 , avec un accent : \'e9t\'e9.\par}";
                email.BodyText = "Bonjour en RTF.";
                var path = Path.Combine(directory, "Exemple RTF.msg");
                email.Save(path);
                return path;
            }
        }
    }
}
