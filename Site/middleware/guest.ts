// middleware/guest.ts
export default defineNuxtRouteMiddleware((to) => {
  const auth = useAuth();

  // Если пользователь уже авторизован и пытается зайти на страницу логина
  if (auth.isAuthenticated.value && to.path === "/login") {
    return navigateTo("/");
  }
});
