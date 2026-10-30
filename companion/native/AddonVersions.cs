using System;

namespace AurifyClient
{
    internal static class AddonVersions
    {
        private const int MaxComponent = 999999999;
        private static readonly Version MinimumProfessions = new Version(7, 0, 24, 0);
        private static readonly Version MinimumOverlord = new Version(2, 0, 4, 0);

        internal static bool TryParse(string text, out Version version)
        {
            version = null;
            if (text == null) return false;

            int major = 0, minor = 0, build = 0, revision = 0;
            int component = 0, value = 0, digits = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (character == '.')
                {
                    if (digits == 0 || component >= 3) return false;
                    if (component == 0) major = value;
                    else if (component == 1) minor = value;
                    else build = value;
                    component++;
                    value = 0;
                    digits = 0;
                    continue;
                }
                if (character < '0' || character > '9' ||
                    (digits == 1 && value == 0) ||
                    value > (MaxComponent - (character - '0')) / 10)
                    return false;
                value = value * 10 + character - '0';
                digits++;
            }
            if (digits == 0 || (component != 2 && component != 3)) return false;
            if (component == 2) build = value;
            else revision = value;
            version = new Version(major, minor, build, revision);
            return true;
        }

        internal static bool IsSupported(string key, string text)
        {
            Version version;
            if (!TryParse(text, out version)) return false;
            if (key == "professions")
                return version.Major == 7 && version.CompareTo(MinimumProfessions) >= 0;
            if (key == "overlord")
                return version.Major == 2 && version.CompareTo(MinimumOverlord) >= 0;
            return false;
        }
    }
}
