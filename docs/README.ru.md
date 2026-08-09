<div align="center">
  <img src="assets/vexark-icon.png" width="112" alt="Логотип VeXArk">
  <h1>VeXArk</h1>
  <p><strong>Ваши данные Android. Ваш компьютер. Никакого облака между ними.</strong></p>
  <p>
    <a href="../README.md">English</a> ·
    <a href="https://vexeveryone.github.io/VeXArk/">Сайт</a> ·
    <a href="https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.7.1">Скачать</a> ·
    <a href="../CHANGELOG.md">История версий</a>
  </p>
</div>

---

VeXArk — полностью офлайн-система резервного копирования Android. Она состоит
из portable-клиента для Windows, Material You Agent для телефона и ограниченного
root-helper. Без root доступны инвентаризация, APK/splits и копирование всех
фото и видео. Root открывает приватные данные приложений и расширенные снимки.
Windows-клиент также умеет копировать оригиналы фото и видео с iPhone без
установки приложения на iOS.

> [!IMPORTANT]
> Проект активно развивается. Не храните единственную копию незаменимых данных
> только в VeXArk и обязательно проверяйте восстановление.

## Скачать

| Платформа | Файл | Требования |
| --- | --- | --- |
| Windows | [Скачать `VeXArk.exe`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/VeXArk.exe) | Windows 10/11 x64 |
| Android | [Скачать `VeXArk-Agent.apk`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/VeXArk-Agent.apk) | Android 10–16, arm64-v8a |
| Хеши | [`SHA256SUMS.txt`](https://github.com/VeXEveryOne/VeXArk/releases/download/v0.7.1/SHA256SUMS.txt) | Для проверки файлов |

Windows-клиент portable: внутри уже лежат совместимый Agent и ADB. Установка и
права администратора не нужны.

> Для тестирования импорта с iPhone используйте
> [prerelease `v0.8.0-beta.4`](https://github.com/VeXEveryOne/VeXArk/releases/tag/v0.8.0-beta.4).

## Возможности

- Полностью офлайн, без аккаунта, телеметрии и облака.
- Копирование всех фото и видео в обычную папку Windows без root.
- Копирование новых оригиналов, HEIC, видео и компонентов Live Photos с
  разблокированного доверенного iPhone по USB с сохранением структуры DCIM.
- Fast Wi-Fi с автоматическим сравнением скорости ADB, локальной сети и диска.
- Встроенный тест кабелей и портов: negotiated USB-режим, реальная скорость ADB
  и Fast Wi-Fi, рекомендация транспорта и оценка времени без чтения личных файлов.
- До четырёх параллельных worker-ов и продолжение прерванных файлов.
- Безопасная отмена backup, копирования медиа и Restore из панели состояния:
  частичный snapshot не публикуется, Android-медиа продолжатся при следующем запуске.
- Зашифрованные переносимые файлы `.vexark`.
- Инкрементальные снимки и дедупликация неизменившихся блоков.
- Argon2id, AES-256-GCM, случайный master key и recovery key из 24 слов.
- APK и split APK без root; CE/DE app data после явного предоставления root.
- Проверка подписей, путей и совместимости перед восстановлением.
- Темы Windows: системная, светлая, тёмная и полностью чёрная OLED.
- Material You на Android.
- Английский язык по умолчанию и переключение на русский в обоих клиентах.

Исходники открытых ROM можно проверять без установки прошивки через
`scripts/rom_catalog.py`. Seed-список прошивок Xiaomi 13 (`fuxi`) находится в
`catalog/rom-sources/xiaomi-13-fuxi.json`, подробности — в
[`ROM_SOURCE_CATALOG.md`](ROM_SOURCE_CATALOG.md). Найденный ключ не разрешается
для Restore автоматически: обязательны типизированная проверка, capability,
read-back и rollback.

## Скриншоты

| Светлая | Тёмная |
| --- | --- |
| ![Светлая тема](assets/desktop-light.png) | ![Тёмная тема](assets/desktop-dark.png) |

## Режимы

**Portable** предназначен для переноса между ROM: APK/splits, CE/DE,
`/Android/data` и OBB с root, опасные runtime-permissions, enabled state,
исключение из оптимизации батареи, per-app locales, контакты и журнал звонков с
дедупликацией, документы/музыка и типизированные безопасные Android-настройки.
SMS/MMS пока сохраняются только как зашифрованный архив: автоматическая запись
без временной роли SMS-приложения не выдаётся за успешное восстановление.

**Controlled Full** добавляет archive-only снимки выбранных Wi-Fi internals и
файлов модулей KernelSU/Magisk. Данные совместимого Launcher3 имеют класс
`ExactRom` и применяются только после проверки полной ROM identity, подписи
пакета и создания safety snapshot. Archive-only компоненты автоматически не
применяются.

Каталог настроек покрывает AOSP, Lineage и crDroid, отдельно показывает
`Supported`, `Archived` и `Ignored`. Неизвестные provider-ключи сохраняются в
зашифрованном отчёте по именам, но их значения не записываются обратно. Внутренние
XiaomiParts, GameSpace, LiveDisplay и charging control остаются archive-only,
пока для конкретной версии нет проверенного semantic adapter.

Android Keystore, PIN, биометрия, eSIM, Wallet, DRM-ключи и аппаратно привязанные
passkeys никогда не копируются. Для Google-аккаунтов сохраняется только
зашифрованная памятка с именами и типами; пароли и OAuth-токены Android не отдаёт.

Поддержка iPhone ограничена импортом фото и видео. VeXArk не читает данные
iOS-приложений, сообщения, учётные данные или резервные копии устройства.

## Быстрый старт

1. Включите режим разработчика и USB debugging.
2. Запустите `VeXArk.exe`.
3. Подключите телефон и подтвердите ADB.
4. Установите Agent со страницы «Устройства».
5. На странице «Фото и видео» проверяйте скорость после замены кабеля или порта.
6. Выберите папку репозитория, пароль и сохраните recovery key.
7. Начните с небольшой копии и проверьте её в истории.

VeXArk не устанавливает root и не патчит boot image.

Для копирования медиа с iPhone:

1. Установите Apple Devices из Microsoft Store.
2. Подключите iPhone по USB, разблокируйте его и подтвердите «Доверять этому
   компьютеру».
3. Откройте «Фото и видео», нажмите «Найти iPhone», выберите папку и нажмите
   «Скопировать с iPhone».

Если iCloud Photos оптимизирует хранилище, сначала загрузите оригиналы полного
разрешения на iPhone.

Сборка из исходников и технические подробности находятся в
[English README](../README.md). Лицензия — `GPL-3.0-only`.
