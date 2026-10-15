using System.Net.Http.Headers;
using API.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace API.Schema.LibraryContext.LibraryConnectors;

public class Kavita(string baseUrl, string auth) : LibraryConnector(LibraryType.Kavita, baseUrl, auth)
{
    private readonly HttpClient _netClient = new HttpClient()
    {
        DefaultRequestHeaders =
        {
            Accept = { new MediaTypeWithQualityHeaderValue("application/json") }
        }
    };
    
    /// <summary>
    /// Get a new JWT Token
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ParsingException"></exception>
    private async Task<string> GetToken()
    {
        Log.Debug("Getting Token...");
        string apiKey = $"apiKey={Auth}";
        string pluginName = "pluginName=Tranga";
        string path = $"/api/Plugin/authenticate?{apiKey}&{pluginName}";

        if (await _netClient.PostAsync(BuildUri(path), null) is not { IsSuccessStatusCode: true } responseMessage)
        {
            throw new ParsingException("Could not connect to the Library instance");
        }
        
        if(JObject.Parse(await responseMessage.Content.ReadAsStringAsync()) is not { } data)
        {
            throw new ParsingException("Could not parse the response");
        }

        return data.TryGetValue("token", out JToken? token) ? token.Value<string>()! : throw new ParsingException("Could not parse the response");
    }

    /// <summary>
    /// Refreshes the JWT Token
    /// </summary>
    private async Task RefreshAuth()
    {
        Log.Debug("Refreshing auth...");
        string token = await GetToken();
        _netClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public override async Task UpdateLibrary(CancellationToken ct)
    {
        Log.Debug("Updating Libraries...");
        List<int> ids = await GetLibraries(ct);
        JObject requestData = new () { { "ids", JsonConvert.SerializeObject(ids) } };

        await RefreshAuth();
        await _netClient.PostAsJsonAsync(BuildUri("/api/Library/scan-multiple"), requestData, ct);
    }

    /// <summary>
    /// Fetches all libraries available to the user
    /// </summary>
    /// <returns>Array of KavitaLibrary</returns>
    private async Task<List<int>> GetLibraries(CancellationToken ct)
    {
        Log.Debug("Getting Libraries...");
        await RefreshAuth();
        if(await _netClient.GetStringAsync(BuildUri("/api/Library/libraries"), ct) is not { } responseData)
        {
            Log.Error("Unable to fetch libraries");
            return [];
        }

        JArray librariesJson = JArray.Parse(responseData);
        return librariesJson.SelectTokens("id").Values<int>().ToList();
    }

    /// <summary>A series in Kavita</summary>
    internal record KavitaSeries(int Id, string Name, int LibraryId);

    /// <summary>
    /// Finds the Kavita series for a manga by its titles (Kavita's name, localized name or original name must match one,
    /// ignoring case, accents and punctuation). Null if Kavita doesn't have it (yet).
    /// </summary>
    internal async Task<KavitaSeries?> FindSeries(IEnumerable<string> titles, CancellationToken ct)
    {
        string[] wanted = titles.Select(NormalizeTitle).Where(t => t.Length > 0).Distinct().ToArray();
        if (wanted.Length == 0)
            return null;
        await RefreshAuth();
        foreach (string query in titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(3))
        {
            string path = $"/api/Search/search?queryString={Uri.EscapeDataString(query)}&includeChapterAndFiles=false";
            HttpResponseMessage response = await _netClient.GetAsync(BuildUri(path), ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"Kavita search for \"{query}\" failed: {(int)response.StatusCode}");
                continue;
            }
            JObject result = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (JToken series in result["series"] as JArray ?? new JArray())
            {
                string[] names = new[] { "name", "localizedName", "originalName" }
                    .Select(k => series[k]?.Value<string>())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => NormalizeTitle(n!))
                    .ToArray();
                if (!names.Any(n => wanted.Contains(n)))
                    continue;
                int id = series["seriesId"]?.Value<int>() ?? series["id"]?.Value<int>() ?? 0;
                if (id > 0)
                    return new KavitaSeries(id, series["name"]?.Value<string>() ?? query, series["libraryId"]?.Value<int>() ?? 0);
            }
        }
        return null;
    }

    /// <summary>
    /// Sets a series' cover in Kavita (POST /api/Upload/series with the image as base64)
    /// </summary>
    /// <param name="lockCover">Stop Kavita replacing it with the first page on later scans</param>
    internal async Task<(bool ok, string message)> UploadSeriesCover(int seriesId, byte[] image, bool lockCover, CancellationToken ct)
    {
        await RefreshAuth();
        JObject body = new()
        {
            ["id"] = seriesId,
            ["url"] = Convert.ToBase64String(image),
            ["lockCover"] = lockCover
        };
        HttpResponseMessage response = await _netClient.PostAsync(BuildUri("/api/Upload/series"),
            new StringContent(body.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json"), ct);
        if (response.IsSuccessStatusCode)
            return (true, "Cover set");
        string text = await response.Content.ReadAsStringAsync(ct);
        return (false, $"Kavita answered {(int)response.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
    }

    private static string NormalizeTitle(string s) =>
        System.Text.RegularExpressions.Regex.Replace(
            new string(s.Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .ToArray()).ToLowerInvariant(),
            @"[^\p{L}\p{N}]+", " ").Trim();

    internal override async Task<bool> Test(CancellationToken ct)
    {
        Log.Debug("Testing...");
        await RefreshAuth();
        if(await _netClient.GetAsync(BuildUri("/api/Account"), ct) is not { IsSuccessStatusCode: true })
        {
            Log.Error("Unable to fetch account");
            return false;
        }

        return true;
    }
}