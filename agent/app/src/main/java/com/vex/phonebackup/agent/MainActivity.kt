package com.vex.phonebackup.agent

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat

private data class AccessState(
    val contacts: Boolean = false,
    val messages: Boolean = false,
    val calls: Boolean = false,
    val images: Boolean = false,
    val videos: Boolean = false,
    val selectedMediaOnly: Boolean = false,
    val allFiles: Boolean = false
)

class MainActivity : ComponentActivity() {
    private var rootProbe by mutableStateOf(RootCapabilities.probe(false))
    private var trustedKeys by mutableStateOf<List<String>>(emptyList())
    private var accessState by mutableStateOf(AccessState())
    private var language by mutableStateOf("en")

    private val permissions = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {
        refreshState()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        language = getSharedPreferences(UI_PREFS, MODE_PRIVATE)
            .getString(KEY_LANGUAGE, "en")
            .takeIf { it == "ru" } ?: "en"
        refreshState()
        ContextCompat.startForegroundService(
            this,
            Intent(this, AgentForegroundService::class.java)
        )
        setContent { PhoneBackupTheme { AgentScreen() } }
    }

    override fun onResume() {
        super.onResume()
        refreshState()
    }

    private fun refreshState() {
        loadTrustedKeys()
        rootProbe = RootCapabilities.probe(false)
        val media = MediaStoreAccess.status(this)
        accessState = AccessState(
            contacts = granted(Manifest.permission.READ_CONTACTS),
            messages = granted(Manifest.permission.READ_SMS),
            calls = granted(Manifest.permission.READ_CALL_LOG),
            images = media.optBoolean("images"),
            videos = media.optBoolean("videos"),
            selectedMediaOnly = Build.VERSION.SDK_INT >= 34 &&
                granted(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) &&
                !media.optBoolean("images") &&
                !media.optBoolean("videos"),
            allFiles = media.optBoolean("allFiles")
        )
    }

    private fun requestPersonalPermissions() {
        val requested = buildList {
            add(Manifest.permission.READ_CONTACTS)
            add(Manifest.permission.WRITE_CONTACTS)
            add(Manifest.permission.GET_ACCOUNTS)
            add(Manifest.permission.READ_SMS)
            add(Manifest.permission.READ_CALL_LOG)
            add(Manifest.permission.WRITE_CALL_LOG)
            if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
        }
        permissions.launch(requested.toTypedArray())
    }

