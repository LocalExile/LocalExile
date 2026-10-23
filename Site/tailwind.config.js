// tailwind.config.js
module.exports = {
  content: [
    "./components/**/*.{vue,js,ts}",
    "./layouts/**/*.vue",
    "./pages/**/*.vue",
    "./composables/**/*.{js,ts}",
    "./plugins/**/*.{js,ts}",
    "./app.vue",
  ],
  theme: {
    extend: {
      fontFamily: {
        cinzel: ["'Cinzel'", "serif"],
        fontin: ["'Fontin'", "sans-serif"],
        friz: ["'Friz Quadrata'", "serif"],
      },
    },
  },
  plugins: [],
};
