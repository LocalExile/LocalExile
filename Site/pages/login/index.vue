<script setup>
import { ref } from "vue";

definePageMeta({
  layout: "login", // Имя лейаута, которое мы используем
  // Если нужно защитить от уже авторизованных пользователей
  middleware: ["guest"],
});

useHead({
  title: "Path of Exile - Авторизация",
});

// Используем наш auth компосабл
const auth = useAuth();

const email = ref("admin"); // Предзаполняем для удобства
const password = ref("admin"); // Предзаполняем для удобства
const error = ref("");
const loading = ref(false);
const rememberMe = ref(false);

// Удаляем старые props если они не нужны
const focusedEmail = ref(false);
const focusedPassword = ref(false);

async function loginGo() {
  error.value = "";
  loading.value = true;

  try {
    // Используем наш auth компосабл для входа
    const result = await auth.login(email.value, password.value);

    if (result.success) {
      // Получаем redirect из query или переходим на главную
      const route = useRoute();
      const redirect = route.query.redirect || "/";

      // Навигация
      await navigateTo(redirect);
    } else {
      // Показываем ошибку
      error.value = result.error || "Ошибка авторизации";
    }
  } catch (err) {
    error.value = "Произошла ошибка при входе";
    console.error("Login error:", err);
  } finally {
    loading.value = false;
  }
}

// Быстрый вход с тестовыми данными (опционально)
async function quickLogin() {
  email.value = "admin";
  password.value = "admin";
  await loginGo();
}

// Если пользователь уже авторизован - перенаправляем
const { isAuthenticated } = auth;
if (isAuthenticated.value) {
  await navigateTo("/");
}
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
        <div class="text-[30px] text-[#efe4ad]">Авторизоваться</div>

        <NuxtImg src="/img/login-title-zs.png" class="w-[70%] mt-[-5px]" />

        <div class="text-[#9d9d9d] mt-4 text-[17px]">
          Войдите в вашу учетную запись
          <span class="font-semibold">Path of Exile</span>
        </div>

        <!-- Сообщение об ошибке -->
        <div
          v-if="error"
          class="mt-2 mx-6 p-2 bg-red-900/50 border border-red-700 text-[#ff6b6b] text-sm rounded"
        >
          {{ error }}
        </div>

        <form @submit.prevent="loginGo" class="w-full mt-3">
          <div class="relative mx-6 mb-3">
            <input
              type="text"
              autocomplete="username"
              v-model="email"
              @focus="focusedEmail = true"
              @blur="focusedEmail = false"
              :disabled="loading"
              class="w-full px-4 py-3 bg-black text-[#fff1d1] border-[1px] border-[#4a3d22] focus:outline-none focus:border-[#7e673a] transition-colors duration-200 disabled:opacity-70"
              placeholder="Логин"
            />
          </div>

          <div class="relative mx-6 mb-3">
            <input
              type="password"
              v-model="password"
              @focus="focusedPassword = true"
              @blur="focusedPassword = false"
              :disabled="loading"
              class="w-full px-4 py-3 bg-black text-[#fff1d1] border-[1px] border-[#4a3d22] focus:outline-none focus:border-[#7e673a] transition-colors duration-200 disabled:opacity-70"
              placeholder="Пароль"
            />
          </div>

          <div class="flex flex-row w-full justify-between items-center px-7">
            <div class="flex items-center">
              <input
                type="checkbox"
                id="remember"
                v-model="rememberMe"
                :disabled="loading"
                class="appearance-none w-4 h-4 bg-black border border-gray-300 rounded dark:bg-black dark:border-[#efe4ad69] relative checked:before:content-['✓'] checked:before:absolute checked:before:left-1/2 checked:before:top-1/2 checked:before:-translate-x-1/2 checked:before:-translate-y-1/2 checked:before:text-[#efe4ad] checked:before:text-[10px] checked:before:font-bold"
              />
              <label
                for="remember"
                class="ml-2 text-sm font-medium text-[#e7dfb3ea]"
                >Запомнить меня</label
              >
            </div>
            <div class="flex items-center">
              <NuxtLink
                to="/lost-password"
                class="text-[#e7dfb3ea] text-sm hover:text-[#fff7cbea] hover:cursor-pointer"
              >
                Забыл пароль?
              </NuxtLink>
            </div>
          </div>

          <div class="flex w-full items-center justify-center">
            <button
              type="submit"
              :disabled="loading"
              class="bg-[url(/img/button-primary.png)] bg-no-repeat bg-center w-[100%] h-[95px] opacity-90 hover:opacity-100 disabled:opacity-50 hover:cursor-pointer text-black/90 text-[26px] font-semibold flex items-center justify-center pb-2 bg-[length:93%_80%] disabled:cursor-not-allowed"
            >
              {{ loading ? "Вход..." : "Продолжить" }}
            </button>
          </div>
        </form>

        <!-- Кнопка быстрого входа -->
        <!-- <div class="mt-4">
          <button
            @click="quickLogin"
            :disabled="loading"
            class="px-4 py-2 text-sm bg-[#4a3d22]/50 text-[#e7dfb3ea] border border-[#4a3d22] hover:bg-[#4a3d22]/70 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Тестовый вход (admin / admin)
          </button>
        </div> -->

        <div class="text-[#efe4ad] mt-1">
          Нет учетной записи?
          <NuxtLink
            to="/create"
            class="text-[#9d9d9d] font-semibold hover:text-[#fff7cbea] hover:cursor-pointer"
            >Создать.</NuxtLink
          >
        </div>
        <NuxtLink to="/" class="mt-1">
          <div
            class="text-[#e7dfb3ea] text-sm hover:text-[#fff7cbea] hover:cursor-pointer"
          >
            Назад
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
