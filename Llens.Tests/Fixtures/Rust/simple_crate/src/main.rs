mod models;
mod services;

use crate::services::order_service::create_order;
use crate::services::order_service::cancel_order;

fn main() {
    let order = create_order(1, 99.99);
    let status = cancel_order(&order);
}
