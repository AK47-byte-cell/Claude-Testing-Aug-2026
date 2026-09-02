using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Html;
using CourrielPDF.Core.Model;
using CourrielPDF.Core.Naming;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    public class OutputNamingTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static EmailMessage Sample()
        {
            var m = new EmailMessage { Subject = "RE: Dossier 2024/123 : rapport <final>?", SentOn = Fixtures.SentOn };
            m.From = new EmailAddress("Jean Tremblay", "jean.tremblay@example.com");
            return m;
        }

        [Fact]
        public void Sanitize_removes_forbidden_characters()
        {
            var s = OutputNaming.Sanitize("RE: Dossier 2024/123 : rapport <final>?  \r\n suite.");
            Assert.DoesNotContain(s, c => Path.GetInvalidFileNameChars().Contains(c));
            Assert.False(s.EndsWith("."));
            Assert.DoesNotContain("  ", s);
            Assert.Contains("Dossier 2024-123", s);
        }

        [Fact]
        public void Sanitize_prefixes_reserved_names_and_truncates()
        {
            Assert.Equal("_CON", OutputNaming.Sanitize("CON"));
            Assert.True(OutputNaming.Sanitize(new string('a', 500)).Length <= OutputNaming.MaxBaseNameLength);
            Assert.Equal(string.Empty, OutputNaming.Sanitize("  ...  "));
        }

        [Fact]
        public void Patterns_produce_expected_shapes()
        {
            var m = Sample();
            var date = FrenchFormats.FileDate(Fixtures.SentOn);

            Assert.Equal("Courriel test", OutputNaming.BuildBaseName(m, NamingPattern.SameAsSource, "Courriel test.msg"));
            Assert.StartsWith(date + " - RE- Dossier 2024-123", OutputNaming.BuildBaseName(m, NamingPattern.DateSubject, "x.msg"));
            Assert.StartsWith(date + " - Jean Tremblay - RE- Dossier", OutputNaming.BuildBaseName(m, NamingPattern.DateSenderSubject, "x.msg"));

            // Without a source file (direct Outlook drop) the subject is used.
            Assert.StartsWith("RE- Dossier 2024-123", OutputNaming.BuildBaseName(m, NamingPattern.SameAsSource, null));
        }

        [Fact]
        public void Missing_subject_and_date_fall_back_gracefully()
        {
            var m = new EmailMessage();
            Assert.Equal("(sans objet)", OutputNaming.BuildBaseName(m, NamingPattern.SameAsSource, null));
            Assert.Equal("sans date - (sans objet)", OutputNaming.BuildBaseName(m, NamingPattern.DateSubject, null));
            Assert.Equal("sans date - expéditeur inconnu - (sans objet)", OutputNaming.BuildBaseName(m, NamingPattern.DateSenderSubject, null));
        }

        [Fact]
        public void Collisions_get_a_counter()
        {
            File.WriteAllText(Path.Combine(_dir, "a.pdf"), "x");
            File.WriteAllText(Path.Combine(_dir, "a (2).pdf"), "x");
            Assert.Equal(Path.Combine(_dir, "a (3).pdf"), OutputNaming.MakeUniquePath(_dir, "a", ".pdf"));
            Assert.Equal(Path.Combine(_dir, "b.pdf"), OutputNaming.MakeUniquePath(_dir, "b", "pdf"));
            Directory.CreateDirectory(Path.Combine(_dir, "d"));
            Assert.Equal(Path.Combine(_dir, "d (2)"), OutputNaming.MakeUniqueDirectory(_dir, "d"));
        }

        [Fact]
        public void Long_subjects_are_truncated_to_the_limit()
        {
            var m = new EmailMessage { Subject = new string('x', 400), SentOn = Fixtures.SentOn };
            m.From = new EmailAddress("Un Expéditeur Avec Un Très Long Nom Vraiment Très Long", null);
            Assert.True(OutputNaming.BuildBaseName(m, NamingPattern.DateSenderSubject, null).Length <= OutputNaming.MaxBaseNameLength + 5);
        }
    }
}
