//! Content-type resolution ← `MimeTypeResolver`.
//!
//! Deliberately small: covers the file kinds commonly attached to issues and merge requests, and falls
//! back to `application/octet-stream` for everything else.

pub const DEFAULT_CONTENT_TYPE: &str = "application/octet-stream";

/// Best-effort content type from a file name's extension (case-insensitive).
pub fn resolve_from_file_name(file_name: &str) -> &'static str {
    let Some(extension) = file_name
        .rsplit_once('.')
        .map(|(_, ext)| ext.to_ascii_lowercase())
    else {
        return DEFAULT_CONTENT_TYPE;
    };

    match extension.as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "ico" => "image/x-icon",
        "pdf" => "application/pdf",
        "txt" | "log" => "text/plain",
        "md" => "text/markdown",
        "csv" => "text/csv",
        "json" => "application/json",
        "xml" => "application/xml",
        "yml" | "yaml" => "application/yaml",
        "zip" => "application/zip",
        "gz" => "application/gzip",
        "tar" => "application/x-tar",
        "7z" => "application/x-7z-compressed",
        "mp4" => "video/mp4",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "ppt" => "application/vnd.ms-powerpoint",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => DEFAULT_CONTENT_TYPE,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_known_and_unknown_extensions() {
        assert_eq!(resolve_from_file_name("shot.PNG"), "image/png");
        assert_eq!(resolve_from_file_name("archive.tar.gz"), "application/gzip");
        assert_eq!(
            resolve_from_file_name("report.docx"),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        );
        assert_eq!(resolve_from_file_name("noext"), DEFAULT_CONTENT_TYPE);
        assert_eq!(resolve_from_file_name("weird.qwerty"), DEFAULT_CONTENT_TYPE);
    }
}
