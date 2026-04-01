import { canAccessPage, getCurrentUser, login, logout } from "./auth.js";

export function renderNav(active) {
  if (!canAccessPage(active)) {
    window.location.href = "./catalog.html";
    return false;
  }

  const links = [
    ["catalog", "Catalog", "./catalog.html"],
    ["cart", "Cart", "./cart.html"],
    ["admin-products", "Admin Products", "./admin-products.html"],
    ["admin-categories", "Admin Categories", "./admin-categories.html"],
    ["reports", "Reports", "./reports.html"]
  ];

  const nav = document.getElementById("app-nav");
  if (!nav) return true;

  nav.innerHTML = links
    .map(([id, label, href]) => {
      const locked = (id === "admin-products" || id === "admin-categories") && !getCurrentUser();
      return `<a class="${id === active ? "active" : ""}" href="${href}">${label}${locked ? " (locked)" : ""}</a>`;
    })
    .join("");

  renderUserPanel(active);
  return true;
}

function renderUserPanel(active) {
  const header = document.querySelector("header.topbar");
  if (!header) return;

  let panel = document.getElementById("user-panel");
  if (!panel) {
    panel = document.createElement("div");
    panel.id = "user-panel";
    panel.className = "user-panel";
    header.appendChild(panel);
  }

  const user = getCurrentUser();
  if (user) {
    panel.innerHTML = `
      <div class="auth-row">
        <span class="auth-label">Signed in as <strong>${user}</strong></span>
        <button id="logout-btn" class="soft" type="button">Logout</button>
      </div>
    `;
    panel.querySelector("#logout-btn")?.addEventListener("click", () => {
      logout();
      window.location.href = "./catalog.html";
    });
    return;
  }

  panel.innerHTML = `
    <form id="login-form" class="auth-row">
      <input id="login-user" autocomplete="username" placeholder="username" value="user" />
      <input id="login-pass" autocomplete="current-password" type="password" placeholder="password" value="user" />
      <button type="submit">Login</button>
      <span id="login-msg" class="muted"></span>
    </form>
  `;

  panel.querySelector("#login-form")?.addEventListener("submit", (e) => {
    e.preventDefault();
    const u = panel.querySelector("#login-user")?.value || "";
    const p = panel.querySelector("#login-pass")?.value || "";
    const ok = login(u, p);
    const msg = panel.querySelector("#login-msg");
    if (!ok) {
      if (msg) msg.textContent = "Invalid credentials";
      return;
    }

    if (active === "admin-products") {
      window.location.href = "./admin-products.html";
      return;
    }
    if (active === "admin-categories") {
      window.location.href = "./admin-categories.html";
      return;
    }

    renderNav(active);
  });
}
