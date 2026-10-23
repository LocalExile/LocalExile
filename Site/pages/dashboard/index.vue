<template>
  <div class="min-h-screen bg-gray-50">
    <!-- Навигация -->
    <nav class="bg-white shadow-sm">
      <div
        class="max-w-7xl mx-auto px-4 py-4 flex justify-between items-center"
      >
        <div class="font-semibold text-lg">Панель управления</div>
        <div class="flex items-center space-x-4">
          <div class="flex items-center space-x-2">
            <div
              class="w-8 h-8 bg-blue-500 rounded-full flex items-center justify-center text-white"
            >
              {{ auth.user.value?.name?.charAt(0) || "A" }}
            </div>
            <span>{{ auth.user.value?.name }}</span>
          </div>
          <button
            @click="handleLogout"
            class="px-4 py-2 bg-gray-200 rounded hover:bg-gray-300 transition-colors"
          >
            Выйти
          </button>
        </div>
      </div>
    </nav>

    <!-- Основной контент -->
    <main class="max-w-7xl mx-auto px-4 py-8">
      <div class="bg-white rounded-lg shadow p-6">
        <h1 class="text-2xl font-bold mb-4">
          Добро пожаловать, {{ auth.user.value?.name }}!
        </h1>
        <p class="text-gray-600 mb-6">Вы успешно авторизовались в системе.</p>

        <!-- Информация о пользователе -->
        <div class="mb-8">
          <h2 class="text-lg font-semibold mb-3">Ваш профиль:</h2>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div class="p-4 border rounded">
              <div class="text-sm text-gray-500">Email</div>
              <div class="font-medium">{{ auth.user.value?.email }}</div>
            </div>
            <div class="p-4 border rounded">
              <div class="text-sm text-gray-500">Роль</div>
              <div class="font-medium">{{ auth.user.value?.role }}</div>
            </div>
          </div>
        </div>

        <!-- Действия -->
        <div>
          <h2 class="text-lg font-semibold mb-3">Доступные действия:</h2>
          <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div
              v-for="action in actions"
              :key="action.id"
              class="p-4 border rounded hover:bg-gray-50 cursor-pointer transition-colors"
            >
              <div class="font-medium mb-1">{{ action.title }}</div>
              <div class="text-sm text-gray-600">{{ action.description }}</div>
            </div>
          </div>
        </div>
      </div>
    </main>
  </div>
</template>

<script setup>
definePageMeta({
  middleware: "auth",
  requiresAuth: true,
});

const auth = useAuth();

// Примерные действия
const actions = ref([
  {
    id: 1,
    title: "Просмотр статистики",
    description: "Анализ активности системы",
  },
  {
    id: 2,
    title: "Управление контентом",
    description: "Редактирование материалов",
  },
  { id: 3, title: "Настройки системы", description: "Конфигурация параметров" },
]);

// Выход
const handleLogout = () => {
  auth.logout();
  navigateTo("/");
};
</script>