    private fun requestMediaPermissions() {
        val requested = if (Build.VERSION.SDK_INT >= 33) {
            buildList {
                add(Manifest.permission.READ_MEDIA_IMAGES)
                add(Manifest.permission.READ_MEDIA_VIDEO)
                if (Build.VERSION.SDK_INT >= 34)
                    add(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
            }
        } else {
            listOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        permissions.launch(requested.toTypedArray())
    }

    @OptIn(ExperimentalMaterial3Api::class)
    @Composable
    private fun AgentScreen() {
        Scaffold(
            modifier = Modifier.fillMaxSize(),
            contentWindowInsets = WindowInsets.safeDrawing,
            topBar = {
                CenterAlignedTopAppBar(
                    title = {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text("VeXArk Agent", fontWeight = FontWeight.SemiBold)
                            Text(
                                "${tr("version", "версия")} ${BuildConfig.VERSION_NAME}",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                )
            }
        ) { contentPadding ->
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(contentPadding)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp, vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Text(
                    tr(
                        "Data is sent only to a trusted computer through ADB. " +
                            "Backups are never stored on the phone.",
                        "Данные передаются только на подтверждённый компьютер через ADB. " +
                            "На телефоне резервные копии не хранятся."
                    ),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )

                ComputerAccessCard()

                SectionCard(tr("Language", "Язык")) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        FilterChip(
                            selected = language == "en",
                            onClick = { changeUiLanguage("en") },
                            label = { Text("English") }
                        )
                        FilterChip(
                            selected = language == "ru",
                            onClick = { changeUiLanguage("ru") },
                            label = { Text("Русский") }
                        )
                    }
                }

                StatusCard(
                    "Agent",
                    if (AgentState.serviceRunning)
                        tr("Running • local port 49321", "Работает • локальный порт 49321")
                    else
                        tr("Stopped", "Остановлен"),
                    AgentState.serviceRunning
                )
                StatusCard(
                    "Root",
                    when {
                        rootProbe.granted ->
                            "${rootProbe.provider} • ${tr("granted", "предоставлен")}"
                        rootProbe.available ->
                            "${rootProbe.provider} • ${tr("available", "можно запросить")}"
                        else -> tr(
                            "Not installed • no-root mode is available",
                            "Не установлен • доступен no-root режим"
                        )
                    },
                    rootProbe.granted
                )

                AgentState.pendingRestore?.let { request ->
                    ConfirmationCard(
                        title = tr("Confirm restore", "Подтвердить восстановление"),
                        text = "Snapshot ${request.snapshotId.take(12)} • " +
                            tr("items", "объектов") + ": ${request.itemCount}",
                        approveLabel = tr("Restore", "Восстановить"),
                        onApprove = AgentState::approveRestore,
                        onReject = AgentState::rejectRestore
                    )
                }

                SectionCard(tr("Data access", "Доступ к данным")) {
                    PermissionLine(
                        tr("Photos and videos", "Фото и видео"),
                        when {
                            accessState.images && accessState.videos ->
                                tr("Full access", "Разрешены полностью")
                            accessState.selectedMediaOnly -> tr(
                                "Selected items only — allow all for a complete copy",
                                "Только выбранные — для полной копии разрешите все"
                            )
                            accessState.images || accessState.videos ->
                                tr("Partial access", "Разрешены частично")
                            else -> tr("Not allowed", "Не разрешены")
                        },
                        accessState.images && accessState.videos
                    )
                    FilledTonalButton(
                        onClick = ::requestMediaPermissions,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(tr("Allow all photos and videos", "Разрешить все фото и видео"))
                    }

                    HorizontalDivider(Modifier.padding(vertical = 6.dp))
                    PermissionLine(
                        tr("Personal data", "Личные данные"),
                        tr(
                            "Contacts, SMS, calls and account inventory",
                            "Контакты, SMS, звонки и список аккаунтов"
                        ),
                        accessState.contacts && accessState.messages && accessState.calls
                    )
                    FilledTonalButton(
                        onClick = ::requestPersonalPermissions,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(tr("Allow personal data", "Разрешить личные данные"))
                    }

                    HorizontalDivider(Modifier.padding(vertical = 6.dp))
                    PermissionLine(
                        tr("Shared storage", "Общее хранилище"),
                        if (accessState.allFiles)
                            tr("All-files access granted", "Доступ ко всем файлам предоставлен")
                        else
                            tr(
                                "Required only for Documents, Downloads and Music",
                                "Нужен только для Documents, Downloads и Music"
                            ),
                        accessState.allFiles
                    )
                    OutlinedButton(
                        onClick = ::requestAllFilesAccess,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(tr("Configure all-files access", "Настроить доступ ко всем файлам"))
                    }
                }

                SectionCard(tr("Root and service", "Root и сервис")) {
                    Text(
                        tr(
                            "Root is required only for private app data and Full snapshots. " +
                                "It is not required for copying photos and videos.",
                            "Root нужен только для приватных данных приложений и Full snapshot. " +
                                "Для копирования фото и видео он не нужен."
                        ),
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    FilledTonalButton(
                        onClick = { rootProbe = RootCapabilities.probe(true) },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(tr("Check and request root", "Проверить и запросить root"))
                    }
                    OutlinedButton(
                        onClick = {
                            stopService(
                                Intent(this@MainActivity, AgentForegroundService::class.java)
                            )
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(tr("Stop Agent", "Остановить Agent"))
                    }
                }

                if (trustedKeys.isNotEmpty()) {
                    SectionCard(tr("Trusted computers", "Доверенные компьютеры")) {
                        trustedKeys.forEachIndexed { index, key ->
                            if (index > 0) HorizontalDivider()
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(vertical = 6.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                Column(Modifier.fillMaxWidth(0.62f)) {
                                    Text(
                                        "${tr("Computer", "Компьютер")} ${index + 1}",
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        AgentState.fingerprint(key),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                                OutlinedButton(onClick = { revokeDesktop(key) }) {
                                    Text(tr("Revoke", "Отозвать"))
                                }
                            }
                        }
                    }
                }

                Text(
                    tr(
                        "Google passwords, OAuth tokens, Keystore, PIN, biometrics, eSIM and DRM " +
                            "are never copied. Sign in to accounts again after migration.",
                        "Пароли Google, OAuth-токены, Keystore, PIN, биометрия, eSIM и DRM " +
                            "никогда не копируются. После переноса аккаунты нужно войти заново."
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = 14.dp)
                )
            }
        }
    }

    @Composable
    private fun ComputerAccessCard() {
        val pendingKey = AgentState.pendingDesktopKey
        val connected = AgentState.connectedClient != null
        val containerColor = when {
            pendingKey != null -> MaterialTheme.colorScheme.tertiaryContainer
            connected -> MaterialTheme.colorScheme.secondaryContainer
            else -> MaterialTheme.colorScheme.primaryContainer
        }
        val badgeColor = when {
            pendingKey != null -> MaterialTheme.colorScheme.tertiary
            connected -> MaterialTheme.colorScheme.primary
            trustedKeys.isNotEmpty() -> MaterialTheme.colorScheme.primary
            else -> MaterialTheme.colorScheme.error
        }
        val badgeText = when {
            pendingKey != null -> tr("Action required", "Нужно подтверждение")
            connected -> tr("Connected", "Подключён")
            trustedKeys.isNotEmpty() -> tr("Waiting for PC", "Ожидание ПК")
            else -> tr("Setup required", "Нужна настройка")
        }

        ElevatedCard(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.elevatedCardColors(containerColor = containerColor)
        ) {
            Column(
                modifier = Modifier.padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            tr("Computer access", "Доступ компьютера"),
                            style = MaterialTheme.typography.headlineSmall,
                            fontWeight = FontWeight.Bold
                        )
                        Text(
                            localizedStatus(AgentState.statusText),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    Surface(
                        color = badgeColor,
                        contentColor = MaterialTheme.colorScheme.surface,
                        shape = MaterialTheme.shapes.extraLarge
                    ) {
                        Text(
                            badgeText,
                            modifier = Modifier.padding(horizontal = 12.dp, vertical = 7.dp),
                            style = MaterialTheme.typography.labelMedium,
                            fontWeight = FontWeight.Bold
                        )
                    }
                }

                when {
                    pendingKey != null -> {
                        Text(
                            tr(
                                "A computer wants to access VeXArk. Compare this fingerprint " +
                                    "with the one shown in the Windows app:",
                                "Компьютер запрашивает доступ к VeXArk. Сверьте этот fingerprint " +
                                    "с указанным в приложении Windows:"
                            )
                        )
                        Surface(
                            modifier = Modifier.fillMaxWidth(),
                            color = MaterialTheme.colorScheme.surface.copy(alpha = 0.72f),
                            shape = MaterialTheme.shapes.medium
                        ) {
                            Text(
                                AgentState.fingerprint(pendingKey),
                                modifier = Modifier.padding(16.dp),
                                style = MaterialTheme.typography.titleLarge,
                                fontWeight = FontWeight.Bold
                            )
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(10.dp)
                        ) {
                            Button(
                                onClick = { approveDesktop(pendingKey) },
                                modifier = Modifier.weight(1f)
                            ) {
                                Text(tr("Allow computer", "Разрешить компьютеру"))
                            }
                            OutlinedButton(
                                onClick = {
                                    AgentState.pendingDesktopKey = null
                                    AgentState.statusText = "Computer rejected"
                                },
                                modifier = Modifier.weight(1f)
                            ) {
                                Text(tr("Reject", "Отклонить"))
                            }
                        }
                    }

                    connected -> {
                        Text(
                            tr(
                                "The desktop is connected through the local ADB tunnel. " +
                                    "Backup commands can now be received securely.",
                                "Компьютер подключён через локальный ADB-туннель. " +
                                    "Теперь можно безопасно принимать команды резервного копирования."
                            )
                        )
                        OutlinedButton(
                            onClick = ::openUsbDebuggingSettings,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text(
                                tr(
                                    "Open USB debugging settings",
                                    "Открыть настройки USB-отладки"
                                )
                            )
                        }
                    }

                    trustedKeys.isNotEmpty() -> {
                        Text(
                            if (language == "ru") {
                                "Доверенных компьютеров: ${trustedKeys.size}. " +
                                    "Подключите USB-кабель и откройте VeXArk в Windows. Если телефон " +
                                    "не обнаружен, проверьте USB-отладку кнопкой ниже."
                            } else {
                                "This phone already trusts ${trustedKeys.size} " +
                                    (if (trustedKeys.size == 1) "computer. " else "computers. ") +
                                    "Connect the USB cable and open VeXArk on Windows. If the phone " +
                                    "is not detected, check USB debugging below."
                            }
                        )
                        Button(
                            onClick = ::openUsbDebuggingSettings,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text(
                                tr(
                                    "Open USB debugging settings",
                                    "Открыть настройки USB-отладки"
                                )
                            )
                        }
                    }

                    else -> {
                        Text(
                            tr(
                                "Connect VeXArk in three steps:",
                                "Подключите VeXArk за три шага:"
                            ),
                            fontWeight = FontWeight.SemiBold
                        )
                        Text(
                            tr(
                                "1. Enable USB debugging.\n" +
                                    "2. Connect the phone to the PC with a data cable.\n" +
                                    "3. Tap Allow on Android's computer fingerprint prompt.",
                                "1. Включите USB-отладку.\n" +
                                    "2. Подключите телефон к ПК кабелем для передачи данных.\n" +
                                    "3. Нажмите «Разрешить» в системном запросе fingerprint компьютера."
                            )
                        )
                        Button(
                            onClick = ::openUsbDebuggingSettings,
                            modifier = Modifier.fillMaxWidth()
                        ) {
                            Text(
                                tr(
                                    "Open USB debugging settings",
                                    "Открыть настройки USB-отладки"
                                )
                            )
                        }
                        Text(
                            tr(
                                "VeXArk opens Developer options and asks Android to highlight " +
                                    "USB debugging. Some ROMs may ignore the highlight. If Developer " +
                                    "options are hidden, tap the OS/build version seven times first.",
                                "VeXArk откроет параметры разработчика и попросит Android выделить " +
                                    "пункт USB-отладки. Некоторые прошивки могут проигнорировать " +
                                    "выделение. Если меню скрыто, сначала семь раз нажмите на версию ОС/сборки."
                            ),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }
    }

    @Composable
    private fun StatusCard(title: String, value: String, positive: Boolean) {
        ElevatedCard(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.elevatedCardColors(
                containerColor = if (positive)
                    MaterialTheme.colorScheme.secondaryContainer
                else
                    MaterialTheme.colorScheme.surfaceContainer
            )
        ) {
            Column(Modifier.padding(18.dp)) {
                Text(
                    title,
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Text(value, style = MaterialTheme.typography.titleMedium)
            }
        }
    }

    @Composable
    private fun SectionCard(title: String, content: @Composable () -> Unit) {
        ElevatedCard(modifier = Modifier.fillMaxWidth()) {
            Column(
                modifier = Modifier.padding(18.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Text(title, style = MaterialTheme.typography.titleLarge)
                content()
            }
        }
    }

    @Composable
    private fun PermissionLine(title: String, detail: String, granted: Boolean) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.Top
        ) {
            Column(Modifier.fillMaxWidth(0.78f)) {
                Text(title, fontWeight = FontWeight.Medium)
                Text(
                    detail,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Text(
                if (granted) tr("Ready", "Готово") else tr("Required", "Нужно"),
                color = if (granted)
                    MaterialTheme.colorScheme.primary
                else
                    MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.padding(start = 10.dp)
            )
        }
    }

    @Composable
    private fun ConfirmationCard(
        title: String,
        text: String,
        approveLabel: String,
        onApprove: () -> Unit,
        onReject: () -> Unit
    ) {
        ElevatedCard(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.elevatedCardColors(
                containerColor = MaterialTheme.colorScheme.tertiaryContainer
            )
        ) {
            Column(
                modifier = Modifier.padding(18.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Text(title, style = MaterialTheme.typography.titleLarge)
                Text(text)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(onClick = onApprove) { Text(approveLabel) }
                    OutlinedButton(onClick = onReject) {
                        Text(tr("Reject", "Отклонить"))
                    }
                }
            }
        }
    }

    @Composable
    private fun PhoneBackupTheme(content: @Composable () -> Unit) {
        val dark = isSystemInDarkTheme()
        val scheme = when {
            Build.VERSION.SDK_INT >= 31 && dark -> dynamicDarkColorScheme(this)
            Build.VERSION.SDK_INT >= 31 -> dynamicLightColorScheme(this)
            dark -> darkColorScheme(
                primary = Color(0xFF75D6B2),
                secondary = Color(0xFFAFCDBF),
                tertiary = Color(0xFFFFB77C)
            )
            else -> lightColorScheme(
                primary = Color(0xFF006B56),
                secondary = Color(0xFF4D635A),
                tertiary = Color(0xFF7D5700)
            )
        }
        MaterialTheme(colorScheme = scheme, content = content)
    }

    private fun approveDesktop(key: String) {
        val preferences = getSharedPreferences(AgentForegroundService.PREFS, MODE_PRIVATE)
        val keys = preferences.getStringSet(AgentForegroundService.KEY_TRUSTED, emptySet())
            .orEmpty()
            .toMutableSet()
        keys += key
        preferences.edit().putStringSet(AgentForegroundService.KEY_TRUSTED, keys).apply()
        trustedKeys = keys.sorted()
        AgentState.pendingDesktopKey = null
        AgentState.statusText = "Computer trusted"
    }

    private fun loadTrustedKeys() {
        trustedKeys = getSharedPreferences(AgentForegroundService.PREFS, MODE_PRIVATE)
            .getStringSet(AgentForegroundService.KEY_TRUSTED, emptySet())
            .orEmpty()
            .sorted()
    }

    private fun revokeDesktop(key: String) {
        val preferences = getSharedPreferences(AgentForegroundService.PREFS, MODE_PRIVATE)
        val keys = preferences.getStringSet(AgentForegroundService.KEY_TRUSTED, emptySet())
            .orEmpty()
            .toMutableSet()
        keys.remove(key)
        preferences.edit().putStringSet(AgentForegroundService.KEY_TRUSTED, keys).apply()
        trustedKeys = keys.sorted()
        AgentState.statusText = "Computer access revoked"
    }

    private fun changeUiLanguage(value: String) {
        language = if (value == "ru") "ru" else "en"
        getSharedPreferences(UI_PREFS, MODE_PRIVATE)
            .edit()
            .putString(KEY_LANGUAGE, language)
            .apply()
        PhoneBackupAgentApp.updateNotificationChannel(this, language == "ru")
    }

    private fun tr(english: String, russian: String): String =
        if (language == "ru") russian else english

    private fun localizedStatus(status: String): String {
        if (language != "ru") return status
        val exact = mapOf(
            "Agent stopped" to "Agent остановлен",
            "Waiting for VeXArk Desktop" to "Ожидание VeXArk Desktop",
            "PC connected" to "ПК подключён",
            "Computer rejected" to "Компьютер отклонён",
            "Computer trusted" to "Компьютер разрешён",
            "Computer access revoked" to "Доступ компьютера отозван",
            "Restore confirmed" to "Restore подтверждён",
            "Restore rejected" to "Restore отклонён",
            "Restore confirmation required" to "Требуется подтверждение Restore"
        )
        exact[status]?.let { return it }
        return status
            .replace("Connection closed:", "Соединение завершено:")
            .replace("Agent error:", "Ошибка Agent:")
            .replace("Command error:", "Ошибка команды:")
    }

    private fun requestAllFilesAccess() {
        if (Build.VERSION.SDK_INT < 30 || android.os.Environment.isExternalStorageManager()) {
            refreshState()
            return
        }
        startActivity(
            Intent(
                Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                Uri.parse("package:$packageName")
            )
        )
    }

    private fun openUsbDebuggingSettings() {
        val fragmentArguments = Bundle().apply {
            putString(SETTINGS_FRAGMENT_KEY, USB_DEBUGGING_PREFERENCE_KEY)
        }
        val developerSettings = Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS).apply {
            putExtra(SETTINGS_FRAGMENT_KEY, USB_DEBUGGING_PREFERENCE_KEY)
            putExtra(SETTINGS_FRAGMENT_ARGUMENTS, fragmentArguments)
        }

        try {
            startActivity(developerSettings)
        } catch (_: ActivityNotFoundException) {
            startActivity(Intent(Settings.ACTION_SETTINGS))
        }
    }

    private fun granted(permission: String): Boolean =
        ContextCompat.checkSelfPermission(this, permission) ==
            PackageManager.PERMISSION_GRANTED

    companion object {
        private const val UI_PREFS = "vexark_ui"
        private const val KEY_LANGUAGE = "language"
        private const val SETTINGS_FRAGMENT_ARGUMENTS = ":settings:show_fragment_args"
        private const val SETTINGS_FRAGMENT_KEY = ":settings:fragment_args_key"
        private const val USB_DEBUGGING_PREFERENCE_KEY = "enable_adb"
    }
}
