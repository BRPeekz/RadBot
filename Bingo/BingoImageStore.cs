namespace RadBot.Bingo;

/// <summary>Downloads bounded, signature-validated Discord images into generated local filenames.</summary>
public sealed class BingoImageStore(BingoOptions options, HttpClient httpClient)
{
    /// <summary>Maximum accepted attachment size (8 MiB).</summary>
    public const int MaximumBytes = 8 * 1024 * 1024;
    private string DirectoryPath => Path.Combine(options.DataDirectory, "images");

    /// <summary>Validates an attachment and stores it durably without using its supplied filename as a path.</summary>
    public async Task<string> SaveAsync(string url, string filename, string? contentType, long size, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        var expectedMime = extension switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null
        };
        if (expectedMime is null || contentType != expectedMime || size is <= 0 or > MaximumBytes ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            (uri.Host != "cdn.discordapp.com" && uri.Host != "media.discordapp.net"))
            throw new BingoException("Use a Discord PNG, JPEG, GIF or WebP attachment up to 8 MiB with a matching image MIME type.");

        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != expectedMime || response.Content.Headers.ContentLength > MaximumBytes)
            throw new BingoException("The attachment response is not a supported image.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bytes.Length + read > MaximumBytes) throw new BingoException("Image exceeds 8 MiB.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var data = bytes.ToArray();
        if (data.Length != size || !ValidSignature(data, extension)) throw new BingoException("Image content does not match the attachment type or size.");
        Directory.CreateDirectory(DirectoryPath);
        var name = Guid.NewGuid().ToString("N") + extension;
        var path = GetPath(name);
        var temporary = path + ".tmp";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await target.WriteAsync(data, cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(true);
            }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return name;
    }

    /// <summary>Resolves only generated filenames and rejects traversal or absolute paths from stored state.</summary>
    public string GetPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/') ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _) ||
            Path.GetExtension(name) is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
            throw new BingoException("The stored instruction image path is invalid.");
        return Path.Combine(DirectoryPath, name);
    }

    /// <summary>Checks that a durable image is still available before opening an automatic window.</summary>
    public void EnsureExists(string name)
    {
        if (!File.Exists(GetPath(name))) throw new BingoException("Instruction image is missing. Restore the event image before opening an automatic period.");
    }

    private static bool ValidSignature(byte[] data, string extension) => extension switch
    {
        ".png" => data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255,
        ".gif" => data.AsSpan().StartsWith("GIF87a"u8) || data.AsSpan().StartsWith("GIF89a"u8),
        ".webp" => data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
}
