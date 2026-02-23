using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Valour.WindowsLauncher;

internal static class Program
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Valour-Software/Valour/releases/latest";
    private const string ReleaseAssetName = "Valour-full.zip";
    private const string ReleaseExecutableName = "Valour-full.exe";
    private const string LatestTagFileName = "latest-release-tag.txt";
    private static readonly byte[] PayloadMarker = Encoding.ASCII.GetBytes("VALOURP1");
    private static readonly HttpClient GitHubClient = CreateGitHubClient();

    [STAThread]
    private static int Main(string[] args)
    {
        var exitCode = 1;

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var statusWindow = new LauncherStatusWindow();
            statusWindow.Shown += (_, _) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        exitCode = await RunLauncherAsync(args, statusWindow).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        statusWindow.SetStatus(GetFailureMessage(ex));
                        await Task.Delay(1400).ConfigureAwait(false);
                        exitCode = 1;
                    }
                    finally
                    {
                        statusWindow.SafeClose();
                    }
                });
            };

            Application.Run(statusWindow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            exitCode = 1;
        }

        return exitCode;
    }

    private static async Task<int> RunLauncherAsync(string[] args, LauncherStatusWindow statusWindow)
    {
        var launcherPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve launcher path.");

        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Valour",
            "Launcher");
        Directory.CreateDirectory(launcherRoot);

        statusWindow.SetStatus("Checking for updates...");
        var effectiveLauncherPath = await ResolveLauncherPathAsync(launcherPath, launcherRoot, statusWindow)
            .ConfigureAwait(false);

        var payloadPath = Path.Combine(launcherRoot, "payload.zip");
        statusWindow.SetStatus("Preparing application files...", 0);

        var payloadHash = await Task.Run(
                () => ExtractPayloadToArchive(
                    effectiveLauncherPath,
                    payloadPath,
                    percent => statusWindow.SetStatus("Preparing application files...", percent)))
            .ConfigureAwait(false);

        var installRoot = Path.Combine(launcherRoot, "versions");
        var installDir = Path.Combine(installRoot, payloadHash);
        var appPath = Path.Combine(installDir, "Valour.exe");

        if (!File.Exists(appPath) || !File.Exists(Path.Combine(installDir, ".payload")))
        {
            statusWindow.SetStatus("Installing update...");
            await Task.Run(() => InstallPayload(payloadPath, installDir, payloadHash)).ConfigureAwait(false);
        }

        CleanupOldInstalls(installRoot, installDir);

        statusWindow.SetStatus("Launching Valour...");
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

    private static async Task<string> ResolveLauncherPathAsync(
        string currentLauncherPath,
        string launcherRoot,
        LauncherStatusWindow? statusWindow)
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
                if (HasEmbeddedPayloadTrailer(fallbackPath))
                {
                    statusWindow?.SetStatus("Could not check updates. Launching cached version.");
                    return fallbackPath;
                }

                throw new InvalidOperationException("No runnable local version is available.");
            }

            var cachedReleasePath = GetReleaseExecutablePath(releaseRoot, latestRelease.Tag);
            if (File.Exists(cachedReleasePath) && HasEmbeddedPayloadTrailer(cachedReleasePath))
            {
                TryWriteLatestTag(latestTagPath, latestRelease.Tag);
                CleanupOldReleaseCaches(releaseRoot, cachedReleasePath);
                statusWindow?.SetStatus("Already up to date.", 100);
                return cachedReleasePath;
            }

            if (HasEmbeddedPayloadTrailer(currentLauncherPath) && IsLauncherVersionMatch(currentLauncherPath, latestRelease.Tag))
            {
                var seededReleasePath = SeedReleaseCacheFromCurrent(currentLauncherPath, cachedReleasePath);
                TryWriteLatestTag(latestTagPath, latestRelease.Tag);
                CleanupOldReleaseCaches(releaseRoot, seededReleasePath);
                statusWindow?.SetStatus("Local version matches latest. Skipping download.", 100);
                return seededReleasePath;
            }

            statusWindow?.SetStatus("Downloading update...", 0);
            var downloadedPath = await DownloadReleaseExecutableAsync(latestRelease, cachedReleasePath, statusWindow)
                .ConfigureAwait(false);
            TryWriteLatestTag(latestTagPath, latestRelease.Tag);
            CleanupOldReleaseCaches(releaseRoot, downloadedPath);
            return downloadedPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (HasEmbeddedPayloadTrailer(fallbackPath))
            {
                statusWindow?.SetStatus("Using fallback build.");
                return fallbackPath;
            }

            throw;
        }
    }

    private static bool IsLauncherVersionMatch(string launcherPath, string releaseTag)
    {
        var launcherVersion = TryReadLauncherVersion(launcherPath);
        if (string.IsNullOrWhiteSpace(launcherVersion))
        {
            return false;
        }

        var normalizedLocal = NormalizeVersion(launcherVersion);
        var normalizedRemote = NormalizeVersion(releaseTag);

        return !string.IsNullOrWhiteSpace(normalizedLocal) &&
               !string.IsNullOrWhiteSpace(normalizedRemote) &&
               string.Equals(normalizedLocal, normalizedRemote, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadLauncherVersion(string launcherPath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(launcherPath);
            return FirstNonEmpty(versionInfo.ProductVersion, versionInfo.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        var separatorIndex = trimmed.IndexOfAny(new[] { '+', '-', ' ' });
        if (separatorIndex > 0)
        {
            trimmed = trimmed[..separatorIndex];
        }

        if (Version.TryParse(trimmed, out var parsed))
        {
            var parts = new List<int> { parsed.Major, parsed.Minor };
            if (parsed.Build >= 0)
            {
                parts.Add(parsed.Build);
            }

            if (parsed.Revision >= 0)
            {
                parts.Add(parsed.Revision);
            }

            return string.Join('.', parts);
        }

        return trimmed;
    }

    private static string GetFailureMessage(Exception ex)
    {
        if (ex is InvalidDataException)
        {
            return "Downloaded update is invalid.";
        }

        if (ex is InvalidOperationException op && !string.IsNullOrWhiteSpace(op.Message))
        {
            return op.Message;
        }

        return "Failed to start Valour.";
    }

    private static string SeedReleaseCacheFromCurrent(string currentLauncherPath, string cachedReleasePath)
    {
        try
        {
            var releaseDir = Path.GetDirectoryName(cachedReleasePath)
                ?? throw new InvalidOperationException("Release cache path is invalid.");
            Directory.CreateDirectory(releaseDir);

            File.Copy(currentLauncherPath, cachedReleasePath, overwrite: true);
            return HasEmbeddedPayloadTrailer(cachedReleasePath)
                ? cachedReleasePath
                : currentLauncherPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return currentLauncherPath;
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

            return new GitHubReleaseAsset(tag, name, downloadUrl);
        }

        return null;
    }

    private static async Task<string> DownloadReleaseExecutableAsync(
        GitHubReleaseAsset releaseAsset,
        string destinationPath,
        LauncherStatusWindow? statusWindow)
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

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is null or <= 0)
            {
                statusWindow?.SetStatus("Downloading update...");
            }

            await using (var downloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var destinationStream = File.Create(tempPath))
            {
                var buffer = new byte[1024 * 128];
                long downloaded = 0;
                var lastPercent = -1;

                while (true)
                {
                    var read = await downloadStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await destinationStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    downloaded += read;

                    if (totalBytes is > 0)
                    {
                        var percent = (int)(downloaded * 100 / totalBytes.Value);
                        percent = Math.Clamp(percent, 0, 100);
                        if (percent != lastPercent)
                        {
                            statusWindow?.SetStatus("Downloading update...", percent);
                            lastPercent = percent;
                        }
                    }
                }
            }

            statusWindow?.SetStatus("Verifying update...");
            var extractedExecutablePath = tempPath + ".exe";
            if (releaseAsset.IsZipAsset)
            {
                ExtractReleaseExecutableFromArchive(tempPath, extractedExecutablePath);
            }
            else
            {
                File.Move(tempPath, extractedExecutablePath, overwrite: true);
            }

            if (!HasEmbeddedPayloadTrailer(extractedExecutablePath))
            {
                throw new InvalidDataException("Downloaded release asset does not contain a valid launcher payload.");
            }

            File.Move(extractedExecutablePath, destinationPath, overwrite: true);
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

            var extractedExecutablePath = tempPath + ".exe";
            if (File.Exists(extractedExecutablePath))
            {
                try
                {
                    File.Delete(extractedExecutablePath);
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
        return Path.Combine(releaseRoot, SanitizePathSegment(tag), ReleaseExecutableName);
    }

    private static void ExtractReleaseExecutableFromArchive(string archivePath, string destinationPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        ZipArchiveEntry? executableEntry = null;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (string.Equals(entry.Name, ReleaseExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                executableEntry = entry;
                break;
            }
        }

        if (executableEntry is null)
        {
            throw new InvalidDataException("Downloaded release archive does not contain a launcher executable.");
        }

        var destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        using var entryStream = executableEntry.Open();
        using var destinationStream = File.Create(destinationPath);
        entryStream.CopyTo(destinationStream);
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

        var normalizedReleaseRoot = NormalizePath(releaseRoot);
        var normalizedCurrentDir = NormalizePath(currentReleaseDir);
        if (!IsPathWithinRoot(normalizedCurrentDir, normalizedReleaseRoot))
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

    private static string NormalizePath(string path)
    {
        return Path
            .GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathWithinRoot(string candidatePath, string rootPath)
    {
        return string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    private static string ExtractPayloadToArchive(string launcherPath, string payloadOutputPath, Action<int>? progress)
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
        var lastPercent = -1;

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

            var extracted = payloadLength - remaining;
            var percent = (int)(extracted * 100 / payloadLength);
            percent = Math.Clamp(percent, 0, 100);
            if (percent != lastPercent)
            {
                progress?.Invoke(percent);
                lastPercent = percent;
            }
        }

        progress?.Invoke(100);
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

    private sealed record GitHubReleaseAsset(string Tag, string Name, string DownloadUrl)
    {
        public bool IsZipAsset => Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }
}
