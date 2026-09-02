using System;

namespace CourrielPDF.Core.Model
{
    /// <summary>A display name plus (optionally) an SMTP address.</summary>
    public sealed class EmailAddress
    {
        public EmailAddress(string? name, string? address)
        {
            Name = Clean(name);
            Address = Clean(address);
            // Exchange sometimes yields X.500 legacy DNs instead of SMTP addresses; hide those.
            if (Address != null && (Address.StartsWith("/O=", StringComparison.OrdinalIgnoreCase) || Address.IndexOf('@') < 0))
            {
                if (Name == null) Name = Address;
                Address = null;
            }
            if (Name != null && Address != null && string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase))
                Name = null;
        }

        public string? Name { get; }
        public string? Address { get; }

        public bool IsEmpty => Name == null && Address == null;

        /// <summary>"Jean Tremblay &lt;jean@example.com&gt;", or whichever part is available.</summary>
        public string Display
        {
            get
            {
                if (Name != null && Address != null) return Name + " <" + Address + ">";
                return Name ?? Address ?? string.Empty;
            }
        }

        /// <summary>Short form for file names: the display name, else the local part of the address.</summary>
        public string ShortName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name!;
                if (Address == null) return string.Empty;
                var at = Address.IndexOf('@');
                return at > 0 ? Address.Substring(0, at) : Address;
            }
        }

        public override string ToString() => Display;

        private static string? Clean(string? s)
        {
            if (s == null) return null;
            s = s.Trim().Trim('"', '\'', '<', '>').Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
