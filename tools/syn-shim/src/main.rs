use std::io::{self, Read};
use syn::visit::Visit;
use syn::{File, ItemFn, ItemStruct, ItemEnum, ItemTrait, ItemImpl, ItemMod, ItemUse, UseTree};
use serde::Serialize;

#[derive(Serialize)]
struct Symbol {
    name: String,
    kind: String,
    line: usize,
    signature: Option<String>,
}

#[derive(Serialize)]
struct Output {
    symbols: Vec<Symbol>,
    imports: Vec<String>,
}

struct Visitor {
    symbols: Vec<Symbol>,
    imports: Vec<String>,
    source_lines: Vec<String>,
}

impl Visitor {
    fn line_of(&self, span: proc_macro2::Span) -> usize {
        span.start().line
    }
}

fn collect_use_paths(tree: &UseTree, prefix: &str, out: &mut Vec<String>) {
    match tree {
        UseTree::Path(p) => {
            let next = if prefix.is_empty() {
                p.ident.to_string()
            } else {
                format!("{}::{}", prefix, p.ident)
            };
            collect_use_paths(&p.tree, &next, out);
        }
        UseTree::Name(n) => {
            let path = if prefix.is_empty() {
                n.ident.to_string()
            } else {
                format!("{}::{}", prefix, n.ident)
            };
            out.push(path);
        }
        UseTree::Rename(r) => {
            let path = if prefix.is_empty() {
                r.ident.to_string()
            } else {
                format!("{}::{}", prefix, r.ident)
            };
            out.push(path);
        }
        UseTree::Glob(_) => {
            let path = if prefix.is_empty() {
                "*".to_string()
            } else {
                format!("{}::*", prefix)
            };
            out.push(path);
        }
        UseTree::Group(g) => {
            for item in &g.items {
                collect_use_paths(item, prefix, out);
            }
        }
    }
}

impl<'ast> Visit<'ast> for Visitor {
    fn visit_item_fn(&mut self, node: &'ast ItemFn) {
        let line = self.line_of(node.sig.fn_token.span);
        let sig = self.source_lines
            .get(line.saturating_sub(1))
            .map(|s| s.trim().to_string());
        self.symbols.push(Symbol {
            name: node.sig.ident.to_string(),
            kind: "Function".to_string(),
            line,
            signature: sig,
        });
        syn::visit::visit_item_fn(self, node);
    }

    fn visit_item_struct(&mut self, node: &'ast ItemStruct) {
        let line = self.line_of(node.struct_token.span);
        self.symbols.push(Symbol {
            name: node.ident.to_string(),
            kind: "Struct".to_string(),
            line,
            signature: None,
        });
        syn::visit::visit_item_struct(self, node);
    }

    fn visit_item_enum(&mut self, node: &'ast ItemEnum) {
        let line = self.line_of(node.enum_token.span);
        self.symbols.push(Symbol {
            name: node.ident.to_string(),
            kind: "Enum".to_string(),
            line,
            signature: None,
        });
        syn::visit::visit_item_enum(self, node);
    }

    fn visit_item_trait(&mut self, node: &'ast ItemTrait) {
        let line = self.line_of(node.trait_token.span);
        self.symbols.push(Symbol {
            name: node.ident.to_string(),
            kind: "Trait".to_string(),
            line,
            signature: None,
        });
        syn::visit::visit_item_trait(self, node);
    }

    fn visit_item_impl(&mut self, node: &'ast ItemImpl) {
        if let Some((_, path, _)) = &node.trait_ {
            let trait_name = path.segments.last()
                .map(|s| s.ident.to_string())
                .unwrap_or_default();
            let self_ty = match node.self_ty.as_ref() {
                syn::Type::Path(tp) => tp.path.segments.last()
                    .map(|s| s.ident.to_string())
                    .unwrap_or_default(),
                _ => "?".to_string(),
            };
            let line = self.line_of(node.impl_token.span);
            self.symbols.push(Symbol {
                name: format!("{} for {}", trait_name, self_ty),
                kind: "TraitImpl".to_string(),
                line,
                signature: None,
            });
        }
        syn::visit::visit_item_impl(self, node);
    }

    fn visit_item_mod(&mut self, node: &'ast ItemMod) {
        let line = self.line_of(node.mod_token.span);
        self.symbols.push(Symbol {
            name: node.ident.to_string(),
            kind: "Module".to_string(),
            line,
            signature: None,
        });
        syn::visit::visit_item_mod(self, node);
    }

    fn visit_item_use(&mut self, node: &'ast ItemUse) {
        let mut paths = Vec::new();
        collect_use_paths(&node.tree, "", &mut paths);
        self.imports.extend(paths);
        syn::visit::visit_item_use(self, node);
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let source = if args.len() > 1 {
        std::fs::read_to_string(&args[1]).unwrap_or_else(|e| {
            eprintln!("Error reading file: {}", e);
            std::process::exit(1);
        })
    } else {
        let mut buf = String::new();
        io::stdin().read_to_string(&mut buf).expect("Failed to read stdin");
        buf
    };

    let source_lines: Vec<String> = source.lines().map(|l| l.to_string()).collect();

    let ast: File = match syn::parse_str(&source) {
        Ok(f) => f,
        Err(_) => {
            let output = Output { symbols: vec![], imports: vec![] };
            println!("{}", serde_json::to_string(&output).unwrap());
            return;
        }
    };

    let mut visitor = Visitor {
        symbols: Vec::new(),
        imports: Vec::new(),
        source_lines,
    };
    visitor.visit_file(&ast);

    let output = Output {
        symbols: visitor.symbols,
        imports: visitor.imports,
    };

    println!("{}", serde_json::to_string(&output).unwrap());
}
