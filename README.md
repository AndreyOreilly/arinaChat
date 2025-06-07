# Арина - Чат-ассистент на базе GigaChat

Веб-приложение для общения с ассистентом на базе GigaChat API от Сбера. Приложение построено на ASP.NET Core и использует современный веб-интерфейс.

## Требования

- Docker и Docker Compose
- Учетные данные GigaChat API (Client ID и Client Secret)

## Быстрый старт

1. Клонируйте репозиторий:

```bash
git clone <repository-url>
cd arina-chat
```

2. Запустите приложение через Docker Compose:

```bash
docker-compose up --build
```

3. Откройте браузер и перейдите по адресу: `http://localhost:8080`

4. При первом запуске введите ваши учетные данные GigaChat API в появившемся окне.

## Конфигурация

Приложение использует следующие переменные окружения:

- `GigaChat__ClientId`: Ваш Client ID от GigaChat API
- `GigaChat__ClientSecret`: Ваш Client Secret от GigaChat API

Вы можете задать их через:

1. Файл `.env` в корне проекта
2. Переменные окружения Docker
3. Файл `appsettings.json`

## Развертывание

Приложение готово к развертыванию на любой платформе, поддерживающей Docker:

- [Render.com](https://render.com)
- [Railway.app](https://railway.app)
- [Fly.io](https://fly.io)

### Пример развертывания на Render.com

1. Создайте новый Web Service
2. Укажите Docker как среду выполнения
3. Настройте переменные окружения:
   - `GigaChat__ClientId`
   - `GigaChat__ClientSecret`
4. Разверните приложение

## Безопасность

- Учетные данные API хранятся только в памяти браузера на время сессии
- Все запросы к API выполняются через backend для защиты учетных данных
- Поддерживается HTTPS для защиты данных при передаче

## Лицензия

MIT
