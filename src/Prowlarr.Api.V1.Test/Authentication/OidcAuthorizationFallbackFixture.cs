using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Prowlarr.Http.Authentication;

namespace NzbDrone.Api.V1.Test.Authentication
{
    [TestFixture]
    public class OidcAuthorizationFallbackFixture : TestBase
    {
        private static bool InvokeTryAuthorizeWithGroups(IConfigFileProvider configFileProvider, ClaimsPrincipal principal, JsonElement? user)
        {
            var method = typeof(AuthenticationBuilderExtensions).GetMethod(
                "TryAuthorizeWithGroups",
                BindingFlags.NonPublic | BindingFlags.Static);

            method.Should().NotBeNull("TryAuthorizeWithGroups should exist on AuthenticationBuilderExtensions");

            var result = method.Invoke(null, new object[] { configFileProvider, principal, user });
            return (bool)result;
        }

        private static ClaimsPrincipal PrincipalWithSubAndEmail(string sub, string email, bool emailVerified = true)
        {
            var claims = new List<Claim>
            {
                new Claim("sub", sub),
                new Claim("email", email),
                new Claim("email_verified", emailVerified ? "true" : "false")
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static ClaimsPrincipal PrincipalWithSubAndGroups(string sub, IEnumerable<string> groups)
        {
            var claims = new List<Claim> { new Claim("sub", sub) };
            foreach (var group in groups)
            {
                claims.Add(new Claim("groups", group));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        [Test]
        public void should_not_authorize_matching_identifier_when_role_mappings_configured_but_group_not_mapped()
        {
            var config = Mocker.GetMock<IConfigFileProvider>();
            config.SetupGet(c => c.OidcRoleMappings).Returns("homies=Admin");
            config.SetupGet(c => c.OidcGroupsClaim).Returns("groups");
            config.SetupGet(c => c.OidcUserIdentifier).Returns("user@example.com");

            // Identifier matches configured allowlist, but groups claim is empty/unmapped.
            var principal = PrincipalWithSubAndEmail("sub-123", "user@example.com");

            var result = InvokeTryAuthorizeWithGroups(config.Object, principal, null);

            result.Should().BeFalse();
            principal.HasClaim(c => c.Type == "prowlarr_role" && c.Value == "Admin").Should().BeFalse();

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_authorize_matching_identifier_when_role_mappings_are_not_configured()
        {
            var config = Mocker.GetMock<IConfigFileProvider>();
            config.SetupGet(c => c.OidcRoleMappings).Returns((string)null);
            config.SetupGet(c => c.OidcGroupsClaim).Returns("groups");
            config.SetupGet(c => c.OidcUserIdentifier).Returns("user@example.com");

            var principal = PrincipalWithSubAndEmail("sub-123", "user@example.com");

            var result = InvokeTryAuthorizeWithGroups(config.Object, principal, null);

            result.Should().BeTrue();
            principal.HasClaim(c => c.Type == "prowlarr_role" && c.Value == "Admin").Should().BeTrue();
        }

        [Test]
        public void should_authorize_when_role_mappings_configured_and_group_matches()
        {
            var config = Mocker.GetMock<IConfigFileProvider>();
            config.SetupGet(c => c.OidcRoleMappings).Returns("homies=Admin");
            config.SetupGet(c => c.OidcGroupsClaim).Returns("groups");
            config.SetupGet(c => c.OidcUserIdentifier).Returns("user@example.com");

            var principal = PrincipalWithSubAndGroups("sub-123", new[] { "homies" });

            var result = InvokeTryAuthorizeWithGroups(config.Object, principal, null);

            result.Should().BeTrue();
            principal.HasClaim(c => c.Type == "prowlarr_role" && c.Value == "Admin").Should().BeTrue();
        }
    }
}
