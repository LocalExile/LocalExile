export default defineEventHandler(async (event) => {
  const body = await readBody(event);
  const { email, password } = body;

  // Фиксированные учетные данные
  const FIXED_CREDENTIALS = {
    email: "admin",
    password: "admin",
    name: "admin",
  };

  // Проверяем учетные данные
  if (
    email === FIXED_CREDENTIALS.email &&
    password === FIXED_CREDENTIALS.password
  ) {
    // Возвращаем данные пользователя
    return {
      user: {
        id: 1,
        email: FIXED_CREDENTIALS.email,
        name: FIXED_CREDENTIALS.name,
        role: "admin",
      },
      // Можно добавить фиктивный токен
      token: "fixed-token-for-development",
    };
  }

  // Если учетные данные неверные
  throw createError({
    statusCode: 401,
    message: "Неверный логин или пароль",
  });
});
