export const STORAGE_KEY = "example_app_store_pages_v1";

const seed = {
  categories: [
    { id: "all", name: "All" },
    { id: "books", name: "Books" },
    { id: "hardware", name: "Hardware" },
    { id: "office", name: "Office" }
  ],
  products: [
    { id: "p1", name: "Refactoring", category: "books", price: 39.9, stock: 14 },
    { id: "p2", name: "Mechanical Keyboard", category: "hardware", price: 89, stock: 7 },
    { id: "p3", name: "Noise-Canceling Headphones", category: "hardware", price: 129, stock: 5 },
    { id: "p4", name: "Notebook Set", category: "office", price: 12.5, stock: 25 },
    { id: "p5", name: "Architecture Notes", category: "books", price: 24, stock: 9 },
    { id: "p6", name: "Desk Lamp", category: "office", price: 31, stock: 11 }
  ],
  filters: { q: "", sort: "name", category: "all" },
  cart: {}
};

function clone(v) { return JSON.parse(JSON.stringify(v)); }

function load() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return clone(seed);
    const parsed = JSON.parse(raw);
    return {
      ...clone(seed),
      ...parsed,
      filters: { ...seed.filters, ...(parsed.filters || {}) },
      cart: parsed.cart || {}
    };
  } catch {
    return clone(seed);
  }
}

const state = load();
export function getState() { return state; }
export function save() { localStorage.setItem(STORAGE_KEY, JSON.stringify(state)); }
export function resetSeed() { Object.assign(state, clone(seed)); save(); }

export function setFilter(patch) {
  state.filters = { ...state.filters, ...patch };
  save();
}

export function visibleProducts() {
  const q = state.filters.q.trim().toLowerCase();
  const items = state.products.filter((p) => {
    const byCat = state.filters.category === "all" || p.category === state.filters.category;
    const byQ = q.length === 0 || p.name.toLowerCase().includes(q);
    return byCat && byQ;
  });
  const s = state.filters.sort;
  return [...items].sort((a, b) => {
    if (s === "price-asc") return a.price - b.price;
    if (s === "price-desc") return b.price - a.price;
    if (s === "stock") return b.stock - a.stock;
    return a.name.localeCompare(b.name);
  });
}

export function addCategory(name) {
  const id = name.toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "") || `cat-${Date.now()}`;
  if (state.categories.some((c) => c.id === id)) return null;
  const item = { id, name };
  state.categories.push(item);
  save();
  return item;
}

export function deleteCategory(id) {
  if (id === "all") return false;
  if (state.products.some((p) => p.category === id)) return false;
  state.categories = state.categories.filter((c) => c.id !== id);
  if (state.filters.category === id) state.filters.category = "all";
  save();
  return true;
}

export function addProduct({ name, category, price, stock }) {
  const item = {
    id: `p-${Math.random().toString(36).slice(2, 9)}`,
    name,
    category,
    price: Number(price),
    stock: Math.floor(Number(stock))
  };
  state.products.push(item);
  save();
  return item;
}

export function updateProduct(id, patch) {
  const p = state.products.find((x) => x.id === id);
  if (!p) return false;
  Object.assign(p, patch);
  if (p.price < 0) p.price = 0;
  if (p.stock < 0) p.stock = 0;
  p.stock = Math.floor(p.stock);
  save();
  return true;
}

export function deleteProduct(id) {
  delete state.cart[id];
  state.products = state.products.filter((p) => p.id !== id);
  save();
}

export function cartQty(productId) { return state.cart[productId] || 0; }

export function setCartQty(productId, qty) {
  const p = state.products.find((x) => x.id === productId);
  if (!p) return;
  const clamped = Math.max(0, Math.min(Math.floor(qty), p.stock));
  if (clamped === 0) delete state.cart[productId];
  else state.cart[productId] = clamped;
  save();
}

export function clearCart() { state.cart = {}; save(); }

export function cartTotal() {
  let total = 0;
  for (const [id, qty] of Object.entries(state.cart)) {
    const p = state.products.find((x) => x.id === id);
    if (!p) continue;
    total += p.price * qty;
  }
  return total;
}

export function reportStats() {
  const productCount = state.products.length;
  const categoryCount = state.categories.length - 1;
  const inventoryValue = state.products.reduce((sum, p) => sum + p.price * p.stock, 0);
  const lowStock = state.products.filter((p) => p.stock <= 5);
  const topCart = Object.entries(state.cart)
    .map(([id, qty]) => ({ id, qty, product: state.products.find((p) => p.id === id) }))
    .filter((x) => x.product)
    .sort((a, b) => b.qty - a.qty)
    .slice(0, 5);
  return { productCount, categoryCount, inventoryValue, lowStock, topCart };
}
