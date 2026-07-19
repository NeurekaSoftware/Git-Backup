//! Shared metadata JSON mappers ← the comment + attachment-scan helpers on `ProviderHttpClientBase`.

use super::json;
use super::models::{BackedUpAttachment, BackedUpComment};
use regex::Regex;
use serde_json::Value;
use std::collections::HashSet;

/// Maps a comment, reading the author from `author_object.author_property`, optionally reading a
/// `system` flag ← `MapComment`.
pub fn map_comment(
    item: &Value,
    author_object: &str,
    author_property: &str,
    read_system_flag: bool,
) -> Option<BackedUpComment> {
    let body = json::get_str(item, "body");
    let author = json::get_nested_str(item, author_object, author_property);
    let body_blank = body.is_none_or(|b| b.trim().is_empty());
    let author_blank = author.is_none_or(|a| a.trim().is_empty());
    if body_blank && author_blank {
        return None;
    }

    Some(BackedUpComment {
        id: json::get_i64(item, "id"),
        author: author.map(str::to_string),
        body: body.map(str::to_string),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        updated_at: json::get_str(item, "updated_at").map(str::to_string),
        system: read_system_flag && json::get_bool(item, "system"),
    })
}

/// Scans an item body and its comment bodies for attachment references matching `pattern`, builds each
/// via `build`, and dedupes by `original_path` (first wins) ← `ScanBodyAndComments`.
pub fn scan_body_and_comments(
    body: Option<&str>,
    comments: &[BackedUpComment],
    pattern: &Regex,
    build: impl Fn(&regex::Captures) -> Option<BackedUpAttachment>,
) -> Vec<BackedUpAttachment> {
    let mut seen = HashSet::new();
    let mut attachments = Vec::new();

    let mut scan = |text: Option<&str>| {
        let Some(text) = text else {
            return;
        };
        for captures in pattern.captures_iter(text) {
            if let Some(attachment) = build(&captures) {
                if seen.insert(attachment.original_path.clone()) {
                    attachments.push(attachment);
                }
            }
        }
    };

    scan(body);
    for comment in comments {
        scan(comment.body.as_deref());
    }
    attachments
}
