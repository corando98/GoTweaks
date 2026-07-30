using System.Linq;

namespace Shared.Utilities
{
    public static class StringHelper
    {
        public static string CleanStringForSerialization(string input)
        {
            if (input == null)
                return null;

            // Remove XML-invalid control characters:
            // anything < 0x20 except TAB (0x09), LF (0x0A), CR (0x0D)
            return new string(input.Where(c =>
                c == '\t' || c == '\n' || c == '\r' || c >= ' '
            ).ToArray());
        }

        /// <summary>
        /// Normalizes a game display name for use as a profile key. Some titles
        /// inject zero-width/format code points (U+200B ZERO WIDTH SPACE, U+FEFF,
        /// directional marks) into their window titles — and can vary them per
        /// launch — so a name-keyed profile silently stops matching an identical-
        /// looking name. Strips all Unicode format characters (category Cf),
        /// converts non-standard space separators to a plain space, collapses
        /// whitespace runs, and trims.
        /// </summary>
        public static string CleanGameName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.Format)
                    continue;
                sb.Append(cat == System.Globalization.UnicodeCategory.SpaceSeparator ? ' ' : c);
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        }
    }
}
