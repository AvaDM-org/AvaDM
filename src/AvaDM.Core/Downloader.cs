using System.Net.Http.Headers;

namespace AvaDM.Core;

public class Downloader
{
    private const int chunkCount = 5;

    public async Task Download(Uri uri)
    {
        var directory = "/home/mazdak/projects/AvaDM/AvaDM.Console/test-dir/";

        using var client = new HttpClient();

        var msg = new HttpRequestMessage(HttpMethod.Head, uri);
        var headResponse = await client.SendAsync(msg);

        var filename = Path.GetFileName(uri.LocalPath);
        directory += filename;
        long totalSize = headResponse.Content.Headers.ContentLength
                         ?? throw new InvalidOperationException("Server did not return a Content-Length header.");
        bool supportsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");
        string? etag = headResponse.Headers.ETag?.Tag;

        if (!supportsRanges)
            throw new NotSupportedException("Server does not support range requests.");

        using var handle = File.OpenHandle(
            directory,
            mode: FileMode.Create,
            access: FileAccess.ReadWrite,
            share: FileShare.ReadWrite,
            options: FileOptions.Asynchronous,
            preallocationSize: totalSize
        );

        long bytesPerChunk = totalSize / chunkCount;

        for (var i = 0; i < chunkCount; i++)
        {
            try
            {
                Console.WriteLine("Downloading {0}...", i);
                long start = i * bytesPerChunk;
                long end = (i == chunkCount - 1) ? totalSize - 1 : start + bytesPerChunk - 1;

                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                await using var stream = await resp.Content.ReadAsStreamAsync();

                long offset = start;
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), offset);
                    offset += bytesRead;
                }
            }
            catch (AggregateException e)
            {
                var format = e.Flatten().ToString();
                Console.WriteLine(format);
            }
        }
    }
}