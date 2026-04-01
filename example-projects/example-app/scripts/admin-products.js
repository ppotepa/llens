import { $, money } from "./utils.js";
import { getState, addProduct, updateProduct, deleteProduct } from "./store.js";
import { renderNav } from "./nav.js";

renderNav("admin-products");

function renderCategorySelect() {
  const s = getState();
  const options = s.categories.filter((c) => c.id !== "all")
    .map((c) => `<option value="${c.id}">${c.name}</option>`).join("");
  $("new-category").innerHTML = options;
}

function renderProducts() {
  const s = getState();
  const tbody = $("products-body");
  tbody.innerHTML = "";
  for (const p of s.products) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td><input value="${p.name}" data-f="name"/></td>
      <td>${p.category}</td>
      <td><input type="number" step="0.01" min="0" value="${p.price}" data-f="price"/></td>
      <td><input type="number" step="1" min="0" value="${p.stock}" data-f="stock"/></td>
      <td>${money(p.price)}</td>
      <td>
        <button class="soft" data-a="save">Save</button>
        <button data-a="del">Delete</button>
      </td>
    `;
    const saveBtn = tr.querySelector('button[data-a="save"]');
    const delBtn = tr.querySelector('button[data-a="del"]');
    saveBtn.onclick = () => {
      const name = tr.querySelector('input[data-f="name"]').value.trim();
      const price = Number(tr.querySelector('input[data-f="price"]').value);
      const stock = Number(tr.querySelector('input[data-f="stock"]').value);
      if (!name || Number.isNaN(price) || Number.isNaN(stock)) return;
      updateProduct(p.id, { name, price, stock });
      renderProducts();
    };
    delBtn.onclick = () => {
      deleteProduct(p.id);
      renderProducts();
    };
    tbody.appendChild(tr);
  }
}

$("product-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const name = $("new-name").value.trim();
  const category = $("new-category").value;
  const price = Number($("new-price").value);
  const stock = Number($("new-stock").value);
  if (!name || !category || Number.isNaN(price) || Number.isNaN(stock)) return;
  addProduct({ name, category, price, stock });
  e.target.reset();
  renderProducts();
});

renderCategorySelect();
renderProducts();
