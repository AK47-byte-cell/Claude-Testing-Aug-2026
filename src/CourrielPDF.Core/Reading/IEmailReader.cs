using CourrielPDF.Core.Model;

namespace CourrielPDF.Core.Reading
{
    public interface IEmailReader
    {
        bool CanRead(string path);
        EmailMessage Read(string path);
    }
}
