<script setup>
definePageMeta({
  layout: "login", // Имя лейаута, которое мы используем
});

useHead({
  title: "Path of Exile",
});

// const emit = defineEmits(["update:modelValue"]);
// defineEmits(["update:email"]);
// const focusedEmail = ref(false);
</script>

<template>
  <client-only>
    <div
      class="mx-auto w-full flex items-center justify-center text-center mt-10 absolute"
    >
      <NuxtImg
        src="/img/nine-palace-top.png"
        class="absolute top-[-35px] w-36"
      />
      <NuxtImg
        src="/img/nine-palace-horn-top.png"
        class="absolute top-[-25px] left-[-10px] w-22 h-[122px]"
      />
      <NuxtImg
        src="/img/nine-palace-horn-top.png"
        class="absolute top-[-25px] right-[-10px] w-22 h-[122px] scale-x-[-1]"
      />

      <NuxtImg
        src="/img/nine-palace-horn-bottom.png"
        class="absolute bottom-[-75px] left-[-10px] w-18 h-[122px]"
      />

      <NuxtImg
        src="/img/nine-palace-horn-bottom.png"
        class="absolute bottom-[-75px] right-[-10px] w-18 h-[122px] scale-x-[-1]"
      />

      <NuxtImg
        src="/img/nine-palace-bottom.png"
        class="absolute bottom-[-100px] w-18 h-[105px] scale-x-[-1]"
      />
      <div
        class="bg-[url(/img/nine-palace-bg.png)] bg-no-repeat bg-center w-[512px] h-[475px] flex flex-col items-center justify-center text-white bg-[length:100%_100%]"
      >
        <div class="text-[30px] text-[#efe4ad]">Требуется подтверждение</div>

        <NuxtImg src="/img/login-title-zs.png" class="w-[70%] mt-[-5px]" />

        <div class="flex flex-col items-center w-full px-6">
          <div class="text-[#b8b8b8] mt-4 text-[16px] text-left">
            Чтобы продолжить, необходимо подтвердить вашу почту.
          </div>

          <div class="text-[#b8b8b8] mt-4 text-[16px] text-left">
            Вы должны получить письмо с подтверждением и ссылкой для регистрации
            в ближайшее время.
          </div>
        </div>

        <NuxtLink to="/login" class="w-[85%] mt-5">
          <div
            class="bg-[url(/img/button-primary.png)] bg-no-repeat bg-center w-[100%] h-[95px] opacity-90 hover:opacity-100 hover:cursor-pointer text-black/90 text-[26px] font-semibold flex items-center justify-center pb-2 bg-[length:93%_80%]"
          >
            Назад на сайт
          </div>
        </NuxtLink>
      </div>
    </div>
  </client-only>
</template>

<style scoped>
@font-face {
  font-family: "Cinzel";
  font-display: swap;
  src: url("/fonts/cinzel_regular.ttf");
}

div {
  font-family: "Cinzel";
}
</style>
