using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers.Settings;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Definitions
{
    public class ExtToSettings : NoAuthTorrentBaseSettings
    {
        public ExtToSettings()
        {
            BaseUrl = "https://ext.to/";
        }

        [FieldDefinition(2, Label = "FlareSolverr URL", HelpText = "ext.to is Cloudflare-protected and blocks Prowlarr's normal HTTP requests. Set this to the FlareSolverr base URL (for example https://host:8191/); /v1 is appended automatically.", Advanced = true)]
        public string FlareSolverrUrl { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
