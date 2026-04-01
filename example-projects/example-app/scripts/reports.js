import { $, money } from "./utils.js";
import { reportStats } from "./store.js";
import { renderNav } from "./nav.js";

renderNav("reports");

function render() {
  const stats = reportStats();
  $("product-count").textContent = stats.productCount;
  $("category-count").textContent = stats.categoryCount;
  $("inventory-value").textContent = money(stats.inventoryValue);

  const low = $("low-stock");
  low.innerHTML = stats.lowStock.length
    ? stats.lowStock.map((p) => `<li>${p.name} (stock: ${p.stock})</li>`).join("")
    : "<li>No low-stock items.</li>";

  const top = $("top-cart");
  top.innerHTML = stats.topCart.length
    ? stats.topCart.map((x) => `<li>${x.product.name} x${x.qty}</li>`).join("")
    : "<li>Cart is empty.</li>";
}

render();
