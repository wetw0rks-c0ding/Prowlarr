using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Http.CloudFlare;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.IndexerProxies.FlareSolverr
{
    public class FlareSolverr : HttpIndexerProxyBase<FlareSolverrSettings>
    {
        private readonly IHttpProxySettingsProvider _proxySettingsProvider;
        private readonly ICached<string> _cache;

        public FlareSolverr(IHttpProxySettingsProvider proxySettingsProvider, IProwlarrCloudRequestBuilder cloudRequestBuilder, IHttpClient httpClient, Logger logger, ILocalizationService localizationService, ICacheManager cacheManager)
            : base(cloudRequestBuilder, httpClient, logger, localizationService)
        {
            _proxySettingsProvider = proxySettingsProvider;
            _cache = cacheManager.GetCache<string>(typeof(string), "UserAgent");
        }

        public override string Name => "FlareSolverr";

        public override HttpRequest PreRequest(HttpRequest request)
        {
            //Try original request first, ignore errors, detect CF in post response
            request.SuppressHttpError = true;

            //Inject UA if not present
            var cachedUserAgent = _cache.Find(request.Url.Host);

            if (cachedUserAgent.IsNotNullOrWhiteSpace() && request.Headers.UserAgent.IsNullOrWhiteSpace())
            {
                request.Headers.UserAgent = cachedUserAgent;
                _logger.Debug("FlareSolverr PreRequest: injected cached UA for {0} (prefix='{1}', length={2})", request.Url.Host, cachedUserAgent.Substring(0, Math.Min(40, cachedUserAgent.Length)), cachedUserAgent.Length);
            }
            else if (request.Headers.UserAgent.IsNotNullOrWhiteSpace())
            {
                var existingUserAgent = request.Headers.UserAgent;
                _logger.Debug("FlareSolverr PreRequest: not injecting cached UA for {0} because request.Headers.UserAgent already set (prefix='{1}', length={2})", request.Url.Host, existingUserAgent.Substring(0, Math.Min(40, existingUserAgent.Length)), existingUserAgent.Length);
            }

            return request;
        }

        public override HttpResponse PostResponse(HttpResponse response)
        {
            if (!CloudFlareDetectionService.IsCloudflareProtected(response))
            {
                _logger.Debug("CF Protection not detected, returning original response");
                return response;
            }

            var flaresolverrResponse = _httpClient.Execute(GenerateFlareSolverrRequest(response.Request));

            if (flaresolverrResponse.StatusCode != HttpStatusCode.OK && flaresolverrResponse.StatusCode != HttpStatusCode.InternalServerError)
            {
                throw new FlareSolverrException("HTTP StatusCode not 200 or 500. Status is :" + flaresolverrResponse.StatusCode);
            }

            var result = JsonConvert.DeserializeObject<FlareSolverrResponse>(flaresolverrResponse.Content);

            if (result?.Solution?.Cookies is not { Count: not 0 })
            {
                throw new FlareSolverrException("Empty cookies returned by FlareSolverr.");
            }

            var newRequest = response.Request;

            //Cache the user-agent so we can inject it in next request to avoid re-solve
            _cache.Set(response.Request.Url.Host, result.Solution.UserAgent);
            newRequest.Headers.UserAgent = result.Solution.UserAgent;

            var flaresolverrUserAgent = result.Solution.UserAgent ?? string.Empty;
            var cookieNames = result.Solution.Cookies?.Select(c => c.Name).ToArray() ?? Array.Empty<string>();
            _logger.Debug("FlareSolverr PostResponse: FlareSolverr UA (prefix='{0}', length={1}), injecting cookies [{2}] for {3}", flaresolverrUserAgent.Substring(0, Math.Min(40, flaresolverrUserAgent.Length)), flaresolverrUserAgent.Length, string.Join(", ", cookieNames), response.Request.Url.Host);

            InjectCookies(newRequest, result);

            var outgoingHeaders = newRequest.Headers.AllKeys?.Select(key =>
            {
                var value = newRequest.Headers[key] ?? string.Empty;
                if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    value = "<redacted>";
                }

                if (value.Length > 120)
                {
                    value = value.Substring(0, 120) + "...";
                }

                return $"{key}: {value}";
            }).ToArray() ?? Array.Empty<string>();

            var outgoingCookieNames = newRequest.Cookies?.Keys.ToArray() ?? Array.Empty<string>();
            _logger.Debug("FlareSolverr PostResponse: retry outgoing request method={0}, url={1}, cookieNames=[{2}], headers=[{3}]", newRequest.Method, newRequest.Url, string.Join(", ", outgoingCookieNames), string.Join(" | ", outgoingHeaders));

            //Request again with User-Agent and Cookies from Flaresolverr
            var finalResponse = _httpClient.Execute(newRequest);

            var finalIsCloudflareProtected = CloudFlareDetectionService.IsCloudflareProtected(finalResponse);
            var retryServer = finalResponse.Headers.GetSingleValue("Server") ?? string.Empty;
            var retryCfMitigated = finalResponse.Headers.GetSingleValue("cf-mitigated") ?? string.Empty;
            var retryCfRay = finalResponse.Headers.GetSingleValue("cf-ray") ?? string.Empty;
            var retryContentType = finalResponse.Headers.GetSingleValue("Content-Type") ?? string.Empty;
            var retryContentLength = finalResponse.Headers.GetSingleValue("Content-Length") ?? string.Empty;
            var retryBodyPrefix = finalResponse.Content ?? string.Empty;
            if (retryBodyPrefix.Length > 160)
            {
                retryBodyPrefix = retryBodyPrefix.Substring(0, 160);
            }

            _logger.Debug("FlareSolverr PostResponse: retry status={0}, still Cloudflare-protected={1}, server='{2}', cf-mitigated='{3}', cf-ray='{4}', content-type='{5}', content-length='{6}', body-prefix='{7}' for {8}", (int)finalResponse.StatusCode, finalIsCloudflareProtected, retryServer, retryCfMitigated, retryCfRay, retryContentType, retryContentLength, retryBodyPrefix, response.Request.Url.Host);

            return finalResponse;
        }

        private void InjectCookies(HttpRequest request, FlareSolverrResponse flareSolverrResponse)
        {
            var rCookies = flareSolverrResponse.Solution.Cookies;

            if (!rCookies.Any())
            {
                return;
            }

            var rCookiesList = rCookies.Select(x => x.Name).ToList();

            foreach (var rCookie in rCookies)
            {
                request.Cookies.Remove(rCookie.Name);
                request.Cookies.Add(rCookie.Name, rCookie.Value);
            }
        }

        private HttpRequest GenerateFlareSolverrRequest(HttpRequest request)
        {
            FlareSolverrRequest req;

            var url = request.Url.ToString();
            var maxTimeout = Settings.RequestTimeout * 1000;

            var proxySettings = _proxySettingsProvider.GetProxySettings();
            var proxyUrl = proxySettings != null ? GetProxyUri(proxySettings) : null;

            var requestProxy = new FlareSolverrProxy
            {
                Url = proxyUrl?.OriginalString,
                Username = proxySettings != null && proxySettings.Username.IsNotNullOrWhiteSpace() ? proxySettings.Username : null,
                Password = proxySettings != null && proxySettings.Password.IsNotNullOrWhiteSpace() ? proxySettings.Password : null
            };

            if (request.Method == HttpMethod.Get)
            {
                req = new FlareSolverrRequestGet
                {
                    Cmd = "request.get",
                    Url = url,
                    MaxTimeout = maxTimeout,
                    Proxy = requestProxy
                };
            }
            else if (request.Method == HttpMethod.Post)
            {
                var contentTypeType = request.Headers.ContentType.ToLower() ?? "<null>";

                if (contentTypeType.Contains("application/x-www-form-urlencoded"))
                {
                    var contentTypeValue = request.Headers.ContentType.ToString();
                    var postData = request.GetContent();

                    req = new FlareSolverrRequestPostUrlEncoded
                    {
                        Cmd = "request.post",
                        Url = url,
                        PostData = postData,
                        Headers = new HeadersPost
                        {
                            ContentType = contentTypeValue,
                            ContentLength = null
                        },
                        MaxTimeout = maxTimeout,
                        Proxy = requestProxy
                    };
                }
                else if (contentTypeType.Contains("multipart/form-data")
                         || contentTypeType.Contains("text/html"))
                {
                    //TODO Implement - check if we just need to pass the content-type with the relevant headers
                    throw new FlareSolverrException("Unimplemented POST Content-Type: " + request.Headers.ContentType);
                }
                else
                {
                    throw new FlareSolverrException("Unsupported POST Content-Type: " + request.Headers.ContentType);
                }
            }
            else
            {
                throw new FlareSolverrException("Unsupported HttpMethod: " + request.Method);
            }

            var apiUrl = string.Format("{0}/v1", Settings.Host.TrimEnd('/'));
            var newRequest = new HttpRequest(apiUrl, HttpAccept.Json);

            newRequest.Headers.ContentType = "application/json";
            newRequest.Method = HttpMethod.Post;
            newRequest.LogResponseContent = true;
            newRequest.RequestTimeout = TimeSpan.FromSeconds(Settings.RequestTimeout + 5);
            newRequest.SetContent(req.ToJson());
            newRequest.ContentSummary = req.ToJson(Formatting.None);

            _logger.Debug("Cloudflare Detected, Applying FlareSolverr Proxy {0} to request {1}", Name, request.Url);

            return newRequest;
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            var request = GenerateFlareSolverrRequest(_cloudRequestBuilder.Create()
                                  .Resource("/ping")
                                  .Build());

            try
            {
                var response = _httpClient.Execute(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Error("Proxy validation failed: {0}", response.StatusCode);
                    failures.Add(new NzbDroneValidationFailure("Host", _localizationService.GetLocalizedString("ProxyValidationBadRequest", new Dictionary<string, object> { { "statusCode", response.StatusCode } })));
                }

                _ = JsonConvert.DeserializeObject<FlareSolverrResponse>(response.Content);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Proxy validation failed");
                failures.Add(new NzbDroneValidationFailure("Host", _localizationService.GetLocalizedString("ProxyValidationUnableToConnect", new Dictionary<string, object> { { "exceptionMessage", ex.Message } })));
            }

            return new ValidationResult(failures);
        }

        private static Uri GetProxyUri(HttpProxySettings proxySettings)
        {
            return proxySettings.Type switch
            {
                ProxyType.Http => new Uri("http://" + proxySettings.Host + ":" + proxySettings.Port),
                ProxyType.Socks4 => new Uri("socks4://" + proxySettings.Host + ":" + proxySettings.Port),
                ProxyType.Socks5 => new Uri("socks5://" + proxySettings.Host + ":" + proxySettings.Port),
                _ => null
            };
        }

        private class FlareSolverrRequest
        {
            public string Cmd { get; set; }
            public string Url { get; set; }
            public IReadOnlyCollection<Cookie> Cookies { get; set; }
            public FlareSolverrProxy Proxy { get; set; }
        }

        private class FlareSolverrRequestGet : FlareSolverrRequest
        {
            public string Headers { get; set; }
            public int MaxTimeout { get; set; }
        }

        private class FlareSolverrRequestPost : FlareSolverrRequest
        {
            public string PostData { get; set; }
            public int MaxTimeout { get; set; }
        }

        private class FlareSolverrRequestPostUrlEncoded : FlareSolverrRequestPost
        {
            public HeadersPost Headers { get; set; }
        }

        private class FlareSolverrProxy
        {
            public string Url { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
        }

        private class HeadersPost
        {
            public string ContentType { get; set; }
            public string ContentLength { get; set; }
        }

        private class FlareSolverrResponse
        {
            public string Status { get; set; }
            public string Message { get; set; }
            public long StartTimestamp { get; set; }
            public long EndTimestamp { get; set; }
            public string Version { get; set; }
            public Solution Solution { get; set; }
        }

        private class Solution
        {
            public string Url { get; set; }
            public string Status { get; set; }
            public Headers Headers { get; set; }
            public string Response { get; set; }
            public IReadOnlyCollection<Cookie> Cookies { get; set; }
            public string UserAgent { get; set; }
        }

        private class Cookie
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Domain { get; set; }
            public string Path { get; set; }
            public double Expires { get; set; }
            public int Size { get; set; }
            public bool HttpOnly { get; set; }
            public bool Secure { get; set; }
            public bool Session { get; set; }
            public string SameSite { get; set; }

            public string ToHeaderValue() => $"{Name}={Value}";
            public System.Net.Cookie ToCookieObj() => new(Name, Value);
        }

        private class Headers
        {
            public string Status { get; set; }
            public string Date { get; set; }

            [JsonProperty(PropertyName = "content-type")]
            public string ContentType { get; set; }
        }
    }
}
