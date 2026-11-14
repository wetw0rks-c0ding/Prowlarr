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
            releases.Should().OnlyContain(r => r.DownloadUrl.StartsWith("https://"));
            releases.Should().NotContain(r => r.DownloadUrl.StartsWith("extto:"));

            var first = (TorrentInfo)releases.First();

            first.Title.Should().Be("Dexter.Resurrection.S02E01-E03.HD720p.WEBRip.Rus");
            first.Size.Should().Be(5465345884);
            first.Seeders.Should().Be(0);
            first.Peers.Should().Be(12);
            first.InfoUrl.Should().Be("https://ext.to/dexter-resurrection-s02e01-e03-hd720p-webrip-rus-23729733/");
            first.Guid.Should().Be("https://ext.to/dexter-resurrection-s02e01-e03-hd720p-webrip-rus-23729733/");
            first.DownloadUrl.Should().Be(first.InfoUrl);
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
            second.DownloadUrl.Should().Be(second.InfoUrl);
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
            releases.Should().OnlyContain(r => r.DownloadUrl.StartsWith("https://"));
            releases.Should().NotContain(r => r.DownloadUrl.StartsWith("extto:"));
            var first = (TorrentInfo)releases.First();
            first.Title.Should().Be("Dexter.Resurrection.S02E01-E03.HD720p.WEBRip.Rus");
            first.DownloadUrl.Should().Be(first.InfoUrl);
            first.MagnetUrl.Should().BeNullOrEmpty();

            Mocker.GetMock<IIndexerHttpClient>()
                .Verify(o => o.ExecuteProxiedAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Post), Subject.Definition), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public async Task should_use_flaresolverr_session_when_configured_and_enrich_magnets()
        {
            var search = ReadAllText(@"Files/Indexers/ExtTo/search.html");
            var magnet = ReadAllText(@"Files/Indexers/ExtTo/magnet.json");
            var createSessionResponse = @"{""status"":""ok"",""message"":""Session created"",""session"":""sess-123""}";
            var pageResponse = $@"{{
  ""status"": ""ok"",
  ""message"": ""Challenge solved"",
  ""solution"": {{
    ""url"": ""https://ext.to/browse/?page=1&q=dexter&sort=age&order=desc"",
    ""status"": 200,
    ""response"": {System.Text.Json.JsonSerializer.Serialize(search)}
  }}
}}";
            var magnetResponse = $@"{{
  ""status"": ""ok"",
  ""message"": ""Challenge solved"",
  ""solution"": {{
    ""url"": ""https://ext.to/ajax/getSearchMagnet.php"",
    ""status"": 200,
    ""response"": {System.Text.Json.JsonSerializer.Serialize(magnet)}
  }}
}}";
            var destroyResponse = @"{""status"":""ok""}";

            ((ExtToSettings)Subject.Definition.Settings).FlareSolverrUrl = "http://localhost:8191/";

            Mocker.GetMock<IIndexerHttpClient>()
                .Setup(o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.Url.FullUri == "http://localhost:8191/v1")))
                .ReturnsAsync((HttpRequest r) =>
                {
                    var content = r.GetContent() ?? string.Empty;

                    if (content.Contains("sessions.create"))
                    {
                        return new HttpResponse(r, new HttpHeader { { "Content-Type", "application/json" } }, new CookieCollection(), createSessionResponse);
                    }

                    if (content.Contains("request.get"))
                    {
                        return new HttpResponse(r, new HttpHeader { { "Content-Type", "application/json" } }, new CookieCollection(), pageResponse);
                    }

                    if (content.Contains("request.post"))
                    {
                        return new HttpResponse(r, new HttpHeader { { "Content-Type", "application/json" } }, new CookieCollection(), magnetResponse);
                    }

                    if (content.Contains("sessions.destroy"))
                    {
                        return new HttpResponse(r, new HttpHeader { { "Content-Type", "application/json" } }, new CookieCollection(), destroyResponse);
                    }

                    return new HttpResponse(r, new HttpHeader(), new CookieCollection(), "{}");
                });

            var releases = (await Subject.Fetch(new BasicSearchCriteria { Categories = Array.Empty<int>() })).Releases;

            releases.Should().HaveCount(2);
            releases.OfType<TorrentInfo>().Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.MagnetUrl) && r.MagnetUrl.Contains("magnet:?xt=urn:btih:"));

            Mocker.GetMock<IIndexerHttpClient>()
                .Verify(
                    o => o.ExecuteProxiedAsync(It.Is<HttpRequest>(v => v.Method == HttpMethod.Get), Subject.Definition),
                    Times.Never());

            Mocker.GetMock<IIndexerHttpClient>()
                .Verify(
                    o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.GetContent().Contains("sessions.create"))),
                    Times.Once());

            Mocker.GetMock<IIndexerHttpClient>()
                .Verify(
                    o => o.ExecuteAsync(It.Is<HttpRequest>(v => v.GetContent().Contains("sessions.destroy") && v.GetContent().Contains("sess-123"))),
                    Times.Once());
        }

        [Test]
        public void should_map_anime_category_and_capabilities()
        {
            Subject.Capabilities.Categories.MapTorznabCapsToTrackers(new[] { NewznabStandardCategory.TVAnime.Id })
                .Should().Contain("7");

            var animeHtml = @"<!DOCTYPE html><html><head><meta name=""csrf-token"" content=""abc""></head><body>
<table class=""table table-striped table-hover search-table""><tbody>
<tr>
  <td class=""text-left"">
    <div class=""float-left has-movie"">
      <div class=""torrent-title-line""><a href=""/some-anime-23729733/"" class=""torrent-title-link""><b>Some Anime</b></a></div>
      <div class=""related-posted"">Posted in <a href=""/anime/""><strong>Anime</strong></a></div>
    </div>
    <div class=""btn-blocks float-right""><a class=""dwn-btn search-magnet-btn"" href=""javascript:void(0);"" data-id=""23729733""></a></div>
  </td>
  <td class=""nowrap-td hide-on-mob""><div class=""add-block-wrapper""><span class=""add-block"">Size</span><span>1.5 GB</span></div></td>
  <td class=""hide-on-mob""><div class=""add-block-wrapper""><span class=""add-block"">Files</span><span>1</span></div></td>
  <td class=""nowrap-td hide-on-mob""><div class=""add-block-wrapper""><span class=""add-block"">Age</span><span title=""14 November 2026"">1 hour ago</span></div></td>
  <td class=""hide-on-mob""><div class=""add-block-wrapper""><span class=""add-block"">Seeds</span><span class=""text-success"">1</span></div></td>
  <td class=""hide-on-mob""><div class=""add-block-wrapper""><span class=""add-block"">Leechs</span><span class=""text-danger"">0</span></div></td>
  <td class=""hide-on-mob""></td>
</tr>
</tbody></table>
<script>window.searchPageToken = 'd42abb6508298558d8a42fab2048df33';</script>
</body></html>";

            Mocker.GetMock<IIndexerHttpClient>()
                .Setup(o => o.ExecuteProxiedAsync(It.IsAny<HttpRequest>(), Subject.Definition))
                .ReturnsAsync((HttpRequest r, IndexerDefinition _) => new HttpResponse(r, new HttpHeader(), new CookieCollection(), animeHtml));

            var releases = Subject.Fetch(new BasicSearchCriteria { Categories = Array.Empty<int>() }).GetAwaiter().GetResult().Releases;

            releases.Should().HaveCount(1);
            releases.Should().OnlyContain(r => r.DownloadUrl.StartsWith("https://"));
            releases.Should().NotContain(r => r.DownloadUrl.StartsWith("extto:"));
            releases.First().Categories.Should().Contain(c => c.Id == NewznabStandardCategory.TVAnime.Id);
        }
    }
}
