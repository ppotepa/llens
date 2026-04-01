/// A product available for purchase.
#[derive(Debug, Clone)]
pub struct Product {
    pub id: u32,
    pub name: String,
    pub price: f64,
    pub stock_quantity: u32,
}

impl Product {
    pub fn new(id: u32, name: impl Into<String>, price: f64, stock_quantity: u32) -> Self {
        Self { id, name: name.into(), price, stock_quantity }
    }

    pub fn is_in_stock(&self) -> bool {
        self.stock_quantity > 0
    }
}
