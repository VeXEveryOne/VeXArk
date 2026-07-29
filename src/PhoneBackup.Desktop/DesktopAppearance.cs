using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace PhoneBackup.Desktop;

public sealed record DesktopPreferences(
    string RepositoryPath,
    string MediaDestination,
    string Theme,
    string Language,
    IReadOnlyDictionary<string, string> MediaTransports);

public static class DesktopSettingsStore
{
    private static readonly object Gate = new();

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneBackup",
        "settings.json");

    public static DesktopPreferences Load()
    {
        var defaults = new DesktopPreferences(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "VeXArk"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "VeXArk Media"),
            "system",
            "en",
            new Dictionary<string, string>(StringComparer.Ordinal));
        lock (Gate)
        {
            if (!File.Exists(SettingsPath)) return defaults;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
                var root = document.RootElement;
                return defaults with
                {
                    RepositoryPath = Read(root, "repositoryPath") ?? defaults.RepositoryPath,
                    MediaDestination = Read(root, "mediaDestination") ?? defaults.MediaDestination,
                    Theme = NormalizeTheme(Read(root, "theme")),
                    Language = NormalizeLanguage(Read(root, "language")),
                    MediaTransports = ReadMediaTransports(root)
                };
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                return defaults;
            }
        }
    }

    public static void Save(DesktopPreferences settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    repositoryPath = settings.RepositoryPath,
                    mediaDestination = settings.MediaDestination,
                    theme = NormalizeTheme(settings.Theme),
                    language = NormalizeLanguage(settings.Language),
                    mediaTransports = settings.MediaTransports
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .ToDictionary(
                            x => x.Key,
                            x => NormalizeMediaTransport(x.Value),
                            StringComparer.Ordinal)
                }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    public static string NormalizeTheme(string? value) =>
        value?.ToLowerInvariant() is "light" or "dark" or "oled" ? value.ToLowerInvariant() : "system";

    public static string NormalizeLanguage(string? value) =>
        value?.ToLowerInvariant() == "ru" ? "ru" : "en";

    public static string NormalizeMediaTransport(string? value) =>
        value?.ToLowerInvariant() is "adb" or "fastlan" ? value.ToLowerInvariant() : "auto";

    private static IReadOnlyDictionary<string, string> ReadMediaTransports(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("mediaTransports", out var transports) ||
            transports.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in transports.EnumerateObject())
        {
            if (!string.IsNullOrWhiteSpace(property.Name) &&
                property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = NormalizeMediaTransport(property.Value.GetString());
        }
        return result;
    }
}

public sealed record ChoiceOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public static class ThemeManager
{
    private sealed record Palette(
        string Background,
        string Sidebar,
        string Panel,
        string Panel2,
        string Input,
        string Accent,
        string AccentHover,
        string Text,
        string TextMuted,
        string Border,
        string Danger,
        string Hover,
        string HoverBorder,
        string Selection,
        string ListHover,
        string ReadOnly,
        string CheckBorder,
        string ProgressTrack,
        string Warning);

    private static readonly Palette Dark = new(
        "#0A0F15", "#0E141C", "#131B24", "#19232E", "#0F171F",
        "#79DDB4", "#91E8C5", "#F1F5F7", "#92A2B4", "#263341",
        "#FFA1A8", "#22303D", "#3B4C5D", "#20352F", "#1A2530",
        "#10161D", "#425160", "#202B36", "#FFD58A");

    private static readonly Palette Light = new(
        "#F4F7FA", "#FFFFFF", "#FFFFFF", "#EDF2F6", "#F8FAFC",
        "#13795B", "#0F664D", "#17212B", "#607080", "#D5DEE7",
        "#B4232D", "#E7EDF2", "#C8D4DE", "#DDF3EA", "#EAF0F5",
        "#EEF2F5", "#9AA9B7", "#DCE4EA", "#9A5A00");

    private static readonly Palette Oled = new(
        "#000000", "#020202", "#080808", "#101010", "#050505",
        "#73E2B6", "#91EBC8", "#FAFAFA", "#9A9A9A", "#252525",
        "#FF9DA5", "#171717", "#303030", "#112B21", "#151515",
        "#080808", "#4A4A4A", "#202020", "#FFD27A");

    public static string SelectedTheme { get; private set; } = "system";

