<script setup lang="ts">
import { on } from "node:cluster";

const auth = useAuth();

// alert(auth.user.value);

// Состояния для отслеживания наведения на каждый пункт меню
const hoverStates = reactive({
  item1: false,
  item2: false,
  item3: false,
  item4: false,
  item5: false,
  item6: false,
});

// Данные для выпадающих меню
const dropdownItems = [
  {
    id: "item1",
    items: [
      { label: "Последние новости", href: "/news/latest" },
      { label: "Обновления", href: "/news/updates" },
      { label: "События", href: "/news/events" },
      { label: "Анонсы", href: "/news/announcements" },
    ],
  },
  {
    id: "item2",
    items: [
      { label: "Новичкам", href: "/guides/beginner" },
      { label: "Классы", href: "/guides/classes" },
      { label: "Предметы", href: "/guides/items" },
      { label: "Прохождение", href: "/guides/walkthrough" },
    ],
  },
  {
    id: "item3",
    items: [
      { label: "Обсуждения", href: "/forum/discussions" },
      { label: "Помощь", href: "/forum/help" },
      { label: "Клубы", href: "/forum/clubs" },
      { label: "Отзывы", href: "/forum/feedback" },
    ],
  },
  {
    id: "item4",
    items: [
      { label: "Предметы", href: "/shop/items" },
      { label: "Услуги", href: "/shop/services" },
      { label: "Валюта", href: "/shop/currency" },
      { label: "Акции", href: "/shop/sales" },
    ],
  },
  {
    id: "item5",
    items: [
      { label: "FAQ", href: "/support/faq" },
      { label: "Баги", href: "/support/bugs" },
      { label: "Контакты", href: "/support/contacts" },
      { label: "Политика", href: "/support/policy" },
    ],
  },
  {
    id: "item6",
    items: [
      { label: "Клиент", href: "/downloads/client" },
      { label: "Патчи", href: "/downloads/patches" },
      { label: "Дополнения", href: "/downloads/addons" },
      { label: "Драйверы", href: "/downloads/drivers" },
    ],
  },
];

const userAuth = ref(null);
async function getAuth() {
  if (auth.user.value) {
    userAuth.value = true;
    return true;
  }
  userAuth.value = false;
  return false;
}

onMounted(async () => {
  await getAuth();
});
</script>

