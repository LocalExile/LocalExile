// composables/useAuth.ts
interface AuthUser {
  id: number;
  name: string;
  email: string;
  role: string;
}

export const useAuth = () => {
  // Фиксированные учетные данные
  const FIXED_CREDENTIALS = {
    email: "admin",
    password: "admin",
    user: {
      id: 1,
      name: "admin",
      email: "admin",
      role: "admin",
    } as AuthUser,
  };

  // Реактивные состояния
  const user = useState<AuthUser | null>("auth_user", () => null);
  const isAuthenticated = useState<boolean>("auth_authenticated", () => false);
  const loading = useState<boolean>("auth_loading", () => false);

  // Проверка клиентской стороны (совместимый способ)
  const isClient = typeof window !== "undefined";

  // Инициализация при загрузке
  const init = () => {
    if (isClient) {
      const savedUser = localStorage.getItem("auth_user");
      const savedToken = localStorage.getItem("auth_token");

      if (savedUser && savedToken) {
        try {
          user.value = JSON.parse(savedUser);
          isAuthenticated.value = true;
        } catch (e) {
          console.error("Failed to parse saved user:", e);
        }
      }
    }
  };

  // Вход
  const login = async (email: string, password: string) => {
    loading.value = true;

    try {
      // Имитация задержки сети
      await new Promise((resolve) => setTimeout(resolve, 300));

      // Проверяем фиксированные учетные данные
      if (
        email === FIXED_CREDENTIALS.email &&
        password === FIXED_CREDENTIALS.password
      ) {
        user.value = FIXED_CREDENTIALS.user;
        isAuthenticated.value = true;

        // Сохраняем в localStorage
        if (isClient) {
          try {
            localStorage.setItem(
              "auth_user",
              JSON.stringify(FIXED_CREDENTIALS.user)
            );
            localStorage.setItem("auth_token", "fixed-token-" + Date.now());
            localStorage.setItem("auth_authenticated", "true");
          } catch (e) {
            console.error("Failed to save to localStorage:", e);
          }
        }

        return {
          success: true,
          user: FIXED_CREDENTIALS.user,
        };
      } else {
        throw new Error("Неверный логин или пароль");
      }
    } catch (error: any) {
      return {
        success: false,
        error: error.message || "Ошибка авторизации",
      };
    } finally {
      loading.value = false;
    }
  };

  // Выход
  const logout = () => {
    user.value = null;
    isAuthenticated.value = false;

    if (isClient) {
      try {
        localStorage.removeItem("auth_user");
        localStorage.removeItem("auth_token");
        localStorage.removeItem("auth_authenticated");
      } catch (e) {
        console.error("Failed to clear localStorage:", e);
      }
    }

    return { success: true };
  };

  // Быстрый вход с фиксированными данными
  const quickLogin = async () => {
    return await login(FIXED_CREDENTIALS.email, FIXED_CREDENTIALS.password);
  };

  // Проверка роли
  const hasRole = (role: string) => {
    return user.value?.role === role;
  };

  // Инициализируем при создании (только на клиенте)
  if (isClient) {
    init();
  }

  return {
    // Состояния
    user: readonly(user),
    isAuthenticated: readonly(isAuthenticated),
    loading: readonly(loading),

    // Методы
    login,
    logout,
    quickLogin,
    hasRole,

    // Компьютед свойства
    isAdmin: computed(() => hasRole("admin")),
  };
};
