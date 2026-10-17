using System.Net;
using log4net;

namespace API.MangaDownloadClients;

internal class HttpDownloadClient : IDownloadClient
{
    private static readonly HttpClient Client = new(handler: Tranga.RateLimitHandler)
    {
        Timeout = TimeSpan.FromSeconds(Constants.HttpRequestTimeout),
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        DefaultRequestHeaders = { { "User-Agent", Tranga.Settings.UserAgent } }
    };
    private static readonly FlareSolverrDownloadClient FlareSolverrDownloadClient = new(Client);
    private ILog Log { get; } = LogManager.GetLogger(typeof(HttpDownloadClient));
    
    public async Task<HttpResponseMessage> MakeRequest(string url, RequestType requestType, string? referrer = null, CancellationToken? cancellationToken = null)
    {
        Log.DebugFormat("Using {0} for {1}", typeof(HttpDownloadClient).FullName, url);
        HttpRequestMessage NewRequest()
        {
            HttpRequestMessage m = new(HttpMethod.Get, url);
            if (referrer is not null)
                m.Headers.Referrer = new (referrer);
            return m;
        }
        HttpRequestMessage requestMessage = NewRequest();
        Log.DebugFormat("Requesting {0}", url);
        
        try
        {
            HttpResponseMessage response;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    response = await Client.SendAsync(requestMessage, cancellationToken ?? CancellationToken.None);
                    break;
                }
                catch (HttpRequestException e) when (attempt < 3 && e.StatusCode is null && e.InnerException is IOException
                                                      && cancellationToken?.IsCancellationRequested != true)
                {
                    // The connection was dropped before an answer (e.g. "unexpected EOF" during TLS, "broken pipe"):
                    // usually the site shedding a burst of requests, so wait a moment and try again on a new connection
                    Log.Debug($"Connection to {requestMessage.RequestUri?.Host} dropped ({e.InnerException.Message}), retrying ({attempt}/2)");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt + Random.Shared.NextDouble() * 2), cancellationToken ?? CancellationToken.None);
                    requestMessage = NewRequest();
                }
            }
            Log.DebugFormat("Request {0} returned {1} {2}", url, (int)response.StatusCode, response.StatusCode.ToString());
            if(response.IsSuccessStatusCode)
                return response;

            if (response.Headers.Server.Any(s =>
                    (s.Product?.Name ?? "").Contains("cloudflare", StringComparison.InvariantCultureIgnoreCase)))
            {
                Log.Debug("Retrying with FlareSolverr!");
                return await FlareSolverrDownloadClient.MakeRequest(url, requestType, referrer);
            }
            
            Log.Debug($"Request returned status code {(int)response.StatusCode} {response.StatusCode}:\n" +
                      $"=====\n" +
                      $"Request:\n" +
                      $"{requestMessage.Method} {requestMessage.RequestUri}\n" +
                      $"{requestMessage.Version} {requestMessage.VersionPolicy}\n" +
                      $"Headers:\n\t{string.Join("\n\t", requestMessage.Headers.Select(h => $"{h.Key}: <{string.Join(">, <", h.Value)}"))}>\n" +
                      $"{requestMessage.Content?.ReadAsStringAsync().Result}" +
                      $"=====\n" +
                      $"Response:\n" +
                      $"{response.Version}\n" +
                      $"Headers:\n\t{string.Join("\n\t", response.Headers.Select(h => $"{h.Key}: <{string.Join(">, <", h.Value)}"))}>\n" +
                      $"{response.Content.ReadAsStringAsync().Result}");
            return new(HttpStatusCode.InternalServerError);
        }
        catch (HttpRequestException e)
        {
            Log.Error($"{requestMessage.RequestUri?.Host}: {e.Message} {e.InnerException?.Message}");
            return new(HttpStatusCode.InternalServerError);
        }
    }
}