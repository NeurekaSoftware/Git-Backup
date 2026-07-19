//! Git credential ← `GitCredential`.

#[derive(Debug, Clone)]
pub struct GitCredential {
    pub username: String,
    pub password: String,
}

impl GitCredential {
    pub fn new(username: impl Into<String>, password: impl Into<String>) -> Self {
        Self {
            username: username.into(),
            password: password.into(),
        }
    }
}
