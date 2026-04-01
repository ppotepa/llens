import { $, money } from "./utils.js";
import { getState, addCategory, deleteCategory } from "./store.js";
import { renderNav } from "./nav.js";

renderNav("admin-categories");

function render() {
  const s = getState();
  const list = $("categories-list");
  list.innerHTML = "";

  for (const c of s.categories.filter((x) => x.id !== "all")) {
    const count = s.products.filter((p) => p.category === c.id).length;
    const value = s.products
      .filter((p) => p.category === c.id)
      .reduce((sum, p) => sum + p.price * p.stock, 0);

    const li = document.createElement("li");
    li.className = "card";
    li.innerHTML = `
      <div class="row" style="justify-content:space-between">
        <strong>${c.name}</strong>
        <button ${count > 0 ? "disabled" : ""}>Delete</button>
      </div>
      <div class="muted">Products: ${count} · Inventory value: ${money(value)}</div>
    `;
    li.querySelector("button").onclick = () => {
      deleteCategory(c.id);
      render();
    };
    list.appendChild(li);
  }
}

$("category-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const name = $("category-name").value.trim();
  if (!name) return;
  addCategory(name);
  e.target.reset();
  render();
});

render();
