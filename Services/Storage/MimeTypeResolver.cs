namespace GitBackup.Services.Storage;

/// <summary>
/// Maps a file name to a best-effort content type for stored attachments. Deliberately small — it
/// covers the file kinds commonly attached to issues and merge requests and falls back to
/// <c>application/octet-stream</c> for everything else.
/// </summary>
public static class MimeTypeResolver
{
    public const string DefaultContentType = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".7z"] = "application/x-7z-compressed",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    public static string ResolveFromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var contentType))
        {
            return contentType;
        }

        return DefaultContentType;
    }
}
