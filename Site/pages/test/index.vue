<!-- app.vue или test.vue -->
<template>
  <div class="p-8 text-white mt-28">
    <h1 class="text-3xl mb-4">Тест шрифтов</h1>

    <div class="space-y-4">
      <!-- Cinzel -->
      <div>
        <p class="text-lg font-semibold">Cinzel:</p>
        <p class="test-font-cinzel text-2xl">
          The quick brown fox jumps over the lazy dog
        </p>
        <p class="test-font-cinzel text-2xl">
          Съешь же ещё этих мягких французских булок, да выпей чаю
        </p>
      </div>

      <!-- Fontin -->
      <div>
        <p class="text-lg font-semibold">Fontin:</p>
        <p class="test-font-fontin text-2xl">
          The quick brown fox jumps over the lazy dog
        </p>
        <p class="test-font-fontin text-2xl">
          Русский текст (возможно не поддерживается)
        </p>
      </div>

      <!-- Friz Quadrata -->
      <div>
        <p class="text-lg font-semibold">Friz Quadrata:</p>
        <p class="test-font-friz text-2xl">
          The quick brown fox jumps over the lazy dog
        </p>
        <p class="test-font-friz text-2xl">Русский текст</p>
      </div>
    </div>

    <!-- Проверка через inline стиль -->
    <div class="mt-8 p-4 border">
      <p style="font-family: 'Cinzel', serif; font-weight: 400">
        Cinzel через inline стиль
      </p>
      <p style="font-family: 'Friz Quadrata', serif; font-weight: 400">
        Friz через inline стиль
      </p>
    </div>

    <p class="font-cinzel">Текст с Cinzel</p>
  </div>
</template>
