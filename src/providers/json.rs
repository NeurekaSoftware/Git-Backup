//! JSON reading helpers ← the `Get*OrNull` family on `ProviderHttpClientBase`.
//!
//! Lenient by design: a missing or wrong-typed field yields `None`/empty rather than an error, so a
//! forge that omits an optional field never aborts a walk.

use serde_json::Value;

pub fn get_str<'a>(element: &'a Value, property: &str) -> Option<&'a str> {
    element.get(property).and_then(Value::as_str)
}

/// Reads an integer that a forge may encode as a JSON number or a numeric string.
pub fn get_i64(element: &Value, property: &str) -> Option<i64> {
    match element.get(property) {
        Some(Value::Number(number)) => number.as_i64(),
        Some(Value::String(text)) => text.parse().ok(),
        _ => None,
    }
}

pub fn get_bool(element: &Value, property: &str) -> bool {
    matches!(element.get(property), Some(Value::Bool(true)))
}

/// Reads a nested string, e.g. `author.username`.
pub fn get_nested_str<'a>(element: &'a Value, object: &str, property: &str) -> Option<&'a str> {
    element
        .get(object)
        .and_then(|nested| get_str(nested, property))
}

/// Reads a label array, handling both a plain array of strings (GitLab) and an array of objects with a
/// `name` property (GitHub, Forgejo).
pub fn get_label_names(element: &Value, property: &str) -> Vec<String> {
    let Some(Value::Array(items)) = element.get(property) else {
        return Vec::new();
    };
    items
        .iter()
        .filter_map(|item| match item {
            Value::String(name) => Some(name.clone()),
            Value::Object(_) => get_str(item, "name").map(str::to_string),
            _ => None,
        })
        .filter(|name| !name.trim().is_empty())
        .collect()
}
