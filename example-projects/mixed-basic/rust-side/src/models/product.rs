#[derive(Debug, Clone)]
pub struct Product {
    pub id: u32,
    pub name: String,
    pub price: f64,
}

impl Product {
    pub fn new(id: u32, name: impl Into<String>, price: f64) -> Self {
        Self { id, name: name.into(), price }
    }
}
