const store = {
  categories: [
    { id: "all", name: "All" },
    { id: "books", name: "Books" },
    { id: "hardware", name: "Hardware" },
    { id: "office", name: "Office" }
  ],
  products: [
    { id: "p1", name: "Refactoring", category: "books", price: 39.9, stock: 14 },
    { id: "p2", name: "Mechanical Keyboard", category: "hardware", price: 89.0, stock: 7 },
    { id: "p3", name: "Noise-Canceling Headphones", category: "hardware", price: 129.0, stock: 5 },
    { id: "p4", name: "Notebook Set", category: "office", price: 12.5, stock: 25 },
    { id: "p5", name: "Architecture Notes", category: "books", price: 24.0, stock: 9 },
    { id: "p6", name: "Desk Lamp", category: "office", price: 31.0, stock: 11 }
  ],
  selectedCategory: "all",
  search: "",
  sort: "name",
  cart: {}
};

const $ = (id) => document.getElementById(id);

function getVisibleProducts() {
  const q = store.search.trim().toLowerCase();
  let items = store.products.filter((p) => {
    const inCategory = store.selectedCategory === "all" || p.category === store.selectedCategory;
    const inSearch = q.length === 0 || p.name.toLowerCase().includes(q);
    return inCategory && inSearch;
  });

  items = [...items].sort((a, b) => {
    if (store.sort === "price-asc") return a.price - b.price;
    if (store.sort === "price-desc") return b.price - a.price;
    if (store.sort === "stock") return b.stock - a.stock;
    return a.name.localeCompare(b.name);
  });

  return items;
}

function addToCart(productId) {
  const product = store.products.find((p) => p.id === productId);
  if (!product) return;
  const qty = store.cart[productId] || 0;
  if (qty >= product.stock) return;
  store.cart[productId] = qty + 1;
  render();
}

function renderCategories() {
  const list = $("category-list");
  list.innerHTML = "";
  for (const c of store.categories) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.textContent = c.name;
    btn.disabled = c.id === store.selectedCategory;
    btn.onclick = () => {
      store.selectedCategory = c.id;
      render();
    };
    li.appendChild(btn);
    list.appendChild(li);
  }
}

function renderProducts() {
  const grid = $("products-grid");
  grid.innerHTML = "";
  for (const p of getVisibleProducts()) {
    const card = document.createElement("article");
    card.className = "product";

    const inCart = store.cart[p.id] || 0;
    const left = p.stock - inCart;

    card.innerHTML = `
      <h3>${p.name}</h3>
      <div class="meta">${p.category} · Stock: ${left}</div>
      <div class="price">$${p.price.toFixed(2)}</div>
      <button ${left <= 0 ? "disabled" : ""}>Add to cart</button>
    `;

    card.querySelector("button").onclick = () => addToCart(p.id);
    grid.appendChild(card);
  }
}

function renderCart() {
  const list = $("cart-list");
  list.innerHTML = "";
  let total = 0;

  for (const [id, qty] of Object.entries(store.cart)) {
    const p = store.products.find((x) => x.id === id);
    if (!p || qty <= 0) continue;
    const li = document.createElement("li");
    const lineTotal = p.price * qty;
    total += lineTotal;
    li.textContent = `${p.name} x${qty} — $${lineTotal.toFixed(2)}`;
    list.appendChild(li);
  }

  $("cart-total").textContent = `Total: $${total.toFixed(2)}`;
}

function render() {
  renderCategories();
  renderProducts();
  renderCart();
}

function wireInputs() {
  $("search-input").addEventListener("input", (e) => {
    store.search = e.target.value;
    renderProducts();
  });

  $("sort-select").addEventListener("change", (e) => {
    store.sort = e.target.value;
    renderProducts();
  });
}

wireInputs();
render();
