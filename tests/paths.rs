//! Storage-key / path-parser parity tests. Every expected string was produced by running the actual
//! .NET `StorageKeyBuilder`/`RepositoryPathParser` over the same inputs (a generated oracle corpus),
//! so these assert byte-for-byte backward compatibility with existing production buckets.

use gitbackup::paths::key_builder as kb;
use gitbackup::paths::repo_path;

struct Case {
    url: &'static str,
    full_domain: &'static str,
    owner: &'static str,
    group: Option<&'static str>,
    secondary: Option<&'static str>,
    repo: &'static str,
    hierarchy: &'static str,
    provider_prefix: &'static str,
    url_prefix: &'static str,
}

const CASES: &[Case] = &[
    Case {
        url: "https://github.com/owner/repo.git",
        full_domain: "github.com",
        owner: "owner",
        group: None,
        secondary: None,
        repo: "repo",
        hierarchy: "owner|repo",
        provider_prefix: "repositories/provider/github/owner/repo",
        url_prefix: "repositories/url/github.com/owner/repo",
    },
    Case {
        url: "https://gitlab.com/group/subgroup/repo",
        full_domain: "gitlab.com",
        owner: "group",
        group: Some("subgroup"),
        secondary: None,
        repo: "repo",
        hierarchy: "group|subgroup|repo",
        provider_prefix: "repositories/provider/github/group/subgroup/repo",
        url_prefix: "repositories/url/gitlab.com/group/subgroup/repo",
    },
    Case {
        url: "https://gitlab.com/a/b/c/d/repo.git",
        full_domain: "gitlab.com",
        owner: "a",
        group: Some("b"),
        secondary: Some("c-d"),
        repo: "repo",
        hierarchy: "a|b|c-d|repo",
        provider_prefix: "repositories/provider/github/a/b/c-d/repo",
        url_prefix: "repositories/url/gitlab.com/a/b/c-d/repo",
    },
    Case {
        url: "https://Example.COM/Owner/My.Repo",
        full_domain: "example.com",
        owner: "owner",
        group: None,
        secondary: None,
        repo: "my.repo",
        hierarchy: "owner|my.repo",
        provider_prefix: "repositories/provider/github/owner/my.repo",
        url_prefix: "repositories/url/example.com/owner/my.repo",
    },
    Case {
        url: "https://code.neureka.dev/git/backup",
        full_domain: "code.neureka.dev",
        owner: "git",
        group: None,
        secondary: None,
        repo: "backup",
        hierarchy: "git|backup",
        provider_prefix: "repositories/provider/github/git/backup",
        url_prefix: "repositories/url/code.neureka.dev/git/backup",
    },
];

#[test]
fn parse_and_prefixes_match_dotnet_oracle() {
    for case in CASES {
        let info = repo_path::parse(case.url).unwrap_or_else(|e| panic!("{}: {e}", case.url));
        assert_eq!(
            info.full_domain, case.full_domain,
            "fullDomain [{}]",
            case.url
        );
        assert_eq!(info.owner, case.owner, "owner [{}]", case.url);
        assert_eq!(info.group.as_deref(), case.group, "group [{}]", case.url);
        assert_eq!(
            info.secondary_group.as_deref(),
            case.secondary,
            "secondary [{}]",
            case.url
        );
        assert_eq!(info.repository_name, case.repo, "repo [{}]", case.url);
        assert_eq!(
            info.hierarchy().join("|"),
            case.hierarchy,
            "hierarchy [{}]",
            case.url
        );

        // "GitHub" is passed with mixed case to prove the provider segment is lowercased.
        assert_eq!(
            kb::build_provider_repository_prefix("GitHub", &info),
            case.provider_prefix,
            "providerPrefix [{}]",
            case.url
        );
        assert_eq!(
            kb::build_url_repository_prefix(&info),
            case.url_prefix,
            "urlPrefix [{}]",
            case.url
        );
    }
}

