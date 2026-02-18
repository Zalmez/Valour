using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Valour.WindowsLauncher;

internal static class Program
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Valour-Software/Valour/releases/latest";
    private const string ReleaseAssetName = "Valour.exe";
    private const string LatestTagFileName = "latest-release-tag.txt";
    private static readonly byte[] PayloadMarker = Encoding.ASCII.GetBytes("VALOURP1");
    private static readonly HttpClient GitHubClient = CreateGitHubClient();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var launcherPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve launcher path.");

            var launcherRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Valour",
                "Launcher");
            Directory.CreateDirectory(launcherRoot);

            var effectiveLauncherPath = ResolveLauncherPathAsync(launcherPath, launcherRoot)
                .GetAwaiter()
                .GetResult();

            var payloadPath = Path.Combine(launcherRoot, "payload.zip");
            var payloadHash = ExtractPayloadToArchive(effectiveLauncherPath, payloadPath);
            var installRoot = Path.Combine(launcherRoot, "versions");
            var installDir = Path.Combine(installRoot, payloadHash);
            var appPath = Path.Combine(installDir, "Valour.exe");

            if (!File.Exists(appPath) || !File.Exists(Path.Combine(installDir, ".payload")))
            {
                InstallPayload(payloadPath, installDir, payloadHash);
            }

            CleanupOldInstalls(installRoot, installDir);

            var psi = new ProcessStartInfo(appPath)
            {
                UseShellExecute = false
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<string> ResolveLauncherPathAsync(string currentLauncherPath, string launcherRoot)
    {
        var releaseRoot = Path.Combine(launcherRoot, "releases");
        Directory.CreateDirectory(releaseRoot);

        var latestTagPath = Path.Combine(launcherRoot, LatestTagFileName);
        var fallbackPath = GetFallbackLauncherPath(currentLauncherPath, releaseRoot, latestTagPath);

        try
        {
            var latestRelease = await FetchLatestReleaseAssetAsync().ConfigureAwait(false);
            if (latestRelease is null)
            {
                return fallbackPath;
            }

            var cachedReleasePath = GetReleaseExecutablePath(releaseRoot, latestRelease.Tag);
            if (File.Exists(cachedReleasePath) && HasEmbeddedPayloadTrailer(cachedReleasePath))
            {
                TryWriteLatestTag(latestTagPath, latestRelease.Tag);
                CleanupOldReleaseCaches(releaseRoot, cachedReleasePath);
                return cachedReleasePath;
            }

            var downloadedPath = await DownloadReleaseExecutableAsync(latestRelease, cachedReleasePath).ConfigureAwait(false);
            TryWriteLatestTag(latestTagPath, latestRelease.Tag);
            CleanupOldReleaseCaches(releaseRoot, downloadedPath);
            return downloadedPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return fallbackPath;
        }
    }

    private static string GetFallbackLauncherPath(string currentLauncherPath, string releaseRoot, string latestTagPath)
    {
        if (HasEmbeddedPayloadTrailer(currentLauncherPath))
        {
            return currentLauncherPath;
        }

        var latestTag = TryReadLatestTag(latestTagPath);
        if (string.IsNullOrWhiteSpace(latestTag))
        {
            return currentLauncherPath;
        }

        var cachedPath = GetReleaseExecutablePath(releaseRoot, latestTag);
        return File.Exists(cachedPath) && HasEmbeddedPayloadTrailer(cachedPath)
            ? cachedPath
            : currentLauncherPath;
    }

    private static async Task<GitHubReleaseAsset?> FetchLatestReleaseAssetAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        using var response = await GitHubClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"GitHub latest release request returned {(int)response.StatusCode}.");
            return null;
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(contentStream).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagElement))
        {
            return null;
        }

        var tag = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (!string.Equals(name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }

            var downloadUrl = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            return new GitHubReleaseAsset(tag, downloadUrl);
        }

        return null;
    }

    private static async Task<string> DownloadReleaseExecutableAsync(GitHubReleaseAsset releaseAsset, string destinationPath)
    {
        var destinationDir = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Release destination directory is invalid.");

        Directory.CreateDirectory(destinationDir);

        var tempPath = destinationPath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releaseAsset.DownloadUrl);
            using var response = await GitHubClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var downloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var destinationStream = File.Create(tempPath))
            {
                await downloadStream.CopyToAsync(destinationStream).ConfigureAwait(false);
            }

            if (!HasEmbeddedPayloadTrailer(tempPath))
            {
                throw new InvalidDataException("Downloaded release asset does not contain a valid launcher payload.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore temporary cleanup failures.
                }
            }
        }
    }

    private static string GetReleaseExecutablePath(string releaseRoot, string tag)
    {
        return Path.Combine(releaseRoot, SanitizePathSegment(tag), ReleaseAssetName);
    }

    private static void CleanupOldReleaseCaches(string releaseRoot, string currentReleaseExecutablePath)
    {
        if (!Directory.Exists(releaseRoot))
        {
            return;
        }

        var currentReleaseDir = Path.GetDirectoryName(currentReleaseExecutablePath);
        if (string.IsNullOrWhiteSpace(currentReleaseDir))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(releaseRoot))
        {
            if (string.Equals(dir, currentReleaseDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures (usually locked files from active process).
            }
        }
    }

    private static string? TryReadLatestTag(string latestTagPath)
    {
        try
        {
            if (!File.Exists(latestTagPath))
            {
                return null;
            }

            var value = File.ReadAllText(latestTagPath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteLatestTag(string latestTagPath, string tag)
    {
        try
        {
            File.WriteAllText(latestTagPath, tag.Trim());
        }
        catch
        {
            // Ignore state write failures.
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static bool HasEmbeddedPayloadTrailer(string launcherPath)
    {
        try
        {
            using var launcherStream = File.Open(launcherPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var trailerLength = sizeof(long) + PayloadMarker.Length;
            if (launcherStream.Length <= trailerLength)
            {
                return false;
            }

            var markerBuffer = new byte[PayloadMarker.Length];
            launcherStream.Seek(-PayloadMarker.Length, SeekOrigin.End);
            ReadExactly(launcherStream, markerBuffer);
            if (!markerBuffer.AsSpan().SequenceEqual(PayloadMarker))
            {
                return false;
            }

            var lengthBuffer = new byte[sizeof(long)];
            launcherStream.Seek(-trailerLength, SeekOrigin.End);
            ReadExactly(launcherStream, lengthBuffer);
            var payloadLength = BitConverter.ToInt64(lengthBuffer, 0);
            return payloadLength > 0 && payloadLength <= launcherStream.Length - trailerLength;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ValourLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string ExtractPayloadToArchive(string launcherPath, string payloadOutputPath)
    {
        using var launcherStream = File.OpenRead(launcherPath);
        var trailerLength = sizeof(long) + PayloadMarker.Length;
        if (launcherStream.Length <= trailerLength)
        {
            throw new InvalidDataException("Launcher payload trailer is missing.");
        }

        var markerBuffer = new byte[PayloadMarker.Length];
        launcherStream.Seek(-PayloadMarker.Length, SeekOrigin.End);
        ReadExactly(launcherStream, markerBuffer);
        if (!markerBuffer.AsSpan().SequenceEqual(PayloadMarker))
        {
            throw new InvalidDataException("Launcher payload marker not found.");
        }

        var lengthBuffer = new byte[sizeof(long)];
        launcherStream.Seek(-trailerLength, SeekOrigin.End);
        ReadExactly(launcherStream, lengthBuffer);
        var payloadLength = BitConverter.ToInt64(lengthBuffer, 0);
        if (payloadLength <= 0 || payloadLength > launcherStream.Length - trailerLength)
        {
            throw new InvalidDataException("Launcher payload length is invalid.");
        }

        var payloadStart = launcherStream.Length - trailerLength - payloadLength;
        launcherStream.Seek(payloadStart, SeekOrigin.Begin);

        using var payloadStream = File.Create(payloadOutputPath);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long remaining = payloadLength;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = launcherStream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of payload data.");
            }

            payloadStream.Write(buffer, 0, read);
            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static void InstallPayload(string payloadPath, string installDir, string payloadHash)
    {
        var tempDir = installDir + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(payloadPath, tempDir, overwriteFiles: true);
            File.WriteAllText(Path.Combine(tempDir, ".payload"), payloadHash);

            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            Directory.Move(tempDir, installDir);
        }
        catch
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            throw;
        }
    }

    private static void CleanupOldInstalls(string installRoot, string currentInstallDir)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(installRoot))
        {
            if (string.Equals(dir, currentInstallDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures (usually locked files from active process).
            }
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            offset += read;
        }
    }

    private sealed record GitHubReleaseAsset(string Tag, string DownloadUrl);
}
