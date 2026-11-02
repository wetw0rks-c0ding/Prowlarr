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

        public static bool IsAuthorized(IEnumerable<string> groups, string mappings = null)
        {
            if (groups == null)
            {
                return false;
            }

            // Config-driven path: any group that maps to a role authorizes.
            if (!string.IsNullOrWhiteSpace(mappings))
            {
                var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in mappings.Split(';'))
                {
                    var kv = part.Split('=');
                    if (kv.Length != 2)
                    {
                        continue;
                    }

                    var key = kv[0].Trim();
                    var val = kv[1].Trim();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
                    {
                        continue;
                    }

                    mapped.Add(key);
                }

                foreach (var group in groups)
                {
                    if (string.IsNullOrWhiteSpace(group))
                    {
                        continue;
                    }

                    if (mapped.Contains(group.Trim()))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Legacy fallback: hardcoded tier gate (homies+).
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
