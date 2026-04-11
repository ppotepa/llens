use crate::models::order::{Order, OrderStatus};

pub fn create_order(id: u32, total: f64) -> Order {
    Order::new(id, total)
}

pub fn cancel_order(order: &Order) -> OrderStatus {
    OrderStatus::Cancelled
}

pub fn validate_order(order: &Order) -> bool {
    order.is_valid()
}
