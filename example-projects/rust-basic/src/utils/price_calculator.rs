/// Applies a percentage discount to a price.
pub fn apply_discount(price: f64, discount_percent: f64) -> f64 {
    assert!((0.0..=100.0).contains(&discount_percent), "Discount must be between 0 and 100");
    price * (1.0 - discount_percent / 100.0)
}

/// Rounds a price to the nearest step (default 0.05).
pub fn round_to_nearest(price: f64, step: f64) -> f64 {
    (price / step).round() * step
}
