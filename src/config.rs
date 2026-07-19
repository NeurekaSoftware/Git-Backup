//! Configuration ← `Configuration/`.
//!
//! Settings model, YAML loader (unknown-key + enum + deprecated-key rejection), `${ENV}`/`*File`
//! secret resolution, `url` scalar-or-list, and `LiveSettings` hot reload (2s SHA-256 poll).

pub mod enums;
pub mod live;
pub mod loader;
pub mod model;
pub mod one_or_many;
pub mod secrets;

pub use live::LiveSettings;
pub use loader::{load, SettingsLoadResult};
pub use model::{
    ConcurrencyConfig, CredentialConfig, JobScheduleConfig, LoggingConfig, RepositoryJobConfig,
    ScheduleConfig, Settings, StorageConfig,
};
