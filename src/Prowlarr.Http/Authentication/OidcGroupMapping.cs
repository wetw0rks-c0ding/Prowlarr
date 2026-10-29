using System;
using System.Collections.Generic;

namespace Prowlarr.Http.Authentication
{
    // Prowlarr is admin-only (no roles): any user at/above the homies tier is
    // authorized as admin. pirates (T1) is blocked, matching the Sonarr/Radarr
    // OidcGroupMapping tier gates.
    public static class OidcGroupMapping
    {
        public static readonly Dictionary<string, string> GroupToTier = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pirates"] = "T1",
            ["homies"] = "T2",
            ["family"] = "T3",
            ["maintenance"] = "T4",
            ["authentik Admins"] = "T5",
        };

        private static readonly HashSet<string> AuthorizedTiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "T2",
            "T3",
            "T4",
            "T5",
        };

        public static bool IsAuthorized(IEnumerable<string> groups)
        {
            if (groups == null)
            {
                return false;
            }

            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group))
                {
                    continue;
                }

                if (GroupToTier.TryGetValue(group, out var tier) && AuthorizedTiers.Contains(tier))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
