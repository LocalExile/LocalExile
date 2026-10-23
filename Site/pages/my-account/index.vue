<script setup>
// Этот файл может содержать логику для страницы
definePageMeta({
  layout: "default", // Имя лейаута, которое мы используем
});

useHead({
  title: "Path of Exile - A free Online Action RPG",
});
</script>

<template>
  <div class="max-w-[980px] mx-auto p-2 text-white mt-[155px] px-10">
    <div class="bg-black/80 flex flex-col gap-3">
      <NuxtImg src="/img/banner.jpg" class="w-[100%]" />

      <!-- <div class="bg-gray-800 p-4">
        <h2 class="text-3xl font-bold mb-10">Welcome to My Nuxt Project</h2>
        <p>Это текст с шрифтом FrizQuadrataCTT.</p>
      </div> -->

      <div class="flex flex-row gap-6">
        <div class="left-block flex flex-col gap-4 w-[65%]">
          <div class="">
            <NuxtImg src="/img/News.jpg" class="w-[100%]" />
          </div>
          <span class="text-[#dfcf99] text-[32px] w-full px-2">Новости</span>
          <div class="bg-green-600">1</div>
          <div class="bg-red-600">1</div>
          <div class="bg-blue-600">1</div>
          <div class="bg-yellow-600">1</div>
        </div>
        <div class="right-block flex flex-col flex-1 gap-1">
          <div class="relative">
            <NuxtImg src="/img/deal-background-large.png" class="w-full" />
            <div class="absolute inset-0 top-[99px] px-[10px]">
              <NuxtImg
                src="/img/deal-item-background-dark.jpg"
                class="w-full h-[94%]"
              />
            </div>
          </div>
          <div class="relative">
            <NuxtImg src="/img/deal-background-large.png" class="w-full" />
            <div class="absolute inset-0 top-[99px] px-[10px]">
              <NuxtImg
                src="/img/deal-item-background-dark.jpg"
                class="w-full h-[94%]"
              />
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
@font-face {
  font-family: "FrizQuadrataCTT";
  src: url("/fonts/FrizQuadrataCTT-Regular.ttf") format("truetype");
  font-display: swap;
}

span {
  font-family: "FrizQuadrataCTT", serif;
}
</style>
