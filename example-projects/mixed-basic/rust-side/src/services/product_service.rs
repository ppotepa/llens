use std::collections::HashMap;
use crate::models::Product;

pub trait ProductServiceTrait {
    fn get_by_id(&self, id: u32) -> Option<&Product>;
    fn get_all(&self) -> Vec<&Product>;
    fn add(&mut self, product: Product);
}

pub struct ProductService {
    store: HashMap<u32, Product>,
}

impl ProductService {
    pub fn new() -> Self {
        Self { store: HashMap::new() }
    }
}

impl ProductServiceTrait for ProductService {
    fn get_by_id(&self, id: u32) -> Option<&Product> {
        self.store.get(&id)
    }

    fn get_all(&self) -> Vec<&Product> {
        self.store.values().collect()
    }

    fn add(&mut self, product: Product) {
        self.store.insert(product.id, product);
    }
}
