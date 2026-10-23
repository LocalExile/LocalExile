<script setup lang="ts"></script>

<template>
  <header class="text-white p-4">
    <div class="w-full h-[100%] mx-auto flex justify-between items-center">
      <div
        class="text-md flex flex-row gap-4 h-10 px-5 items-center ml-[-20px] mt-[-90px]"
      >
        <NuxtLink class="text-sky-500 hover:text-sky-300 text-[13px]" to="/"
          >ВЕРНУТЬСЯ НА ГЛАВНЫЙ САЙТ
        </NuxtLink>
      </div>
      <div class="flex gap-1.5 flex-row mt-[-90px]">
        <NuxtImg src="/img/lang/GB.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/BR.png" class="opacity-50 w-5" />
        <NuxtLink to="/" class="">
          <NuxtImg src="/img/lang/RU.png" class="w-5" />
        </NuxtLink>
        <NuxtImg src="/img/lang/TH.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/DE.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/FR.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/ES.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/JP.png" class="opacity-50 w-5" />
        <NuxtImg src="/img/lang/KR.png" class="opacity-50 w-5" />
      </div>
    </div>

    <div class="max-w-[980px] mx-auto flex flex-col items-center mt-[-100px]">
      <NuxtLink to="/" class="">
        <NuxtImg
          src="/img/keepersoftheflamelogo.png"
          class="w-[275px] h-[100%]"
        />
      </NuxtLink>
    </div>
  </header>
</template>

<style scoped>
@font-face {
  font-family: "FrizQuadrataCTT";
  src: url("/fonts/FrizQuadrataCTT-Regular.ttf") format("truetype");
  font-display: swap;
}

div {
  font-family: "FrizQuadrataCTT", serif;
}
</style>
