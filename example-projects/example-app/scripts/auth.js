const AUTH_KEY = "example_app_auth_user";
const HARD_USER = "user";
const HARD_PASS = "user";

export function getCurrentUser() {
  try {
    const raw = localStorage.getItem(AUTH_KEY);
    if (!raw) return null;
    const value = String(raw).trim();
    return value || null;
  } catch {
    return null;
  }
}

export function isLoggedIn() {
  return Boolean(getCurrentUser());
}

export function login(username, password) {
  const u = String(username || "").trim();
  const p = String(password || "");
  if (u !== HARD_USER || p !== HARD_PASS) return false;
  localStorage.setItem(AUTH_KEY, u);
  return true;
}

export function logout() {
  localStorage.removeItem(AUTH_KEY);
}

export function canAccessPage(pageId) {
  if (!pageId) return true;
  const isAdminPage = pageId === "admin-products" || pageId === "admin-categories";
  if (!isAdminPage) return true;
  return isLoggedIn();
}
