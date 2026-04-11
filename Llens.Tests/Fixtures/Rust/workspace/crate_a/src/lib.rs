pub struct Config {
    pub value: String,
}

pub fn default_config() -> Config {
    Config { value: String::from("default") }
}
