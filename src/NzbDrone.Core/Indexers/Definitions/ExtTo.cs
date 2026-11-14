using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Definitions
{
    public class ExtTo : TorrentIndexerBase<ExtToSettings>
    {
        public override string Name => "Ext.to";
        public override string[] IndexerUrls => new[] { "https://ext.to/" };
        public override string Description => "Ext.to is a Public torrent indexer";
        public override string Language => "en-US";
        public override Encoding Encoding => Encoding.UTF8;
        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override IndexerPrivacy Privacy => IndexerPrivacy.Public;
        public override bool SupportsPagination => true;
        public override int PageSize => 50;
        public override IndexerCapabilities Capabilities => SetCapabilities();

        private static readonly Regex SearchPageTokenRegex = new(@"window\.searchPageToken\s*=\s*'([^']+)'", RegexOptions.Compiled);
        private static readonly Regex CsrfTokenRegex = new(@"<meta\s+name=""csrf-token""\s+content=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TorrentIdRegex = new(@"-(\d+)/?$", RegexOptions.Compiled);

        public ExtTo(IIndexerHttpClient httpClient,
                     IEventAggregator eventAggregator,
                     IIndexerStatusService indexerStatusService,
                     IConfigService configService,
                     Logger logger)
            : base(httpClient, eventAggregator, indexerStatusService, configService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new ExtToRequestGenerator(Settings, Capabilities);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new ExtToParser(Settings, Capabilities.Categories);
        }

        protected override async Task<IndexerQueryResult> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
        {
            var response = await FetchIndexerResponse(request);
            var releases = new List<ReleaseInfo>();

            try
            {
                releases = parser.ParseResponse(response).ToList();
            }
            catch (Exception ex)
            {
                ex.WithData(response.HttpResponse, 128 * 1024);
                _logger.Trace("Unexpected Response content ({0} bytes): {1}", response.HttpResponse.ResponseData.Length, response.HttpResponse.Content);
                throw;
            }

            if (releases.Count == 0)
            {
                _logger.Trace("No releases found. Response: {0}", response.Content);

                return new IndexerQueryResult
                {
                    Releases = releases,
                    Response = response.HttpResponse
                };
            }

            await EnrichMagnets(request, response, releases);

            return new IndexerQueryResult
            {
                Releases = releases,
                Response = response.HttpResponse
            };
        }

        private async Task EnrichMagnets(IndexerRequest request, IndexerResponse response, List<ReleaseInfo> releases)
        {
            var content = response.Content ?? string.Empty;
            var csrfMatch = CsrfTokenRegex.Match(content);
            var tokenMatch = SearchPageTokenRegex.Match(content);

            if (!csrfMatch.Success || !tokenMatch.Success)
            {
                _logger.Warn("{0}: searchPageToken or csrf-token missing from search page; releases returned without magnets. Assign a FlareSolverr indexer proxy if the site is Cloudflare-protected.", Definition.Name);
                return;
            }

            var csrf = csrfMatch.Groups[1].Value;
            var searchPageToken = tokenMatch.Groups[1].Value;

            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Cookie cookie in response.HttpResponse.Cookies)
            {
                cookies[cookie.Name] = cookie.Value;
            }

            if (request.HttpRequest.Cookies != null)
            {
                foreach (var kv in request.HttpRequest.Cookies)
                {
                    if (!cookies.ContainsKey(kv.Key))
                    {
                        cookies[kv.Key] = kv.Value;
                    }
                }
            }

            foreach (var release in releases.OfType<TorrentInfo>())
            {
                var torrentId = GetTorrentId(release);
                if (torrentId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    var magnet = await FetchMagnet(request, cookies, csrf, searchPageToken, torrentId);
                    if (magnet.IsNotNullOrWhiteSpace())
                    {
                        release.MagnetUrl = magnet;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "{0}: failed to fetch magnet for torrent {1}", Definition.Name, torrentId);
                }
            }
        }

        private static string GetTorrentId(ReleaseInfo release)
        {
            if (release.DownloadUrl != null && release.DownloadUrl.StartsWith("extto:", StringComparison.Ordinal))
            {
                return release.DownloadUrl.Substring("extto:".Length);
            }

            var match = TorrentIdRegex.Match(release.InfoUrl ?? string.Empty);
            return match.Success ? match.Groups[1].Value : null;
        }

        private async Task<string> FetchMagnet(IndexerRequest searchRequest, IDictionary<string, string> cookies, string csrf, string searchPageToken, string torrentId)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var hmac = ComputeHmac(torrentId, timestamp, searchPageToken);
            var baseUrl = Settings.BaseUrl.TrimEnd('/');
            var ajaxUrl = baseUrl + "/ajax/getSearchMagnet.php";

            var requestBuilder = new HttpRequestBuilder(ajaxUrl)
                .Post()
                .SetHeader("X-Requested-With", "XMLHttpRequest")
                .SetHeader("Origin", baseUrl)
                .SetHeader("Referer", searchRequest.Url.FullUri)
                .Accept(HttpAccept.Json);

            requestBuilder.SetCookies(cookies);

            var request = requestBuilder.Build();
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            request.AllowAutoRedirect = true;
            request.Method = HttpMethod.Post;
            request.SetContent($"torrent_id={Uri.EscapeDataString(torrentId)}&hash=&name=&timestamp={timestamp}&hmac={Uri.EscapeDataString(hmac)}&sessid={Uri.EscapeDataString(csrf)}");
            request.Headers.ContentType = "application/x-www-form-urlencoded";

            var response = await _httpClient.ExecuteProxiedAsync(request, Definition);

            if (response.StatusCode != HttpStatusCode.OK || response.Content.IsNullOrWhiteSpace())
            {
                return null;
            }

            var json = STJson.Deserialize<ExtToMagnetResponse>(response.ResponseData);
            if (json != null && json.Success && json.Url.IsNotNullOrWhiteSpace())
            {
                return json.Url;
            }

            _logger.Debug("{0}: magnet POST for {1} returned unsuccessful response", Definition.Name, torrentId);
            return null;
        }

        public static string ComputeHmac(string torrentId, long timestamp, string searchPageToken)
        {
            var data = $"{torrentId}|{timestamp}|{searchPageToken}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            var sb = new StringBuilder(hash.Length * 2);

            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private IndexerCapabilities SetCapabilities()
        {
            var caps = new IndexerCapabilities
            {
                TvSearchParams = new List<TvSearchParam>
                {
                    TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep, TvSearchParam.ImdbId
                },
                MovieSearchParams = new List<MovieSearchParam>
                {
                    MovieSearchParam.Q, MovieSearchParam.ImdbId
                },
                MusicSearchParams = new List<MusicSearchParam>
                {
                    MusicSearchParam.Q
                },
                BookSearchParams = new List<BookSearchParam>
                {
                    BookSearchParam.Q
                }
            };

            caps.Categories.AddCategoryMapping(1, NewznabStandardCategory.Movies, "Movies");
            caps.Categories.AddCategoryMapping(2, NewznabStandardCategory.TV, "TV");
            caps.Categories.AddCategoryMapping(3, NewznabStandardCategory.Audio, "Music");
            caps.Categories.AddCategoryMapping(4, NewznabStandardCategory.Console, "Games");
            caps.Categories.AddCategoryMapping(5, NewznabStandardCategory.PC, "Apps");
            caps.Categories.AddCategoryMapping(6, NewznabStandardCategory.Books, "Books");
            caps.Categories.AddCategoryMapping(7, NewznabStandardCategory.XXX, "XXX");
            caps.Categories.AddCategoryMapping(8, NewznabStandardCategory.Other, "Other");

            return caps;
        }

        private class ExtToMagnetResponse
        {
            public bool Success { get; set; }
            public string Url { get; set; }
            public string Hash { get; set; }
        }
    }

    public class ExtToRequestGenerator : IIndexerRequestGenerator
    {
        private readonly ExtToSettings _settings;
        private readonly IndexerCapabilities _capabilities;

        public ExtToRequestGenerator(ExtToSettings settings, IndexerCapabilities capabilities)
        {
            _settings = settings;
            _capabilities = capabilities;
        }

        public IndexerPageableRequestChain GetSearchRequests(MovieSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetPagedRequests(searchCriteria, searchCriteria.SanitizedSearchTerm, searchCriteria.FullImdbId));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(MusicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetPagedRequests(searchCriteria, searchCriteria.SanitizedSearchTerm, null));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(TvSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetPagedRequests(searchCriteria, searchCriteria.SanitizedTvSearchString, searchCriteria.FullImdbId));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(BookSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetPagedRequests(searchCriteria, searchCriteria.SanitizedSearchTerm, null));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(BasicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetPagedRequests(searchCriteria, searchCriteria.SanitizedSearchTerm, null));
            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetPagedRequests(SearchCriteriaBase searchCriteria, string term, string imdbId)
        {
            var baseUrl = _settings.BaseUrl.TrimEnd('/');
            var cats = _capabilities.Categories.MapTorznabCapsToTrackers(searchCriteria.Categories);
            var cat = cats?.FirstOrDefault();

            for (var page = 1; page <= 10; page++)
            {
                var qc = new NameValueCollection
                {
                    { "page", page.ToString(CultureInfo.InvariantCulture) },
                    { "q", term ?? string.Empty },
                    { "sort", "age" },
                    { "order", "desc" }
                };

                if (cat.IsNotNullOrWhiteSpace())
                {
                    qc.Add("cat", cat);
                }

                if (imdbId.IsNotNullOrWhiteSpace())
                {
                    qc.Add("imdb_id", imdbId);
                }

                var searchUrl = $"{baseUrl}/browse/?{qc.GetQueryString()}";
                yield return new IndexerRequest(searchUrl, HttpAccept.Html);
            }
        }

        public Func<IDictionary<string, string>> GetCookies { get; set; }
        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }
    }

    public class ExtToParser : IParseIndexerResponse
    {
        private readonly ExtToSettings _settings;
        private readonly IndexerCapabilitiesCategories _categories;

        public ExtToParser(ExtToSettings settings, IndexerCapabilitiesCategories categories)
        {
            _settings = settings;
            _categories = categories;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var releaseInfos = new List<ReleaseInfo>();

            if (indexerResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(indexerResponse, $"Unexpected response status {indexerResponse.HttpResponse.StatusCode} code from indexer request");
            }

            var parser = new HtmlParser();
            using var dom = parser.ParseDocument(indexerResponse.Content);

            var rows = dom.QuerySelectorAll("table.search-table tbody tr");

            foreach (var row in rows)
            {
                try
                {
                    var titleLink = row.QuerySelector("a.torrent-title-link");
                    if (titleLink == null)
                    {
                        continue;
                    }

                    var href = titleLink.GetAttribute("href");
                    if (href.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var infoUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? href
                        : _settings.BaseUrl.TrimEnd('/') + href;

                    var title = titleLink.TextContent?.Trim();
                    if (title.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var magnetButton = row.QuerySelector("a.search-magnet-btn");
                    var torrentId = magnetButton?.GetAttribute("data-id");
                    if (torrentId.IsNullOrWhiteSpace())
                    {
                        var m = Regex.Match(href, @"-(\d+)/?$");
                        if (m.Success)
                        {
                            torrentId = m.Groups[1].Value;
                        }
                    }

                    var size = ParseSize(row);
                    var seeders = ParseInt(row.QuerySelector("span.text-success")?.TextContent);
                    var leechers = ParseInt(row.QuerySelector("span.text-danger")?.TextContent);

                    var ageSpan = row.QuerySelectorAll("span[title]").LastOrDefault(s =>
                        s.GetAttribute("title") != null &&
                        Regex.IsMatch(s.GetAttribute("title"), @"\d{1,2}\s+\w+\s+\d{4}"));

                    var publishDate = ParseDate(ageSpan);

                    var catLinks = row.QuerySelectorAll("div.related-posted a[href]");
                    var categories = MapCategories(catLinks.Select(l => l.GetAttribute("href")).Where(h => h.IsNotNullOrWhiteSpace()).ToList());

                    if (!categories.Any())
                    {
                        categories = new List<IndexerCategory> { NewznabStandardCategory.Other };
                    }

                    var release = new TorrentInfo
                    {
                        Guid = infoUrl,
                        InfoUrl = infoUrl,
                        DownloadUrl = torrentId.IsNotNullOrWhiteSpace() ? $"extto:{torrentId}" : infoUrl,
                        Title = title,
                        Size = size,
                        Seeders = seeders,
                        Peers = (seeders ?? 0) + (leechers ?? 0),
                        PublishDate = publishDate,
                        Categories = categories,
                        DownloadVolumeFactor = 0,
                        UploadVolumeFactor = 1
                    };

                    releaseInfos.Add(release);
                }
                catch
                {
                }
            }

            return releaseInfos;
        }

        private static long? ParseSize(AngleSharp.Dom.IElement row)
        {
            foreach (var span in row.QuerySelectorAll("span"))
            {
                var text = span.TextContent?.Trim();
                if (text.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (Regex.IsMatch(text, @"^\d+(\.\d+)?\s*(KB|MB|GB|TB|B)\b", RegexOptions.IgnoreCase))
                {
                    var bytes = ParseUtil.GetBytes(text);
                    if (bytes > 0)
                    {
                        return bytes;
                    }
                }
            }

            var sizeSpan = row.QuerySelector("td span");
            return sizeSpan != null ? ParseUtil.GetBytes(sizeSpan.TextContent?.Trim()) : 0;
        }

        private static int? ParseInt(string text)
        {
            if (text.IsNullOrWhiteSpace())
            {
                return null;
            }

            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) ? val : null;
        }

        private static DateTime ParseDate(AngleSharp.Dom.IElement ageSpan)
        {
            if (ageSpan != null)
            {
                var title = ageSpan.GetAttribute("title");
                if (title.IsNotNullOrWhiteSpace() && DateTime.TryParseExact(title, "d MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    return dt.ToUniversalTime();
                }

                if (title.IsNotNullOrWhiteSpace() && DateTime.TryParse(title, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt2))
                {
                    return dt2.ToUniversalTime();
                }
            }

            return DateTime.UtcNow;
        }

        private ICollection<IndexerCategory> MapCategories(IList<string> hrefs)
        {
            var lowered = hrefs.Select(h => h.ToLowerInvariant()).ToList();

            if (lowered.Any(h => h.StartsWith("/anime")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.TVAnime };
            }

            if (lowered.Any(h => h.StartsWith("/tv")))
            {
                if (lowered.Any(h => h.Contains("uhd") || h.Contains("2160") || h.Contains("4k")))
                {
                    return new List<IndexerCategory> { NewznabStandardCategory.TVUHD };
                }

                if (lowered.Any(h => h.Contains("hd") || h.Contains("1080") || h.Contains("720")))
                {
                    return new List<IndexerCategory> { NewznabStandardCategory.TVHD };
                }

                if (lowered.Any(h => h.Contains("sd")))
                {
                    return new List<IndexerCategory> { NewznabStandardCategory.TVSD };
                }

                return new List<IndexerCategory> { NewznabStandardCategory.TV };
            }

            if (lowered.Any(h => h.StartsWith("/movies")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.Movies };
            }

            if (lowered.Any(h => h.StartsWith("/music") || h.StartsWith("/audio")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.Audio };
            }

            if (lowered.Any(h => h.StartsWith("/games")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.Console };
            }

            if (lowered.Any(h => h.StartsWith("/apps")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.PC };
            }

            if (lowered.Any(h => h.StartsWith("/books")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.Books };
            }

            if (lowered.Any(h => h.StartsWith("/xxx")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.XXX };
            }

            if (lowered.Any(h => h.StartsWith("/other")))
            {
                return new List<IndexerCategory> { NewznabStandardCategory.Other };
            }

            return new List<IndexerCategory>();
        }

        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }
    }
}
