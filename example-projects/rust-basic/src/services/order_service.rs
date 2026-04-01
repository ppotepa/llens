use std::collections::HashMap;
use crate::models::{Order, Product};
use crate::utils::price_calculator;

pub trait OrderServiceTrait {
    fn place_order(&mut self, customer_name: &str, items: Vec<(Product, u32)>) -> &Order;
    fn get_order(&self, id: u32) -> Option<&Order>;
    fn all_orders(&self) -> Vec<&Order>;
    fn cancel_order(&mut self, id: u32) -> bool;
}

pub struct OrderService {
    store: HashMap<u32, Order>,
    next_id: u32,
}

impl OrderService {
    pub fn new() -> Self {
        Self { store: HashMap::new(), next_id: 1 }
    }

    pub fn discounted_total(&self, id: u32, discount_percent: f64) -> Option<f64> {
        self.get_order(id).map(|o| price_calculator::apply_discount(o.total(), discount_percent))
    }
}

impl OrderServiceTrait for OrderService {
    fn place_order(&mut self, customer_name: &str, items: Vec<(Product, u32)>) -> &Order {
        let id = self.next_id;
        self.next_id += 1;
        let mut order = Order::new(id, customer_name);
        for (product, qty) in items {
            order.add_line(product, qty);
        }
        self.store.insert(id, order);
        self.store.get(&id).unwrap()
    }

    fn get_order(&self, id: u32) -> Option<&Order> {
        self.store.get(&id)
    }

    fn all_orders(&self) -> Vec<&Order> {
        self.store.values().collect()
    }

    fn cancel_order(&mut self, id: u32) -> bool {
        self.store.remove(&id).is_some()
    }
}
