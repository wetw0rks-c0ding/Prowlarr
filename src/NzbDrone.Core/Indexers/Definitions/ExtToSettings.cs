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

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }
}
