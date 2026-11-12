using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MitosScope.App.Services;

/// <summary>
/// Keeps the item icons (items.png + items.xml) in sync with the game. https://mitos.is/itemspack.php
/// returns the URL of the current items pack (a zip); when it differs from the one downloaded last
/// time, the zip is downloaded and only items.png and items.xml are extracted into
/// %APPDATA%\MitosScope\items. If anything fails, the cached copy (or the atlas embedded in the
/// executable) keeps being used.
/// Only HTTPS URLs on mitos.is are accepted.
/// </summary>
public static class ItemsPackUpdater
{
    private const string PackInfoUrl = "https://mitos.is/itemspack.php";
    private const long MaxZipBytes = 64L * 1024 * 1024;
    private static readonly string[] Files = ["items.png", "items.xml"];

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MitosScope", "items");

    private static string VersionPath => Path.Combine(DirectoryPath, "pack.url");

    /// <summary>True when an extracted items.png + items.xml are available on disk.</summary>
    public static bool HasCachedPack =>
        File.Exists(Path.Combine(DirectoryPath, "items.png")) && File.Exists(Path.Combine(DirectoryPath, "items.xml"));

    /// <summary>
    /// Checks the current pack and downloads it if it changed. Returns true when a new pack was
    /// installed (the caller should reload the icons). <paramref name="progress"/> gets what is
    /// happening (for the loader in the panels): text and fraction 0..1, or -1 while it can't be measured;
    /// null text when it's done.
    /// </summary>
    public static async Task<bool> UpdateAsync(Action<string?, double>? progress, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MitosScope");

            var url = (await http.GetStringAsync(PackInfoUrl, ct).ConfigureAwait(false)).Trim();
            if (!IsAllowed(url))
            {
                Trace.WriteLine($"[MitosScope] Items pack: unexpected URL '{url}', ignored.");
                return false;
            }

            if (HasCachedPack && File.Exists(VersionPath) && File.ReadAllText(VersionPath).Trim() == url)
                return false; // already up to date

            // from here on the panels show the loader (the version check above is quick and silent)
            progress?.Invoke("Downloading item icons…", -1);
            Directory.CreateDirectory(DirectoryPath);
            var zipPath = Path.Combine(DirectoryPath, "pack.zip.tmp");
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var length = response.Content.Headers.ContentLength;
                    if (length is > MaxZipBytes)
                        throw new InvalidDataException("items pack too large");
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = File.Create(zipPath);
                    await CopyLimitedAsync(input, output, MaxZipBytes, length, progress, ct).ConfigureAwait(false);
                }

                progress?.Invoke("Installing item icons…", -1);
                // Extract to temp files first, then swap: a broken download never replaces a working pack.
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var name in Files)
                    {
                        var entry = FindEntry(zip, name) ?? throw new InvalidDataException($"{name} missing in the items pack");
                        entry.ExtractToFile(Path.Combine(DirectoryPath, name + ".tmp"), overwrite: true);
                    }
                }
                foreach (var name in Files)
                    File.Move(Path.Combine(DirectoryPath, name + ".tmp"), Path.Combine(DirectoryPath, name), overwrite: true);
                File.WriteAllText(VersionPath, url);
                Trace.WriteLine($"[MitosScope] Items pack updated: {url}");
                return true;
            }
            finally
            {
                TryDelete(zipPath);
                foreach (var name in Files)
                    TryDelete(Path.Combine(DirectoryPath, name + ".tmp"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MitosScope] Items pack update failed, keeping the current icons: {ex.Message}");
            return false;
        }
        finally
        {
            progress?.Invoke(null, 0);
        }
    }

    // HTTPS only, and only mitos.is or its subdomains (s.mitos.is hosts the files)
    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("mitos.is", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".mitos.is", StringComparison.OrdinalIgnoreCase));

    // items.png / items.xml at the root of the zip (or in a single folder), matched by file name only
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        foreach (var entry in zip.Entries)
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && entry.FullName.Split('/').Length <= 2)
                return entry;
        return null;
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, long? length,
        Action<string?, double>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        var lastReport = -1;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidDataException("items pack too large");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            // one update per percent, not per buffer
            var percent = length is > 0 ? (int)(total * 100 / length.Value) : -1;
            if (percent != lastReport)
            {
                lastReport = percent;
                var mb = total / 1048576.0;
                progress?.Invoke(length is > 0
                    ? $"Downloading item icons… {mb:0.0} / {length.Value / 1048576.0:0.0} MB"
                    : $"Downloading item icons… {mb:0.0} MB", percent >= 0 ? percent / 100.0 : -1);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
