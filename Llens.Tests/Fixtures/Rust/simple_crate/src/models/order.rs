pub struct Order {
    pub id: u32,
    pub total: f64,
}

impl Order {
    pub fn new(id: u32, total: f64) -> Self {
        Order { id, total }
    }

    pub fn is_valid(&self) -> bool {
        self.total > 0.0
    }
}

pub enum OrderStatus {
    Pending,
    Confirmed,
    Cancelled,
}
