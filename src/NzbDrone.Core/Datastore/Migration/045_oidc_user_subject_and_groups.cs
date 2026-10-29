using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(45)]
    public class oidc_user_subject_and_groups : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("Users").AddColumn("OidcSubject").AsString().Nullable();
            Alter.Table("Users").AddColumn("OidcGroups").AsString().Nullable();

            Execute.Sql("CREATE UNIQUE INDEX \"IX_Users_OidcSubject\" ON \"Users\" (\"OidcSubject\") WHERE \"OidcSubject\" IS NOT NULL");
        }
    }
}
