use crate_a::default_config;
use crate_a::Config;

fn main() {
    let cfg: Config = default_config();
    println!("{}", cfg.value);
}
