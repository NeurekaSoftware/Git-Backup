//! `url` scalar-or-sequence deserialization ← `Yaml/ScalarOrSequenceConverter`.
//!
//! Lets a config key accept a single value (`url: https://…`) or a list (`url:` + items) without a
//! second key. Only used for the `url` field, so no other list field's parsing is affected.

use serde::de::{Deserializer, SeqAccess, Visitor};
use std::fmt;

/// Deserializes a scalar string or a sequence of strings into `Some(Vec<String>)`. Applied via
/// `#[serde(default, deserialize_with = "…")]`, so an absent key stays `None` (serde only invokes
/// this when the key is present).
pub fn string_or_seq_opt<'de, D>(deserializer: D) -> Result<Option<Vec<String>>, D::Error>
where
    D: Deserializer<'de>,
{
    struct StringOrSeq;

    impl<'de> Visitor<'de> for StringOrSeq {
        type Value = Vec<String>;

        fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
            formatter.write_str("a string or a sequence of strings")
        }

        fn visit_str<E>(self, value: &str) -> Result<Self::Value, E> {
            Ok(vec![value.to_string()])
        }

        fn visit_seq<A>(self, mut seq: A) -> Result<Self::Value, A::Error>
        where
            A: SeqAccess<'de>,
        {
            let mut values = Vec::new();
            while let Some(item) = seq.next_element::<String>()? {
                values.push(item);
            }
            Ok(values)
        }
    }

    deserializer.deserialize_any(StringOrSeq).map(Some)
}
