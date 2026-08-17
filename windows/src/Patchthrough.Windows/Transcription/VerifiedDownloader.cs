using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Patchthrough.Windows.Transcription;

internal static class VerifiedDownloader
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// One megabyte per read. Large enough that the loop costs nothing against
    /// network time, small enough that progress moves visibly on a slow link.
    /// </summary>
    private const int ChunkBytes = 1024 * 1024;

    /// <param name="progress">
    /// Reports bytes written against <paramref name="expectedBytes"/>. The first
    /// model download is around 600 MB, so a caller with a user waiting needs
    /// this to say something other than nothing for several minutes.
    /// </param>
    public static async Task<string> EnsureFileAsync(
        string directory,
        string fileName,
        Uri source,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken,
        IProgress<ModelInstallProgress>? progress = null)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        // Verifying an already-installed file hashes 600 MB, which is seconds of
        // apparent hang on first launch unless it is reported.
        progress?.Report(new ModelInstallProgress(ModelInstallPhase.Verifying, 0, expectedBytes));
        if (await IsValidAsync(destination, expectedBytes, expectedSha256, cancellationToken)) return destination;
        if (File.Exists(destination)) File.Delete(destination);

        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > expectedBytes)
        {
            File.Delete(partial);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using (var output = new FileStream(
            partial,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            // A read loop rather than CopyToAsync, so the bytes can be counted.
            // A resumed download starts from what is already on disk, or the bar
            // would restart at zero for a file that is nearly complete.
            var received = append ? existing : 0;
            progress?.Report(new ModelInstallProgress(ModelInstallPhase.Downloading, received, expectedBytes));
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new ModelInstallProgress(ModelInstallPhase.Downloading, received, expectedBytes));
            }
        }

        progress?.Report(new ModelInstallProgress(ModelInstallPhase.Verifying, expectedBytes, expectedBytes));
        if (!await IsValidAsync(partial, expectedBytes, expectedSha256, cancellationToken))
            throw new InvalidDataException($"downloaded model failed SHA-256 or size verification: {fileName}");
        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private static async Task<bool> IsValidAsync(
        string path,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedBytes) return false;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256));
    }
}
