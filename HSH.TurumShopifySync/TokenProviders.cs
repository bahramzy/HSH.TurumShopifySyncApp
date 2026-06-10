using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    public sealed class TokenResponse
    {
        // Matches the JSON you receive from your token endpoint.
        public string access_token { get; set; }
        public int expires_in { get; set; } // seconds (if present)
    }

    public sealed class ShopifyTokenProvider
    {
        private readonly string _storeDomain;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly HttpClient _http = new HttpClient();
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        public ShopifyTokenProvider(string storeDomain, string clientId, string clientSecret)
        {
            _storeDomain = storeDomain ?? throw new ArgumentNullException(nameof(storeDomain));
            _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            // Prevent concurrent refreshes
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // If token exists and is not close to expiry, return it.
                if (!string.IsNullOrWhiteSpace(_accessToken) &&
                    DateTime.UtcNow < _expiresAtUtc - TimeSpan.FromMinutes(5))
                {
                    return _accessToken;
                }

                // Request new token
                var url = $"https://{_storeDomain}/admin/oauth/access_token";
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var form = new[]
                    {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", _clientId),
                    new KeyValuePair<string, string>("client_secret", _clientSecret)
                };

                    req.Content = new FormUrlEncodedContent(form);

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        TokenResponse dto = null;
                        try { dto = JsonConvert.DeserializeObject<TokenResponse>(json); } catch { /* ignore */ }

                        if (dto == null || string.IsNullOrWhiteSpace(dto.access_token))
                            throw new Exception("Failed to obtain Shopify access token.");

                        _accessToken = dto.access_token;

                        if (dto.expires_in > 0)
                            _expiresAtUtc = DateTime.UtcNow.AddSeconds(dto.expires_in);
                        else
                            // If server doesn't return expires_in, pick sensible default (23h).
                            _expiresAtUtc = DateTime.UtcNow.AddHours(23);

                        return _accessToken;
                    }
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    public sealed class TokenRefreshHandler : DelegatingHandler
    {
        private readonly ShopifyTokenProvider _provider;

        public TokenRefreshHandler(ShopifyTokenProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Ensure we have a fresh token for each outgoing request.
            var token = await _provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            // Replace any existing X-Shopify-Access-Token header (safe).
            if (request.Headers.Contains("X-Shopify-Access-Token"))
                request.Headers.Remove("X-Shopify-Access-Token");

            request.Headers.Add("X-Shopify-Access-Token", token);

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }



    // ==========================
    // Turum Token Refresh logic
    // ==========================

    public sealed class TurumTokenProvider
    {
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _http = new HttpClient();
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        public TurumTokenProvider(string username, string password)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_accessToken) &&
                    DateTime.UtcNow < _expiresAtUtc - TimeSpan.FromMinutes(5))
                {
                    return _accessToken;
                }

                var url = "https://api.b2b.turum.pl/v1/account/login";
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    var body = new
                    {
                        username = _username,
                        password = _password
                    };

                    req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        string token = null;
                        int expiresIn = 0;

                        try
                        {
                            var obj = JsonConvert.DeserializeObject<JObject>(json);
                            if (obj != null)
                            {
                                token = obj.Value<string>("token")
                                        ?? obj.Value<string>("access_token")
                                        ?? obj.Value<string>("jwt")
                                        ?? obj.Value<string>("accessToken");

                                if (string.IsNullOrWhiteSpace(token) && obj["data"] != null)
                                {
                                    var data = obj["data"] as JObject;
                                    if (data != null)
                                    {
                                        token = data.Value<string>("token")
                                            ?? data.Value<string>("access_token")
                                            ?? data.Value<string>("jwt")
                                            ?? data.Value<string>("accessToken");
                                    }
                                }

                                if (obj["expires_in"] != null)
                                    int.TryParse(obj["expires_in"].ToString(), out expiresIn);
                            }
                        }
                        catch
                        {
                            // ignore parse errors below - try raw token fallback
                        }

                        if (string.IsNullOrWhiteSpace(token))
                            token = json?.Trim().Trim('"');

                        if (string.IsNullOrWhiteSpace(token))
                            throw new Exception("Failed to obtain Turum token. Response: " + json);

                        _accessToken = token;
                        if (expiresIn > 0)
                            _expiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
                        else
                            _expiresAtUtc = DateTime.UtcNow.AddHours(23);

                        return _accessToken;
                    }
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
    }

    public sealed class TurumTokenRefreshHandler : DelegatingHandler
    {
        private readonly TurumTokenProvider _provider;

        public TurumTokenRefreshHandler(TurumTokenProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            if (request.Headers.Authorization != null)
                request.Headers.Authorization = null;

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }


}
