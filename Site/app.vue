<script setup>
const config = useRuntimeConfig();
const baseUrl = config.public.baseUrl || "";
// Не нужно импортировать Header и Footer в app.vue, если они уже в лейауте
useHead({
  meta: [
    { property: "og:locale", content: "ru_RU" },
    { property: "og:title", content: "Path of Exile" },
    {
      property: "og:description",
      content:
        "Path of Exile - лучшая бесплатная игра в жанре Action RPG. Мрачный мир, глубокая и сложная система прокачки персонажа, бартерная система торговли без привычных для жанра золотых монет",
    },
    { property: "og:image", content: `${baseUrl}/img/ogimage.png` },
    { property: "og:site_name", content: "Path of Exile" },
    { property: "og:type", content: "website" },

    {
      property: "og:keywords",
      content:
        "path of exile, path of exile билды, path of exile официальный, path of exile русификатор, path of exile скачать, path of exile скачать торрент, path of exile русская, скачать path of exile, path of exile на русском, poe, path of exile русская версия, path of exile официальный сайт, path of exile сайт, path of exile торрент",
    },

    {
      name: "keywords",
      content:
        "path of exile, path of exile билды, path of exile официальный, path of exile русификатор, path of exile скачать, path of exile скачать торрент, path of exile русская, скачать path of exile, path of exile на русском, poe, path of exile русская версия, path of exile официальный сайт, path of exile сайт, path of exile торрент",
    },
    {
      name: "description",
      content:
        "Path of Exile - лучшая бесплатная игра в жанре Action RPG. Мрачный мир, глубокая и сложная система прокачки персонажа, бартерная система торговли без привычных для жанра золотых монет",
    },
  ],
});

const { $device } = useNuxtApp();

// Проверка на бота
if ($device.isBot) {
  // console.log("Бот обнаружен");
} else {
  // console.log("Бот не обнаружен");
}

// Также можно проверять другие параметры
console.log("Is mobile:", $device.isMobile);
console.log("Is desktop:", $device.isDesktop);
console.log("Is tablet:", $device.isTablet);
console.log("User agent:", $device.userAgent);
</script>

<template>
  <NuxtLayout>
    <NuxtPage />
  </NuxtLayout>
</template>

<style scoped></style>
