// middleware/auth.ts
export default defineNuxtRouteMiddleware((to) => {
  // Проверяем на клиенте
  if (typeof window === "undefined") return;

  const isLoggedIn = localStorage.getItem("auth_authenticated") === "true";

  const protectedRoutes = ["/dashboard", "/profile", "/admin"];
  const requiresAuth =
    protectedRoutes.some((route) => to.path.startsWith(route)) ||
    to.meta.requiresAuth;

  if (requiresAuth && !isLoggedIn) {
    return navigateTo({
      path: "/login",
      query: { redirect: to.fullPath },
    });
  }

  if (isLoggedIn && to.path === "/login") {
    return navigateTo("/dashboard");
  }
});