#[test]
fn derived_keys_match_dotnet_oracle() {
    // The derived keys are pure formatting over a repository prefix; two representative prefixes cover
    // every shape (simple owner/repo and the deep group/secondary-group hierarchy).
    let simple = "repositories/provider/github/owner/repo";
    assert_eq!(
        kb::build_archive_object_key(simple, 1_700_000_000),
        "repositories/provider/github/owner/repo/1700000000_repo.tar.gz"
    );
    assert_eq!(
        kb::build_repository_metadata_object_key(simple),
        "repositories/provider/github/owner/repo/metadata.json"
    );
    assert_eq!(
        kb::build_issue_object_key(simple, "42"),
        "repositories/provider/github/owner/repo/issues/42.json"
    );
    assert_eq!(
        kb::build_issues_manifest_object_key(simple),
        "repositories/provider/github/owner/repo/issues/index.json"
    );
    assert_eq!(
        kb::build_issue_attachment_object_key(simple, "42", "shot.png"),
        "repositories/provider/github/owner/repo/issues/attachments/42/shot.png"
    );
    assert_eq!(
        kb::build_merge_request_object_key(simple, "7"),
        "repositories/provider/github/owner/repo/merge-requests/7.json"
    );
    assert_eq!(
        kb::build_merge_requests_manifest_object_key(simple),
        "repositories/provider/github/owner/repo/merge-requests/index.json"
    );
    assert_eq!(
        kb::build_release_object_key(simple, "v1.0.0"),
        "repositories/provider/github/owner/repo/releases/v1.0.0.json"
    );
    assert_eq!(
        kb::build_release_attachment_object_key(simple, "v1.0.0", "app.zip"),
        "repositories/provider/github/owner/repo/releases/attachments/v1.0.0/app.zip"
    );

    let deep = "repositories/provider/github/a/b/c-d/repo";
    assert_eq!(
        kb::build_archive_object_key(deep, 1_700_000_000),
        "repositories/provider/github/a/b/c-d/repo/1700000000_repo.tar.gz"
    );
    assert_eq!(
        kb::build_release_attachment_object_key(deep, "v1.0.0", "app.zip"),
        "repositories/provider/github/a/b/c-d/repo/releases/attachments/v1.0.0/app.zip"
    );
}

#[test]
fn snippet_prefixes_sanitize_hostile_identifiers() {
    // A '/' or '..' in a provider-supplied id must collapse to '-' rather than inject key segments.
    assert_eq!(
        kb::build_snippet_resource_prefix("GitLab", "abc/../def"),
        "snippets/provider/gitlab/abc-..-def"
    );
    assert_eq!(
        kb::build_nested_snippet_prefix("repositories/provider/github/owner/repo", "snip/../id"),
        "repositories/provider/github/owner/repo/snippets/snip-..-id"
    );
}

#[test]
fn archive_timestamp_and_prefix_helpers() {
    assert_eq!(
        kb::try_get_archive_timestamp("repositories/provider/github/o/r/1700000000_repo.tar.gz"),
        Some(1_700_000_000)
    );
    assert_eq!(
        kb::try_get_archive_timestamp("repositories/provider/github/o/r/metadata.json"),
        None
    );
    assert_eq!(
        kb::get_parent_prefix("repositories/provider/github/o/r/1700000000_repo.tar.gz"),
        "repositories/provider/github/o/r"
    );
    assert_eq!(kb::ensure_prefix("repositories"), "repositories/");
    assert_eq!(kb::ensure_prefix("/repositories/"), "repositories/");
    assert_eq!(kb::ensure_prefix("///"), "");
}

#[test]
fn parse_rejects_unsupported_urls() {
    assert!(repo_path::parse("ssh://git@github.com/o/r").is_err());
    assert!(repo_path::parse("https://github.com/onlyone").is_err());
    assert!(repo_path::parse("not a url").is_err());
}
