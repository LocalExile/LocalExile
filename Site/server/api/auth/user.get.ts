export default defineEventHandler(async (event) => {
  // Проверяем авторизацию через заголовки
  const authHeader = getHeader(event, "Authorization");

  // В фиктивной авторизации просто возвращаем пользователя
  // если есть заголовок Authorization (даже если он фиктивный)
  if (authHeader) {
    return {
      id: 1,
      email: "admin",
      name: "admin",
      role: "admin",
    };
  }

  // Если нет заголовка авторизации
  throw createError({
    statusCode: 401,
    message: "Not authenticated",
  });
});
