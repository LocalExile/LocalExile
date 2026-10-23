export default defineEventHandler(() => {
  // Просто возвращаем успех
  return {
    success: true,
    message: "Logged out successfully",
  };
});
