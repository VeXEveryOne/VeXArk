<div align="center">
  <img src="assets/vexark-icon.png" width="112" alt="Логотип VeXArk">
  <h1>VeXArk</h1>
  <p><strong>Ваши данные Android. Ваш компьютер. Никакого облака между ними.</strong></p>
  <p>
    <a href="../README.md">English</a> ·
    <a href="https://vexeveryone.github.io/VeXArk/">Сайт</a> ·
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.0">Скачать</a> ·
    <a href="../CHANGELOG.md">История версий</a>
  </p>
</div>

---

VeXArk — полностью офлайн-система резервного копирования Android. Она состоит
из portable-клиента для Windows, Material You Agent для телефона и ограниченного
root-helper. Без root доступны инвентаризация, APK/splits и копирование всех
фото и видео. Root открывает приватные данные приложений и расширенные снимки.

> [!IMPORTANT]
> Проект активно развивается. Не храните единственную копию незаменимых данных
> только в VeXArk и обязательно проверяйте восстановление.

## Скачать

| Платформа | Файл | Требования |
| --- | --- | --- |
| Windows | [Скачать `VeXArk.exe`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/VeXArk.exe) | Windows 10/11 x64 |
| Android | [Скачать `VeXArk-Agent.apk`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/VeXArk-Agent.apk) | Android 10–16, arm64-v8a |
| Хеши | [`SHA256SUMS.txt`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.0/SHA256SUMS.txt) | Для проверки файлов |

Windows-клиент portable: внутри уже лежат совместимый Agent и ADB. Установка и
права администратора не нужны.

## Возможности

- Полностью офлайн, без аккаунта, телеметрии и облака.
- Копирование всех фото и видео в обычную папку Windows без root.
- Fast Wi-Fi с автоматическим сравнением скорости ADB, локальной сети и диска.
- До четырёх параллельных worker-ов и продолжение прерванных файлов.
- Зашифрованные переносимые файлы `.vexark`.
- Инкрементальные снимки и дедупликация неизменившихся блоков.
- Argon2id, AES-256-GCM, случайный master key и recovery key из 24 слов.
- APK и split APK без root; CE/DE app data после явного предоставления root.
- Проверка подписей, путей и совместимости перед восстановлением.
- Темы Windows: системная, светлая, тёмная и полностью чёрная OLED.
- Material You на Android.
- Английский язык по умолчанию и переключение на русский в обоих клиентах.

## Скриншоты

| Светлая | Тёмная |
| --- | --- |
| ![Светлая тема](assets/desktop-light.png) | ![Тёмная тема](assets/desktop-dark.png) |

## Режимы

**Portable** предназначен для переноса между ROM: APK/splits, приватные данные
с root, permissions/app-ops, контакты, SMS, звонки, документы, музыка и безопасные
Android-настройки.

**Controlled Full** добавляет ROM-зависимые данные — отдельные настройки Wi-Fi,
launcher/SystemUI и сведения о root-модулях. Такие компоненты проходят строгую
проверку совместимости и не восстанавливаются автоматически.

Android Keystore, PIN, биометрия, eSIM, Wallet, DRM-ключи и аппаратно привязанные
passkeys никогда не копируются. Для Google-аккаунтов сохраняется только
зашифрованная памятка с именами и типами; пароли и OAuth-токены Android не отдаёт.

## Быстрый старт

1. Включите режим разработчика и USB debugging.
2. Запустите `VeXArk.exe`.
3. Подключите телефон и подтвердите ADB.
4. Установите Agent со страницы «Устройства».
5. Выберите папку репозитория, пароль и сохраните recovery key.
6. Начните с небольшой копии и проверьте её в истории.

VeXArk не устанавливает root и не патчит boot image.

Сборка из исходников и технические подробности находятся в
[English README](../README.md). Лицензия — `GPL-3.0-only`.