<template>
  <header class="text-white p-4">
    <div class="max-w-[925px] mx-auto flex justify-between items-center">
      <div class="mt-5 ml-5" v-if="userAuth === null">
        <div role="status">
          <div role="status">
            <svg
              aria-hidden="true"
              class="w-8 h-8 text-gray-600 animate-spin fill-amber-100"
              viewBox="0 0 100 101"
              fill="none"
              xmlns="http://www.w3.org/2000/svg"
            >
              <!-- Внешнее кольцо: цвет из text-* класса -->
              <path
                d="M100 50.5908C100 78.2051 77.6142 100.591 50 100.591C22.3858 100.591 0 78.2051 0 50.5908C0 22.9766 22.3858 0.59082 50 0.59082C77.6142 0.59082 100 22.9766 100 50.5908ZM9.08144 50.5908C9.08144 73.1895 27.4013 91.5094 50 91.5094C72.5987 91.5094 90.9186 73.1895 90.9186 50.5908C90.9186 27.9921 72.5987 9.67226 50 9.67226C27.4013 9.67226 9.08144 27.9921 9.08144 50.5908Z"
                fill="currentColor"
              />
              <!-- Вращающаяся часть: цвет из fill-* класса -->
              <path
                d="M93.9676 39.0409C96.393 38.4038 97.8624 35.9116 97.0079 33.5539C95.2932 28.8227 92.871 24.3692 89.8167 20.348C85.8452 15.1192 80.8826 10.7238 75.2124 7.41289C69.5422 4.10194 63.2754 1.94025 56.7698 1.05124C51.7666 0.367541 46.6976 0.446843 41.7345 1.27873C39.2613 1.69328 37.813 4.19778 38.4501 6.62326C39.0873 9.04874 41.5694 10.4717 44.0505 10.1071C47.8511 9.54855 51.7191 9.52689 55.5402 10.0491C60.8642 10.7766 65.9928 12.5457 70.6331 15.2552C75.2735 17.9648 79.3347 21.5619 82.5849 25.841C84.9175 28.9121 86.7997 32.2913 88.1811 35.8758C89.083 38.2158 91.5421 39.6781 93.9676 39.0409Z"
                fill="currentFill"
              />
            </svg>
            <span class="sr-only">Loading...</span>
          </div>
        </div>
      </div>
      <div
        v-if="!userAuth && userAuth !== null"
        class="text-md flex flex-row gap-4 bg-[url(/img/ui-sprite.png)] bg-[-2px_-95px] bg-no-repeat h-10 px-7 items-center ml-[-20px]"
      >
        <NuxtLink
          class="text-sky-500 hover:text-sky-300 text-[11px]"
          to="/login"
          >ВОЙТИ</NuxtLink
        >
        <NuxtLink class="text-sky-500 hover:text-sky-300 text-[11px]" to="/"
          >СОЗДАТЬ АККАУНТ</NuxtLink
        >
        <NuxtLink class="text-sky-500 hover:text-sky-300 text-[11px]" to="/"
          >ТЕХПОДДЕРЖКА</NuxtLink
        >
      </div>
      <div class="h-10 ml-[-10px]" v-if="userAuth && userAuth !== null">
        <div
          class="w-[350px] h-11 bg-[url(/img/ui-sprite.png)] bg-[-2px_-95px] bg-no-repeat flex flex-col gap-0 text-[13px] px-6 pt-0.5"
        >
          <div class="flex flex-row gap-3 text-[#a38b63] title">
            Вошли как
            <div
              class="bg-[url(/img/achievement-Chayula.png)] bg-[-90px_-103px] bg-no-repeat w-[20px] h-[20px] top-[-2px] relative"
            ></div>
            <span
              class="font-semibold hover:text-[#f0e4b3be] hover:cursor-pointer"
              >adventure1#1000</span
            >

            <div
              class="flex flex-row gap-1 hover:cursor-pointer hover:text-[#f0e4b3be]"
            >
              <div class="">10000</div>
              <div
                class="bg-[url(/img/ui-sprite.png)] bg-[1px_-140px] bg-no-repeat w-[17px] h-[17x]"
              ></div>
            </div>
          </div>
          <div class="flex flex-row gap-4 text-[11px]">
            <div
              @click="auth.logout() && getAuth()"
              class="text-[#379eff] uppercase title hover:cursor-pointer hover:text-[#79beff]"
            >
              Выйти
            </div>
            <div
              class="text-[#fcff37] hover:cursor-pointer hover:text-[#feffba] font-bold uppercase title"
            >
              0 Сообщений
            </div>
            <div
              class="text-[#379eff] uppercase title hover:cursor-pointer hover:text-[#79beff]"
            >
              Техподдержка
            </div>
          </div>
        </div>
      </div>
      <div class="flex gap-1.5 flex-row mt-[-10px]">
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

    <div class="max-w-[980px] mx-auto flex flex-col items-center mt-[5px]">
      <NuxtImg
        src="/img/header_no_logo.png"
        class="mt-[60px] z-[10] w-[100%]"
      />

      <NuxtLink to="/" class="z-[20]">
        <NuxtImg
          src="/img/keepersoftheflamelogo.png"
          class="mt-[-250px] w-[275px] h-[100%]"
        />
      </NuxtLink>

      <!-- Навигационное меню с выпадающими списками -->

      <div class="flex flex-row text-left w-full z-[10]">
        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item1 = true"
          @mouseleave="hoverStates.item1 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[100px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-244px_-117px] bg-no-repeat h-[27px] w-[94px] hover:cursor-pointer hover:bg-[-244px_-167px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item1"
            class="absolute top-full mt-[-28px] ml-[50px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[0].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>

        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item2 = true"
          @mouseleave="hoverStates.item2 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[20px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-15px_-117px] bg-no-repeat h-[27px] w-[95px] hover:cursor-pointer hover:bg-[-15px_-167px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item2"
            class="absolute top-full mt-[-28px] ml-[-20px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[1].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>

        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item3 = true"
          @mouseleave="hoverStates.item3 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[20px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-131px_-18px] bg-no-repeat h-[27px] w-[95px] hover:cursor-pointer hover:bg-[-131px_-68px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item3"
            class="absolute top-full mt-[-28px] ml-[-20px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[2].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>

        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item4 = true"
          @mouseleave="hoverStates.item4 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[133px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-12px_-16px] bg-no-repeat h-[27px] w-[95px] hover:cursor-pointer hover:bg-[-12px_-66px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item4"
            class="absolute top-full mt-[-28px] ml-[85px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[3].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>

        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item5 = true"
          @mouseleave="hoverStates.item5 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[19px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-130px_-116px] bg-no-repeat h-[27px] w-[95px] hover:cursor-pointer hover:bg-[-130px_-166px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item5"
            class="absolute top-full mt-[-28px] ml-[-20px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[4].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>

        <div
          class="group h-full relative"
          @mouseenter="hoverStates.item6 = true"
          @mouseleave="hoverStates.item6 = false"
        >
          <NuxtLink to="/" class="mt-[-69px] ml-[19px] block py-2">
            <div
              class="bg-[url(/img/nav-button-sprite-ru_RU.png)] bg-[-250px_-16px] bg-no-repeat h-[27px] w-[96px] hover:cursor-pointer hover:bg-[-250px_-66px]"
            ></div>
          </NuxtLink>

          <!-- Выпадающее меню ПРЯМО ВНУТРИ того же div, что и кнопка -->
          <div
            v-if="hoverStates.item6"
            class="absolute top-full mt-[-28px] ml-[-20px] min-w-[180px] z-50"
          >
            <div
              class="bg-black/95 border border-gray-700 rounded-sm shadow-lg"
            >
              <NuxtLink
                v-for="item in dropdownItems[5].items"
                :key="item.href"
                :to="item.href"
                class="block px-4 py-3 text-sm text-[#656565] hover:bg-gray-800 hover:text-white transition-colors duration-150 border-b border-gray-800 last:border-b-0"
              >
                {{ item.label }}
              </NuxtLink>
            </div>
          </div>
        </div>
      </div>

      <div
        class="w-full flex flex-row justify-between z-[30] mt-[-205px] mb-[122px] px-2"
      >
        <div
          v-if="userAuth"
          class="mt-0 ml-7 w-[300px] h-[100px] bg-[url(/img/inventory-sprite.png)] bg-[0px_0px] bg-no-repeat items-center mb-[-17px] block"
        >
          <div class="flex flex-col gap-0 ml-1">
            <div class="flex flex-row gap-2">
              <div
                class="w-[96px] h-[80px] bg-[url(/img/inventory-sprite.png)] bg-[-223px_-260px] bg-no-repeat"
              ></div>

              <div class="flex flex-col gap-0 flex-1 text-[16px]">
                <div class="h-10 flex items-center text-[#a38b63]">
                  Administrator
                </div>
                <div class="h-10 flex items-center text-[#a38b63]">
                  Уровень 100 Маг стихий
                </div>
              </div>
            </div>

            <div class="mt-[-4px] flex flex-row gap-6">
              <div
                class="text-[#379eff] text-[13px] mt-[1px] ml-[2px] hover:cursor-pointer hover:text-[#79beff]"
              >
                сменить героя
              </div>
              <div class="text-[#948a78a9]">Лига Keepers</div>
            </div>
          </div>
        </div>
        <div class="" v-if="!userAuth"></div>

        <div class="mt-[-5px] mr-2.5">
          <div class="">
            <NuxtLink to="/" class="mt-[-61px]">
              <div
                class="bg-[url(/img/play-free-ru_RU.png)] bg-[-1px_-81px] bg-no-repeat h-[65px] w-[265px] hover:cursor-pointer hover:bg-[-1px_-5px]"
              ></div>
            </NuxtLink>
          </div>

          <div class="flex flex-row gap-1 justify-center">
            <NuxtLink to="/" class="">
              <div
                class="bg-[url(/img/platform-button.png)] bg-[-73px_-32px] bg-no-repeat h-[23px] w-[65px] hover:cursor-pointer hover:bg-[-3px_-61px]"
              ></div>
            </NuxtLink>
            <NuxtLink to="/" class="">
              <div
                class="bg-[url(/img/platform-button.png)] bg-[-2px_-3px] bg-no-repeat h-[23px] w-[65px] hover:cursor-pointer hover:bg-[-2px_-32px]"
              ></div>
            </NuxtLink>

            <NuxtLink to="/" class="">
              <div
                class="bg-[url(/img/platform-button.png)] bg-[-142px_-90px] bg-no-repeat h-[23px] w-[65px] hover:cursor-pointer hover:bg-[-2px_-119px]"
              ></div>
            </NuxtLink>

            <NuxtLink to="/" class="">
              <div
                class="bg-[url(/img/platform-button.png)] bg-[-2px_-90px] bg-no-repeat h-[23px] w-[65px] hover:cursor-pointer hover:bg-[-72px_-90px]"
              ></div>
            </NuxtLink>
          </div>
        </div>
      </div>
    </div>

    <!-- <div
      class="max-w-[900px] mx-auto flex justify-end text-white mt-[-100px] z-[30]"
    >
      
    </div> -->
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

.title {
  font-family: "Arial", sans-serif !important;
}

/* Стили для выпадающего меню */
.relative {
  display: inline-block;
}

/* Улучшенные стили для выпадающего меню */
.bg-black\/95 {
  background-color: rgba(0, 0, 0, 0.95);
}

/* Плавное появление (опционально) */
.absolute {
  animation: fadeIn 0.2s ease-in-out;
}

@keyframes fadeIn {
  from {
    opacity: 0;
    transform: translateY(-10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
</style>
