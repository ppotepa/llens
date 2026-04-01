const STORAGE_KEY = "example_app_store_v1";

const defaultData = {
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

const store = loadState();
const $ = (id) => document.getElementById(id);

function loadState() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return structuredClone(defaultData);
    const parsed = JSON.parse(raw);
    return {
      ...structuredClone(defaultData),
      ...parsed,
      cart: parsed.cart || {}
    };
  } catch {
    return structuredClone(defaultData);
  }
}

function persist() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(store));
}

function makeId(prefix) {
  return `${prefix}-${Math.random().toString(36).slice(2, 9)}`;
}

function categoryExists(id) {
  return store.categories.some((c) => c.id === id);
}

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

function qtyInCart(productId) {
  return store.cart[productId] || 0;
}

function addToCart(productId) {
  const product = store.products.find((p) => p.id === productId);
  if (!product) return;
  const qty = qtyInCart(productId);
  if (qty >= product.stock) return;
  store.cart[productId] = qty + 1;
  persist();
  render();
}

function decrementCart(productId) {
  const qty = qtyInCart(productId);
  if (qty <= 1) {
    delete store.cart[productId];
  } else {
    store.cart[productId] = qty - 1;
  }
  persist();
  render();
}

function renderCategories() {
  const list = $("category-list");
  list.innerHTML = "";

  for (const c of store.categories) {
    const li = document.createElement("li");
    const row = document.createElement("div");
    row.className = "row gap";

    const pick = document.createElement("button");
    pick.textContent = c.name;
    pick.disabled = c.id === store.selectedCategory;
    pick.onclick = () => {
      store.selectedCategory = c.id;
      persist();
      render();
    };

    row.appendChild(pick);

    if (c.id !== "all") {
      const del = document.createElement("button");
      del.className = "soft";
      del.textContent = "x";
      del.onclick = () => {
        if (store.products.some((p) => p.category === c.id)) return;
        store.categories = store.categories.filter((x) => x.id !== c.id);
        if (store.selectedCategory === c.id) store.selectedCategory = "all";
        persist();
        render();
      };
      row.appendChild(del);
    }

    li.appendChild(row);
    list.appendChild(li);
  }

  const select = $("product-category");
  select.innerHTML = "";
  for (const c of store.categories.filter((x) => x.id !== "all")) {
    const option = document.createElement("option");
    option.value = c.id;
    option.textContent = c.name;
    select.appendChild(option);
  }
}

function renderProducts() {
  const grid = $("products-grid");
  grid.innerHTML = "";

  for (const p of getVisibleProducts()) {
    const card = document.createElement("article");
    card.className = "product";

    const inCart = qtyInCart(p.id);
    const left = p.stock - inCart;

    card.innerHTML = `
      <h3>${p.name}</h3>
      <div class="meta">${p.category} · Stock left: ${left}</div>
      <div class="price">$${p.price.toFixed(2)}</div>
      <div class="row gap">
        <button ${left <= 0 ? "disabled" : ""}>Add to cart</button>
        <button class="soft" data-del="1">Delete</button>
      </div>
    `;

    const [addBtn, delBtn] = card.querySelectorAll("button");
    addBtn.onclick = () => addToCart(p.id);
    delBtn.onclick = () => {
      delete store.cart[p.id];
      store.products = store.products.filter((x) => x.id !== p.id);
      persist();
      render();
    };

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
    li.className = "cart-line";

    const lineTotal = p.price * qty;
    total += lineTotal;

    const label = document.createElement("span");
    label.textContent = `${p.name} — $${lineTotal.toFixed(2)}`;

    const controls = document.createElement("div");
    controls.className = "qty";

    const minus = document.createElement("button");
    minus.className = "soft";
    minus.textContent = "-";
    minus.onclick = () => decrementCart(p.id);

    const count = document.createElement("span");
    count.textContent = `x${qty}`;

    const plus = document.createElement("button");
    plus.textContent = "+";
    plus.disabled = qty >= p.stock;
    plus.onclick = () => addToCart(p.id);

    controls.append(minus, count, plus);
    li.append(label, controls);
    list.appendChild(li);
  }

  $("cart-total").textContent = `Total: $${total.toFixed(2)}`;
}

function wireInputs() {
  $("search-input").addEventListener("input", (e) => {
    store.search = e.target.value;
    persist();
    renderProducts();
  });

  $("sort-select").addEventListener("change", (e) => {
    store.sort = e.target.value;
    persist();
    renderProducts();
  });

  $("category-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = $("category-name").value.trim();
    if (!name) return;
    const id = name.toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "") || makeId("cat");
    if (categoryExists(id)) return;
    store.categories.push({ id, name });
    $("category-name").value = "";
    persist();
    render();
  });

  $("product-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = $("product-name").value.trim();
    const category = $("product-category").value;
    const price = Number($("product-price").value);
    const stock = Number($("product-stock").value);

    if (!name || !category || Number.isNaN(price) || Number.isNaN(stock)) return;
    if (price < 0 || stock < 0) return;

    store.products.push({
      id: makeId("p"),
      name,
      category,
      price,
      stock: Math.floor(stock)
    });

    $("product-form").reset();
    persist();
    render();
  });

  $("clear-cart").addEventListener("click", () => {
    store.cart = {};
    persist();
    render();
  });

  $("seed-demo").addEventListener("click", () => {
    const next = structuredClone(defaultData);
    Object.assign(store, next);
    persist();
    render();
  });
}

function boot() {
  $("search-input").value = store.search;
  $("sort-select").value = store.sort;
  wireInputs();
  render();
}

function render() {
  renderCategories();
  renderProducts();
  renderCart();
}

boot();
