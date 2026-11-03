using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ByteScape.Core.Util;

namespace ByteScape.Core.Update;

/// <summary>The newest version as the update description names it.</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes, DateTime? Published, string Sha256);

public enum UpdateStatus { NewerAvailable, UpToDate, Failed, NotConfigured }

public sealed record UpdateResult(UpdateStatus Status, UpdateInfo? Info, string Message);

/// <summary>
/// Whether a newer version of the portable exe exists. Only when the user asks (the button, or the weekly check the user
/// switched on): one HTTPS request for a small description file at <see cref="Branding.UpdateUrl"/>, carrying nothing but
/// the program's name and version (User-Agent). Nothing is downloaded or run: a newer version is shown and its page opened
/// in the browser. The Store package is updated by the Store and never checks.
/// The description is either ByteScape's own (<c>{"version", "url", "notes", "published", "sha256"}</c>, see
/// <c>store/make-update-manifest.ps1</c>) or a GitHub release (<c>…/releases/latest</c> of the API).
/// </summary>
public static class UpdateCheck
{
    /// <summary>Tests: another address instead of <see cref="Branding.UpdateUrl"/>.</summary>
    internal static string? UrlOverride;
    public static string Url => UrlOverride ?? Branding.UpdateUrl;

    /// <summary>The portable exe with an address to ask; the Store package is updated by the Store.</summary>
    public static bool Available => IsSafeLink(Url) && !Config.AppConfig.IsPackaged();

    /// <summary>The weekly check (Settings) is due.</summary>
    public static bool IsDue(Config.AppConfig c, DateTime nowUtc) => Available && c.CheckForUpdates && nowUtc - c.LastUpdateCheckUtc >= Interval;

    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    /// <summary>A description is a few hundred bytes; a longer answer is not read.</summary>
    public const int MaxBytes = 64 * 1024;
    private const int MaxNotes = 2000;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
    { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static async Task<UpdateResult> CheckAsync(CancellationToken token = default, TimeSpan? timeout = null)
    {
        if (!Available) return new(UpdateStatus.NotConfigured, null, T("no update address is set"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue(Branding.Name, Branding.Version));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Failed(F($"the server answered {(int)resp.StatusCode}"));
            if (resp.Content.Headers.ContentLength is > MaxBytes) return Failed(T("the answer is too long for an update description"));
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = await s.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buf.Write(chunk, 0, n);
                if (buf.Length > MaxBytes) return Failed(T("the answer is too long for an update description"));
            }
            var info = Parse(System.Text.Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length));
            if (info == null) return Failed(T("the answer is not an update description"));
            return Compare(info, Branding.Version);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failed(T("no answer in time")); }
        catch (HttpRequestException ex) { return Failed(ex.Message); }
        catch (IOException ex) { return Failed(ex.Message); }

        static UpdateResult Failed(string why) { Log.Debug("Update check failed: " + why); return new(UpdateStatus.Failed, null, why); }
    }

    internal static UpdateResult Compare(UpdateInfo info, string current)
    {
        var mine = TryVersion(current);
        var theirs = TryVersion(info.Version);
        if (mine == null || theirs == null) return new(UpdateStatus.Failed, info, T("the version number cannot be read"));
        return theirs > mine ? new(UpdateStatus.NewerAvailable, info, "") : new(UpdateStatus.UpToDate, info, "");
    }

    /// <summary>
    /// ByteScape's own description or a GitHub release; null when it is neither, names a pre-release ("1.2.0-beta", a
    /// GitHub pre-release or draft), or its download link is not an https address (a description can never make the program
    /// open a local file or another kind of link).
    /// </summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool B(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            if (B("prerelease") || B("draft")) return null;
            var version = (S("version") ?? S("tag_name"))?.Trim();
            // a GitHub release also has "url", but that is its API address; the page people can open is "html_url"
            var url = (S("html_url") ?? S("url"))?.Trim();
            if (version == null || url == null || IsPreRelease(version) || TryVersion(version) == null || !IsSafeLink(url, allowLoopbackHttp: false)) return null;
            var notes = (S("notes") ?? S("body") ?? "").Replace("\r\n", "\n").Trim();
            if (notes.Length > MaxNotes) notes = notes[..MaxNotes].TrimEnd() + "…";
            DateTime? published = DateTime.TryParse(S("published") ?? S("published_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p) ? p : null;
            var sha = (S("sha256") ?? "").Trim();
            if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) sha = "";
            return new UpdateInfo(version.TrimStart('v', 'V'), url, notes, published, sha.ToUpperInvariant());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Only https addresses are asked and opened; plain http only on this computer (tests).</summary>
    internal static bool IsSafeLink(string url, bool allowLoopbackHttp = true)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    private static bool IsPreRelease(string v) => v.IndexOf('-') >= 0;

    /// <summary>"1.2", "v1.2.3", "1.2.3.4" → a comparable version (missing parts are 0); null otherwise.</summary>
    internal static Version? TryVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
}