    public static void Initialize(string theme)
    {
        SelectedTheme = DesktopSettingsStore.NormalizeTheme(theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplySelected();
    }

    public static void Apply(string theme)
    {
        SelectedTheme = DesktopSettingsStore.NormalizeTheme(theme);
        ApplySelected();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (SelectedTheme != "system" || Application.Current is null) return;
        Application.Current.Dispatcher.BeginInvoke(ApplySelected);
    }

    private static void ApplySelected()
    {
        if (Application.Current is null) return;
        var palette = SelectedTheme switch
        {
            "light" => Light,
            "oled" => Oled,
            "dark" => Dark,
            _ => SystemUsesLightTheme() ? Light : Dark
        };
        Set("BackgroundBrush", palette.Background);
        Set("SidebarBrush", palette.Sidebar);
        Set("PanelBrush", palette.Panel);
        Set("Panel2Brush", palette.Panel2);
        Set("InputBrush", palette.Input);
        Set("AccentBrush", palette.Accent);
        Set("AccentHoverBrush", palette.AccentHover);
        Set("TextBrush", palette.Text);
        Set("TextMutedBrush", palette.TextMuted);
        Set("BorderBrush", palette.Border);
        Set("DangerBrush", palette.Danger);
        Set("HoverBrush", palette.Hover);
        Set("HoverBorderBrush", palette.HoverBorder);
        Set("SelectionBrush", palette.Selection);
        Set("ListHoverBrush", palette.ListHover);
        Set("ReadOnlyBrush", palette.ReadOnly);
        Set("CheckBorderBrush", palette.CheckBorder);
        Set("ProgressTrackBrush", palette.ProgressTrack);
        Set("WarningBrush", palette.Warning);
    }

    private static void Set(string key, string color) =>
        Application.Current.Resources[key] =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static bool SystemUsesLightTheme()
    {
        try
        {
            return Registry.GetValue(
                       @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                       "AppsUseLightTheme",
                       0) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}

public static class LocalizationManager
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["ваши данные — ваши"] = "your data — yours",
        ["  •  резервное копирование Android"] = "  •  Android backup",
        ["Устройства"] = "Devices",
        ["Новая копия"] = "New backup",
        ["Новая резервная копия"] = "New backup",
        ["Фото и видео"] = "Photos & videos",
        ["Восстановление"] = "Restore",
        ["Восстановить"] = "Restore",
        ["История"] = "History",
        ["Настройки"] = "Settings",
        ["Состояние"] = "Status",
        ["Свернуть"] = "Minimize",
        ["Развернуть"] = "Maximize",
        ["Закрыть"] = "Close",
        ["Обновить устройства"] = "Refresh devices",
        ["Подключите Android-телефон"] = "Connect an Android phone",
        ["USB определяется автоматически. Для беспроводного подключения используйте Wireless ADB."] =
            "USB is detected automatically. Use Wireless ADB for a wireless connection.",
        ["Выбрать устройство"] = "Select device",
        ["Установить Agent"] = "Install Agent",
        ["IP:порт для сопряжения или подключения"] = "IP:port for pairing or connection",
        ["Код"] = "Code",
        ["Сопрячь"] = "Pair",
        ["Подключить"] = "Connect",
        ["Что сохранить"] = "What to back up",
        ["Выберите данные, которые попадут в зашифрованную копию."] =
            "Choose the data to include in the encrypted backup.",
        ["Приложения и split APK"] = "Apps and split APKs",
        ["Приватные данные приложений — нужен root"] = "Private app data — root required",
        ["Документы, загрузки, музыка и аудио"] = "Documents, downloads, music and audio",
        ["Контакты, SMS, звонки и список аккаунтов"] = "Contacts, SMS, calls and account inventory",
        ["Full snapshot — только для той же ROM"] = "Full snapshot — same ROM only",
        ["Добавить cache и code_cache"] = "Include cache and code_cache",
        ["Приложения"] = "Apps",
        ["Все"] = "All",
        ["Снять"] = "None",
        ["Загрузить список приложений"] = "Load app list",
        ["Локально на этом ПК"] = "Local on this PC",
        ["Копия хранится только в выбранной папке. Облако и телеметрия не используются."] =
            "The backup stays in the selected folder. No cloud or telemetry is used.",
        ["Папка хранения"] = "Repository folder",
        ["Выбрать другую папку"] = "Choose another folder",
        ["Защита"] = "Protection",
        ["Минимум 10 символов. Пароль не отправляется с компьютера."] =
            "At least 10 characters. The password never leaves this PC.",
        ["Подготовить локальное хранилище"] = "Prepare local repository",
        ["Фото и видео из общей памяти сохраняются через отдельный простой экспорт."] =
            "Photos and videos from shared storage use a separate simple export.",
        ["Создать резервную копию"] = "Create backup",
        ["Семейный фотоархив"] = "Family photo archive",
        ["Скопируйте все оригиналы с телефона в обычную папку Windows. Root не нужен."] =
            "Copy every original from the phone to a regular Windows folder. Root is not required.",
        ["Куда сохранить"] = "Destination",
        ["Выбрать папку"] = "Choose folder",
        ["Транспорт данных"] = "Data transport",
        ["Скопировать все фото и видео"] = "Copy all photos and videos",
        ["Перед копированием Auto проверит ADB, Fast Wi-Fi и диск назначения."] =
            "Auto will test ADB, Fast Wi-Fi and the destination drive before copying.",
        ["Копируются оригиналы из MediaStore. На телефоне ничего не удаляется."] =
            "Originals are copied from MediaStore. Nothing is deleted from the phone.",
        ["Безопасно и просто"] = "Safe and simple",
        ["✓ Сохраняются DCIM, Pictures, Movies и папки мессенджеров"] =
            "✓ Includes DCIM, Pictures, Movies and messenger folders",
        ["✓ Уже скопированные файлы пропускаются"] = "✓ Already copied files are skipped",
        ["✓ На телефоне ничего не удаляется"] = "✓ Nothing is deleted from the phone",
        ["✓ Работает без root"] = "✓ Works without root",
        ["Выберите резервную копию"] = "Choose a backup",
        ["Перед восстановлением VeXArk проверит ROM, подписи приложений и опасные компоненты."] =
            "Before restoring, VeXArk checks the ROM, app signatures and risky components.",
        ["Загрузить список копий"] = "Load backups",
        ["Проверка совместимости"] = "Compatibility check",
        ["Разрешить downgrade APK после предупреждения"] = "Allow APK downgrade after warning",
        ["Проверить и восстановить"] = "Verify and restore",
        ["Перед изменениями создаётся safety snapshot. Действие подтверждается на телефоне."] =
            "A safety snapshot is created before changes. Restore must be confirmed on the phone.",
        ["Резервные копии"] = "Backups",
        ["Обновить"] = "Refresh",
        ["Проверить целостность"] = "Verify integrity",
        ["Удалить"] = "Delete",
        ["Очистить место"] = "Clean up storage",
        ["Один локальный файл"] = "One portable file",
        ["Сохраните выбранную копию в переносимый зашифрованный файл — например, на внешний диск."] =
            "Save the selected snapshot as one encrypted portable file, for example on an external drive.",
        ["Сохранить выбранную копию"] = "Export selected backup",
        ["Открыть файл резервной копии"] = "Open backup file",
        ["Локальное хранилище"] = "Local repository",
        ["Здесь находятся зашифрованные копии и общие дедуплицированные данные."] =
            "Encrypted snapshots and shared deduplicated data are stored here.",
        ["Папка"] = "Folder",
        ["Пароль хранилища"] = "Repository password",
        ["Не сохраняется в приложении."] = "It is never stored by the app.",
        ["Создать новое хранилище"] = "Create repository",
        ["Сменить пароль"] = "Change password",
        ["Данные не будут перешифровываться целиком — изменится только защита master key."] =
            "Chunks are not re-encrypted; only the master-key wrapper changes.",
        ["24 слова позволяют вернуть доступ, если пароль забыт."] =
            "The 24-word recovery key restores access if the password is lost.",
        ["Восстановить доступ"] = "Recover access",
        ["Ключ восстановления"] = "Recovery key",
        ["Оформление и язык"] = "Appearance & language",
        ["Тема"] = "Theme",
        ["Язык"] = "Language",
        ["Следовать настройкам Windows, выбрать светлую, тёмную или OLED-тему."] =
            "Follow Windows or choose Light, Dark or true-black OLED.",
        ["полностью офлайн"] = "fully offline",
        ["USB и Wireless ADB объединяются по физическому устройству"] =
            "USB and Wireless ADB connections are merged by physical device",
        ["Локальный зашифрованный репозиторий"] = "Local encrypted repository",
        ["Сначала выберите устройство"] = "Select a device first",
        ["Compatibility engine не применяет опасные данные автоматически"] =
            "The compatibility engine never applies risky data automatically"
    };

    private static readonly (string Russian, string English)[] Fragments =
    [
        ("Сначала выберите устройство", "Select a device first"),
        ("Устройства не найдены", "No devices found"),
        ("Найдено устройств:", "Devices found:"),
        ("Поиск ADB-устройств", "Scanning for ADB devices"),
        ("Подключение к Agent", "Connecting to Agent"),
        ("Установка Android Agent", "Installing Android Agent"),
        ("Чтение списка приложений", "Loading app list"),
        ("Создание зашифрованного репозитория", "Creating encrypted repository"),
        ("Репозиторий создан", "Repository created"),
        ("Инвентаризация приложений и APK", "Inventorying apps and APKs"),
        ("Запрос root на телефоне", "Requesting root on the phone"),
        ("Проверка и публикация снимка", "Verifying and publishing snapshot"),
        ("Backup завершён", "Backup complete"),
        ("Открытие manifests", "Opening manifests"),
        ("Репозиторий открыт", "Repository opened"),
        ("Телефон составляет список фото и видео", "The phone is listing photos and videos"),
        ("Проверка скорости диска", "Testing destination drive speed"),
        ("Проверка скорости ADB", "Testing ADB speed"),
        ("Проверка Fast Wi-Fi", "Testing Fast Wi-Fi"),
        ("Fast Wi-Fi недоступен", "Fast Wi-Fi is unavailable"),
        ("Используется ADB", "Using ADB"),
        ("Fast Wi-Fi прерван, продолжение через ADB", "Fast Wi-Fi interrupted, resuming over ADB"),
        ("Копирование завершено", "Copy complete"),
        ("Фото и видео скопированы", "Photos and videos copied"),
        ("Скопировано:", "Copied:"),
        ("Уже было на ПК:", "Already on PC:"),
        ("Продолжено:", "Resumed:"),
        ("Ошибок:", "Errors:"),
        ("Всего найдено:", "Total found:"),
        ("Транспорт:", "Transport:"),
        ("Средняя скорость:", "Average speed:"),
        ("Тесты:", "Benchmarks:"),
        ("Первые ошибки:", "First errors:"),
        ("Создание локального файла резервной копии", "Creating portable backup file"),
        ("Локальная резервная копия сохранена одним файлом", "Portable backup saved"),
        ("Импорт локальной резервной копии", "Importing portable backup"),
        ("Локальная резервная копия импортирована", "Portable backup imported"),
        ("Проверка совместимости Restore", "Checking restore compatibility"),
        ("Restore завершён", "Restore complete"),
        ("Полная проверка репозитория", "Verifying repository"),
        ("Проверено объектов", "Verified objects"),
        ("Удаление snapshot", "Deleting snapshot"),
        ("Очистка незадействованных chunks", "Cleaning unused chunks"),
        ("Пароль изменён", "Password changed"),
        ("Готово", "Ready"),
        ("Root не найден", "Root not found"),
        ("root не требуется", "root not required"),
        ("Хранилище: неизвестно", "Storage: unknown"),
        ("Свободно", "Free"),
        ("приложений", "apps"),
        ("приложения", "apps"),
        ("Найдено:", "Found:"),
        ("ГБ", "GB"),
        ("МБ", "MB")
    ];

    private static readonly DependencyProperty CanonicalTextProperty =
        DependencyProperty.RegisterAttached(
            "CanonicalText",
            typeof(string),
            typeof(LocalizationManager));
    private static readonly DependencyProperty CanonicalToolTipProperty =
        DependencyProperty.RegisterAttached(
            "CanonicalToolTip",
            typeof(string),
            typeof(LocalizationManager));

    public static string Language { get; private set; } = "en";
    public static bool IsRussian => Language == "ru";

    public static void Initialize(string language)
    {
        Language = DesktopSettingsStore.NormalizeLanguage(language);
        var culture = CultureInfo.GetCultureInfo(IsRussian ? "ru-RU" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static void ApplyLanguage(string language)
    {
        Initialize(language);
        foreach (Window window in Application.Current.Windows)
            Apply(window);
    }

    public static string T(string canonicalRussian)
    {
        if (IsRussian || string.IsNullOrEmpty(canonicalRussian)) return canonicalRussian;
        if (English.TryGetValue(canonicalRussian, out var exact)) return exact;
        var result = canonicalRussian;
        foreach (var (russian, english) in Fragments)
            result = result.Replace(russian, english, StringComparison.Ordinal);
        return result;
    }

    public static void Apply(DependencyObject root)
    {
        if (root is TextBlock textBlock &&
            BindingOperations.GetBindingExpression(textBlock, TextBlock.TextProperty) is null)
            ApplyText(textBlock, TextBlock.TextProperty, textBlock.Text);
        if (root is ContentControl contentControl && contentControl.Content is string content)
            ApplyText(contentControl, ContentControl.ContentProperty, content);
        if (root is FrameworkElement element && element.ToolTip is string toolTip)
        {
            var canonical = (string?)element.GetValue(CanonicalToolTipProperty) ?? toolTip;
            element.SetValue(CanonicalToolTipProperty, canonical);
            element.ToolTip = T(canonical);
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            Apply(VisualTreeHelper.GetChild(root, index));
    }

    private static void ApplyText(DependencyObject target, DependencyProperty property, string value)
    {
        var canonical = (string?)target.GetValue(CanonicalTextProperty) ?? value;
        target.SetValue(CanonicalTextProperty, canonical);
        target.SetValue(property, T(canonical));
    }
}
