<script setup>
definePageMeta({
  layout: "login", // Имя лейаута, которое мы используем
});

useHead({
  title: "Path of Exile",
});

const email = ref("");

const props = defineProps({
  inputEmailValue: {
    type: String,
    default: "",
  },

  typeLogin: {
    type: String,
    default: "text",
  },

  placeholderEmail: {
    type: String,
    default: "Эл. почта",
  },

  id: {
    type: String,
    default: "input-" + Math.random().toString(36).substr(2, 9),
  },
});

// const emit = defineEmits(["update:modelValue"]);
// defineEmits(["update:email"]);
const focusedEmail = ref(false);
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
        <div class="text-[30px] text-[#efe4ad]">Регистрация</div>

        <NuxtImg src="/img/login-title-zs.png" class="w-[70%] mt-[-5px]" />

        <div class="text-[#9d9d9d] mt-4 text-[17px]">
          Учетная запись
          <span class="font-semibold">Path of Exile</span>
        </div>

        <form action="" class="w-full mt-5">
          <div class="relative mx-6 mb-3">
            <input
              :id="id"
              :type="typeLogin"
              v-model="email"
              @focus="focusedEmail = true"
              @blur="focusedEmail = false"
              class="w-full px-4 py-3 bg-black text-[#fff1d1] border-[1px] border-[#4a3d22] focus:outline-none focus:border-[#7e673a] transition-colors duration-200"
            />
            <label
              :for="id"
              :class="[
                'absolute left-4 transition-all duration-200 pointer-events-none',
                email || focusedEmail
                  ? 'top-1 text-xs text-[#fff1d1b4] -translate-y-[3px]'
                  : 'top-1/2 transform -translate-y-[12px] text-[#fff1d1]/70 text-base',
              ]"
            >
              {{ placeholderEmail }}
            </label>
          </div>

          <!-- <div class="relative mx-6 mb-3">
          <input
            :id="id"
            :type="typePassword"
            v-model="password"
            @focus="focusedPassword = true"
            @blur="focusedPassword = false"
            class="w-full px-4 py-3 bg-black text-[#fff1d1] border-[1px] border-[#4a3d22] focus:outline-none focus:border-[#7e673a] transition-colors duration-200"
          />
          <label
            :for="id"
            :class="[
              'absolute left-4 transition-all duration-200 pointer-events-none',
              password || focusedPassword
                ? 'top-1 text-xs text-[#fff1d1b4] -translate-y-[3px]'
                : 'top-1/2 transform -translate-y-[12px] text-[#fff1d1]/70 text-base',
            ]"
          >
            {{ placeholderPassword }}
          </label>
        </div> -->

          <div class="flex flex-col w-full px-7 gap-4">
            <div class="flex items-center">
              <input
                type="checkbox"
                id="remember"
                name="remember"
                value="remember"
                class="appearance-none w-4 h-4 bg-black border border-gray-300 rounded dark:bg-black dark:border-[#efe4ad69] relative checked:before:content-['✓'] checked:before:absolute checked:before:left-1/2 checked:before:top-1/2 checked:before:-translate-x-1/2 checked:before:-translate-y-1/2 checked:before:text-[#efe4ad] checked:before:text-[10px] checked:before:font-bold"
              />
              <label
                for="remember"
                class="ml-2 text-sm font-medium text-[#e7dfb3ea]"
                >Я принимаю
                <span class="font-semibold ml-1 text-[#9d9d9d]"
                  >Условия использования</span
                >
              </label>
            </div>

            <div class="flex items-center">
              <input
                type="checkbox"
                id="remember2"
                name="remember2"
                value="remember2"
                class="appearance-none w-4 h-4 bg-black border border-gray-300 rounded dark:bg-black dark:border-[#efe4ad69] relative checked:before:content-['✓'] checked:before:absolute checked:before:left-1/2 checked:before:top-1/2 checked:before:-translate-x-1/2 checked:before:-translate-y-1/2 checked:before:text-[#efe4ad] checked:before:text-[10px] checked:before:font-bold"
              />
              <label
                for="remember2"
                class="ml-2 text-sm font-medium text-[#e7dfb3ea] w-[90%] text-left"
                >Я принимаю
                <span class="font-semibold ml-1 text-[#9d9d9d]"
                  >Уведомление о конфиденциальности и Уведомление в отношении
                  файлов Cookies</span
                >
              </label>
            </div>
          </div>

          <div class="flex w-full items-center justify-center">
            <NuxtLink to="/create-complete" class="w-[100%]">
              <div
                class="bg-[url(/img/button-primary.png)] bg-no-repeat bg-center w-[100%] h-[95px] opacity-90 hover:opacity-100 hover:cursor-pointer text-black/90 text-[26px] font-semibold flex items-center justify-center pb-2 bg-[length:93%_80%]"
              >
                Продолжить
              </div>
            </NuxtLink>
          </div>
        </form>

        <div class="text-[#efe4ad]">
          Есть учетная запись?
          <NuxtLink
            to="/login"
            class="text-[#9d9d9d] font-semibold hover:text-[#fff7cbea] hover:cursor-pointer"
            >Войти.</NuxtLink
          >
        </div>
        <NuxtLink to="/" class="mt-1">
          <div
            class="text-[#e7dfb3ea] text-sm hover:text-[#fff7cbea] hover:cursor-pointer"
          >
            Назад
          </div></NuxtLink
        >
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
