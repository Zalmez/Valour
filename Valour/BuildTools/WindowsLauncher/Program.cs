using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Valour.WindowsLauncher;

internal static class Program
{
    private static readonly byte[] PayloadMarker = Encoding.ASCII.GetBytes("VALOURP1");

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

            var payloadPath = Path.Combine(launcherRoot, "payload.zip");
            var payloadHash = ExtractPayloadToArchive(launcherPath, payloadPath);
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
}
