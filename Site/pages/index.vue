<script setup>
// Этот файл может содержать логику для страницы
definePageMeta({
  layout: "default", // Имя лейаута, которое мы используем
});

useHead({
  title: "Path of Exile - A free Online Action RPG",
});

const auth = useAuth();

const videoId = "ARFSbodxJZU"; // Замените на ID вашего видео
const videoTitle = "Название видео";
const isPlaying = ref(false);

const playVideo = () => {
  isPlaying.value = true;
};
</script>

<template>
  <div class="max-w-[980px] mx-auto p-2 text-white mt-[155px] px-10">
    <div class="bg-black/80 flex flex-col gap-3">
      <NuxtImg src="/img/banner.jpg" class="w-[100%] hover:cursor-pointer" />

      {{ auth }}
      <!-- {{ user }} -->

      <!-- <div class="bg-gray-800 p-4">
        <h2 class="text-3xl font-bold mb-10">Welcome to My Nuxt Project</h2>
        <p>Это текст с шрифтом FrizQuadrataCTT.</p>
      </div> -->

      <div class="flex flex-row gap-6">
        <div class="left-block flex flex-col gap-4 w-[65%]">
          <div class="">
            <NuxtImg
              src="/img/News.jpg"
              class="w-[100%] hover:cursor-pointer"
            />
          </div>
          <span class="text-[#dfcf99] text-[32px] w-full px-2">Новости</span>

          <div
            class="flex flex-col gap-2 w-full h-full items-center justify-center ml-1"
          >
            <div
              class="flex flex-col gap-2 border border-[#141414] p-2 bg-[#0c0b0b]"
            >
              <div class="flex flex-row justify-between w-full">
                <div
                  class="w-[65%] text-[#dfcf99] font-bold text-[18px] flex flex-col -space-y-5 leading-tight"
                >
                  Обновление 0.4.0 - Path of Exile 2: Последний из друидов
                </div>
                <div
                  class="text-[#585858] italic flex-1 text-[14px] pt-1 text-right"
                >
                  4 дек. 2025 г., 11:15:31
                </div>
              </div>
              <div class="w-full text-[#a38b63] text-[14px] mt-"></div>
            </div>

            <div
              class="flex flex-col gap-2 border border-[#141414] p-2 bg-[#0c0b0b]"
            >
              <div class="flex flex-row justify-between w-full">
                <div
                  class="w-[65%] text-[#dfcf99] font-bold text-[18px] flex flex-col -space-y-5 leading-tight"
                >
                  Концепт-арт - Лига Хранители Пламени и Пропуск в хранилище
                  Кирака
                </div>
                <div
                  class="text-[#585858] italic flex-1 text-[14px] pt-1 text-right"
                >
                  24 нояб. 2025 г., 09:07:55
                </div>
              </div>
              <div class="w-full text-[#a38b63] text-[14px] mt-">
                В сегодняшней новости мы публикуем некоторый концепт-арт из лиги
                Хранители Пламени и Пропуска в хранилище Кирака, который
                включает эксклюзивный набор доспехов в тематике древней сущности
                Атласа – Мейвен. Взгляните на него здесь!
              </div>
            </div>

            <div
              class="flex flex-col gap-2 border border-[#141414] p-2 bg-[#0c0b0b]"
            >
              <div class="flex flex-row justify-between w-full">
                <div
                  class="w-[65%] text-[#dfcf99] font-bold text-[18px] flex flex-col -space-y-5 leading-tight"
                >
                  Победители события по убийству убер-босса Хранителей Пламени
                </div>
                <div
                  class="text-[#585858] italic flex-1 text-[14px] pt-1 text-right"
                >
                  23 нояб. 2025 г., 08:52:57
                </div>
              </div>
              <div class="w-full text-[#a38b63] text-[14px] mt-">
                Событие по убийству убер-босса Хранителей Пламени завершилось!
                Гонка за первые 5 мест была напряжённой, так что мы поздравляем
                тех, кто смог добраться до конца и победить убер-Воплощение
                ужаса. Узнайте о победителях и наградах из сегодняшней новости.
              </div>
            </div>

            <div class="w-full text-left">
              <NuxtLink
                to="/news"
                class="text-[#585858] mt-3 text-[14px] underline"
                >Архив новостей</NuxtLink
              >
            </div>
          </div>

          <!-- <div class="bg-red-600">1</div> -->

          <div class="">
            <div class="video-wrapper relative w-full max-w-4xl mx-auto">
              <!-- Превью с кнопкой -->
              <div
                class="relative cursor-pointer group"
                @click="playVideo"
                v-if="!isPlaying"
              >
                <NuxtImg
                  :src="`https://img.youtube.com/vi/${videoId}/maxresdefault.jpg`"
                  :alt="videoTitle"
                  class="w-full aspect-video rounded-lg shadow-lg object-cover"
                  loading="lazy"
                />
                <div class="absolute inset-0 flex items-center justify-center">
                  <div
                    class="w-20 h-20 bg-red-600 rounded-full flex items-center justify-center transform group-hover:scale-110 transition-transform"
                  >
                    <svg
                      class="w-10 h-10 text-white ml-1"
                      fill="currentColor"
                      viewBox="0 0 24 24"
                    >
                      <path d="M8 5v14l11-7z" />
                    </svg>
                  </div>
                </div>
              </div>

              <!-- Iframe (показывается после клика) -->
              <div v-if="isPlaying" class="mt-4">
                <iframe
                  :src="`https://www.youtube.com/embed/${videoId}?autoplay=1`"
                  title="YouTube video player"
                  frameborder="0"
                  allow="
                    accelerometer;
                    autoplay;
                    clipboard-write;
                    encrypted-media;
                    gyroscope;
                    picture-in-picture;
                  "
                  allowfullscreen
                  class="w-full aspect-video rounded-lg shadow-lg"
                />
              </div>
            </div>
          </div>

          <!-- <div class="bg-blue-600">1</div> -->
          <!-- <div class="bg-yellow-600">1</div> -->
        </div>
        <div class="right-block flex flex-col flex-1 gap-3">
          <div
            class="relative bg-[url('/img/deal-background-large.png')] h-[240px] bg-no-repeat bg-cover"
          >
            <div
              class="flex flex-col w-full h-full items-center justify-center"
            >
              <div
                class="flex w-full h-20 items-center justify-center text-[#ffe86e] text-[27px]"
              >
                <div
                  class="flex flex-col justify-center items-center -space-y-3 pt-2"
                >
                  <span class="text-[22px]">Сделка дня</span>
                  <span class="text-[28px]">Скидка 30%</span>
                </div>
              </div>
              <div
                class="flex-1 mb-2.5 w-full mt-2 px-5 py-2 bg-[url('/img/deal-item-background-dark.jpg')] bg-[50%_100%] bg-no-repeat flex items-center justify-center h-full"
              >
                <span class="text-center flex items-center justify-center"
                  >Описательный текст и ссылка внутри новости</span
                >
              </div>
            </div>
          </div>
          <div
            class="relative bg-[url('/img/deal-background-large.png')] h-[240px] bg-no-repeat bg-cover"
          >
            <div
              class="flex flex-col w-full h-full items-center justify-center"
            >
              <div
                class="flex w-full h-20 items-center justify-center text-[#ffe86e] text-[27px]"
              >
                <div
                  class="flex flex-col justify-center items-center -space-y-3 pt-2"
                >
                  <span class="text-[22px]">Сделка дня</span>
                  <span class="text-[28px]">Скидка 20%</span>
                </div>
              </div>
              <div
                class="flex-1 mb-2.5 w-full mt-2 px-5 py-2 bg-[url('/img/deal-item-background-dark.jpg')] bg-[50%_100%] bg-no-repeat flex items-center justify-center h-full"
              >
                <span class="text-center flex items-center justify-center"
                  >Описательный текст и ссылка внутри новости</span
                >
              </div>
            </div>
          </div>

          <div class="flex flex-col gap-2 w-[98%]">
            <div class="flex justify-between items-center">
              <div class="text-[#dfcf99] text-[26px]">Текущие стримы</div>
              <div class="mt-2">
                <NuxtLink to="/" class="text-[#dfcf99] text-[14px] underline">
                  <NuxtImg
                    src="https://web.poecdn.com/protected/image/twitch/offline.png?key=UFGTTYKnNd_C4m2oxhqDrw"
                    class="w-[20px] h-[20px]"
                  />
                </NuxtLink>
              </div>
            </div>

            <div class="">stream1</div>
            <div class="">stream2</div>
            <div class="">stream3</div>
            <div class="">stream4</div>
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
