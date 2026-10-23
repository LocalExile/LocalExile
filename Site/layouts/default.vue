<script setup>
import Header from "~/components/Header.vue";
import Footer from "~/components/Footer.vue";
</script>

<template>
  <div class="relative min-h-screen w-full flex flex-col">
    <!-- Фон всегда фиксирован -->
    <div
      class="absolute top-0 left-[10%] w-[90%] h-[100%] bg-[url('/img/keepersoftheflame-bg.jpg')] bg-contain bg-no-repeat bg-fixed bg-center"
    ></div>

    <Header class="relative mt-[-16px] mb-[50px]" />

    <main class="relative flex-grow w-[1650px] mt-[-255px] h-[100%] mx-auto">
      <slot />
    </main>

    <Footer class="relative" />
  </div>
</template>

<style></style>
