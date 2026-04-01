import { $, money } from "./utils.js";
import { getState, setCartQty, cartTotal, clearCart } from "./store.js";
import { renderNav } from "./nav.js";

renderNav("cart");

function renderCart() {
  const s = getState();
  const list = $("cart-list");
  list.innerHTML = "";

  for (const [id, qty] of Object.entries(s.cart)) {
    const p = s.products.find((x) => x.id === id);
    if (!p) continue;
    const li = document.createElement("li");
    li.className = "card";
    li.innerHTML = `
      <div class="row" style="justify-content:space-between">
        <strong>${p.name}</strong>
        <span>${money(p.price * qty)}</span>
      </div>
      <div class="row" style="margin-top:8px">
        <button class="soft">-</button>
        <span>x${qty}</span>
        <button>+</button>
      </div>
    `;
    const [minus, plus] = li.querySelectorAll("button");
    minus.onclick = () => { setCartQty(id, qty - 1); renderCart(); };
    plus.onclick = () => { setCartQty(id, qty + 1); renderCart(); };
    list.appendChild(li);
  }

  $("cart-total").textContent = `Total: ${money(cartTotal())}`;
}

$("clear-cart").addEventListener("click", () => {
  clearCart();
  renderCart();
});

renderCart();
