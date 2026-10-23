// nuxt.config.ts
export default defineNuxtConfig({
  compatibilityDate: "2025-07-15",
  devtools: { enabled: true },
  css: ["~/assets/css/fonts.css"],

  modules: [
    "@nuxtjs/tailwindcss",
    "@nuxt/image",
    "@nuxtjs/device",
    "@nuxtjs/robots",
  ],

  // Отключите все проверки TypeScript
  typescript: {
    typeCheck: false,
    strict: false,
    shim: false,
  },

  // Отключите vite проверки
  vite: {
    esbuild: {
      tsconfigRaw: "{}",
    },
  },

  // Опции для nitro
  nitro: {
    typescript: {
      strict: false,
    },
  },
});
