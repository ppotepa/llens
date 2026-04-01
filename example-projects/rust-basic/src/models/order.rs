use super::Product;

/// A single line in an order.
#[derive(Debug, Clone)]
pub struct OrderLine {
    pub product: Product,
    pub quantity: u32,
}

impl OrderLine {
    pub fn line_total(&self) -> f64 {
        self.product.price * self.quantity as f64
    }
}

/// A customer order containing one or more products.
#[derive(Debug, Clone)]
pub struct Order {
    pub id: u32,
    pub customer_name: String,
    pub lines: Vec<OrderLine>,
}

impl Order {
    pub fn new(id: u32, customer_name: impl Into<String>) -> Self {
        Self { id, customer_name: customer_name.into(), lines: vec![] }
    }

    pub fn add_line(&mut self, product: Product, quantity: u32) {
        self.lines.push(OrderLine { product, quantity });
    }

    pub fn total(&self) -> f64 {
        self.lines.iter().map(|l| l.line_total()).sum()
    }
}
