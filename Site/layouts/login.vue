<script setup>
import Header from "~/components/HeaderLogin.vue";
</script>

<template>
  <div class="h-[100vh] w-full flex flex-col">
    <!-- Фон всегда фиксирован -->
    <div
      class="absolute top-0 left-[10%] w-[90%] h-[100vh] bg-[url('/img/keepersoftheflame-bg.jpg')] bg-contain bg-no-repeat bg-fixed bg-center"
    ></div>

    <Header class="relative mb-[80px]" />

    <main class="relative flex-grow w-[530px] mx-auto">
      <slot />
    </main>
  </div>
</template>

<style></style>
