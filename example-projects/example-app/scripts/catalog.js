import { $, money } from "./utils.js";
import { getState, setFilter, visibleProducts, cartQty, setCartQty } from "./store.js";
import { renderNav } from "./nav.js";

renderNav("catalog");

function renderCategories() {
  const s = getState();
  const sel = $("category-select");
  sel.innerHTML = s.categories.map((c) => `<option value="${c.id}">${c.name}</option>`).join("");
  sel.value = s.filters.category;
}

function renderProducts() {
  const grid = $("products-grid");
  grid.innerHTML = "";
  for (const p of visibleProducts()) {
    const qty = cartQty(p.id);
    const left = p.stock - qty;
    const el = document.createElement("article");
    el.className = "product";
    el.innerHTML = `
      <h3>${p.name}</h3>
      <div class="muted">${p.category} · Stock left: ${left}</div>
      <div class="money">${money(p.price)}</div>
      <div class="row">
        <button ${left <= 0 ? "disabled" : ""}>Add</button>
        <button class="soft" ${qty <= 0 ? "disabled" : ""}>Remove</button>
      </div>
    `;
    const [addBtn, removeBtn] = el.querySelectorAll("button");
    addBtn.onclick = () => { setCartQty(p.id, qty + 1); renderProducts(); };
    removeBtn.onclick = () => { setCartQty(p.id, qty - 1); renderProducts(); };
    grid.appendChild(el);
  }
}

$("search-input").addEventListener("input", (e) => {
  setFilter({ q: e.target.value });
  renderProducts();
});

$("sort-select").addEventListener("change", (e) => {
  setFilter({ sort: e.target.value });
  renderProducts();
});

$("category-select").addEventListener("change", (e) => {
  setFilter({ category: e.target.value });
  renderProducts();
});

const s = getState();
$("search-input").value = s.filters.q;
$("sort-select").value = s.filters.sort;
renderCategories();
renderProducts();
