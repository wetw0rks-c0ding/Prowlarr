using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Definitions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests.ExtToTests
{
    [TestFixture]
    public class ExtToFixture : CoreTest<ExtTo>
    {
        [SetUp]
        public void Setup()
        {
            Subject.Definition = new IndexerDefinition
            {
                Name = "ExtTo",
                Settings = new ExtToSettings()
            };
        }

        [Test]
        public async Task should_parse_search_results_and_enrich_magnets()
        {
            var search = ReadAllText(@"Files/Indexers/ExtTo/search.html");
            var magnet = ReadAllText(@"Files/Indexers/ExtTo/magnet.json");

            Mocker.GetMock<IIndexerHttpClient>()
                .Setup(o => o.ExecuteProxiedAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get), Subject.Definition))
                .ReturnsAsync((HttpRequest r, IndexerDefinition _) => new HttpResponse(r, new HttpHeader(), new CookieCollection(), search));

            Mocker.GetMock<IIndexerHttpClient>()
                .Setup(o => o.ExecuteProxiedAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Post), Subject.Definition))
                .ReturnsAsync((HttpRequest r, IndexerDefinition _) => new HttpResponse(r, new HttpHeader { { "Content-Type", "application/json" } }, new CookieCollection(), magnet));

            var releases = (await Subject.Fetch(new BasicSearchCriteria { Categories = Array.Empty<int>() })).Releases;

            releases.Should().HaveCount(2);
            releases.Should().AllBeOfType<TorrentInfo>();

            var first = (TorrentInfo)releases.First();

            first.Title.Should().Be("Dexter.Resurrection.S02E01-E03.HD720p.WEBRip.Rus");
            first.Size.Should().Be(5465345884);
            first.Seeders.Should().Be(0);
            first.Peers.Should().Be(12);
            first.InfoUrl.Should().Be("https://ext.to/dexter-resurrection-s02e01-e03-hd720p-webrip-rus-23729733/");
            first.Guid.Should().Be("https://ext.to/dexter-resurrection-s02e01-e03-hd720p-webrip-rus-23729733/");
            first.Categories.Should().Contain(c => c.Id == NewznabStandardCategory.TVHD.Id);
            first.PublishDate.Year.Should().Be(2026);
            first.PublishDate.Month.Should().Be(11);
            first.PublishDate.Day.Should().Be(14);
            first.MagnetUrl.Should().Contain("magnet:?xt=urn:btih:");

            var second = (TorrentInfo)releases.Last();
            second.Title.Should().Be("Ubuntu 24.04 Desktop amd64");
            second.Seeders.Should().Be(42);
            second.Peers.Should().Be(45);
            second.Categories.Should().Contain(c => c.Id == NewznabStandardCategory.PC.Id);
        }

        [Test]
        public void should_compute_hmac_signature()
        {
            var hmac = ExtTo.ComputeHmac("23729733", 1763136000, "d42abb6508298558d8a42fab2048df33");

            // SHA256("23729733|1763136000|d42abb6508298558d8a42fab2048df33")
            hmac.Should().Be("0e62708a7f81ebc5f7a5acf15c4cf5a2e832089e26f43357e1a359af05d26287");
        }

        [Test]
        public async Task should_return_releases_without_magnets_when_token_missing()
        {
            var search = ReadAllText(@"Files/Indexers/ExtTo/search_no_token.html");

            Mocker.GetMock<IIndexerHttpClient>()
                .Setup(o => o.ExecuteProxiedAsync(It.IsAny<HttpRequest>(), Subject.Definition))
                .ReturnsAsync((HttpRequest r, IndexerDefinition _) => new HttpResponse(r, new HttpHeader(), new CookieCollection(), search));

            var releases = (await Subject.Fetch(new BasicSearchCriteria { Categories = Array.Empty<int>() })).Releases;

            releases.Should().HaveCount(1);
            var first = (TorrentInfo)releases.First();
            first.Title.Should().Be("Dexter.Resurrection.S02E01-E03.HD720p.WEBRip.Rus");
            first.MagnetUrl.Should().BeNullOrEmpty();

            Mocker.GetMock<IIndexerHttpClient>()
                .Verify(o => o.ExecuteProxiedAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Post), Subject.Definition), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
