using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
// Keeps model ratings current without rebuilding the app: model-ratings.json in the data folder
// overrides the built-in table, and can be refreshed from a JSON URL the user configures.
public sealed class ModelRatingsService(string dataFolder)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private const int MaxBytes = 1_000_000;
    public string FilePath => Path.Combine(dataFolder, "model-ratings.json");
    public DateTimeOffset? LastUpdated => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null;

    // Applies the ratings file if there is one; a broken file leaves the built-in ratings active.
    public string Load()
    {
        if (!File.Exists(FilePath))
            return ModelRatings.Apply(null);
        if (new FileInfo(FilePath).Length > MaxBytes)
            throw new InvalidOperationException("model-ratings.json is larger than 1 MB.");
        return ModelRatings.Apply(Parse(File.ReadAllText(FilePath), "file"));
    }

    public async Task<string> UpdateFromUrlAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Enter an http(s) URL of a JSON ratings file.");
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
                throw new InvalidOperationException("The ratings file is larger than 1 MB.");
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var set = Parse(text, uri.Host);
        var summary = ModelRatings.Apply(set);
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(set, Json));
        return summary;
    }

    // Writes the built-in table as an editable ratings file when none exists yet.
    public string EnsureFile()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(ModelRatings.BuiltIn(), Json));
        }

        return FilePath;
    }

    private static RatingSet Parse(string text, string fallbackSource)
    {
        RatingSet? set;
        try
        {
            set = JsonSerializer.Deserialize<RatingSet>(text, Json);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Ratings JSON is invalid: " + error.Message);
        }

        if (set?.Models == null)
            throw new InvalidOperationException("Ratings JSON needs a \"models\" array.");
        return set with
        {
            Source = string.IsNullOrWhiteSpace(set.Source) ? fallbackSource : set.Source,
            Updated = set.Updated == default ? DateTimeOffset.UtcNow : set.Updated
        };
    }
}
