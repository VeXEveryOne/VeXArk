package com.vex.phonebackup.agent

import org.json.JSONArray
import org.json.JSONObject

enum class SettingSource(val wireName: String) {
    SYSTEM("system"), SECURE("secure"), GLOBAL("global"),
    LINEAGE_SYSTEM("lineageSystem"), LINEAGE_SECURE("lineageSecure"),
    LINEAGE_GLOBAL("lineageGlobal"), PACKAGE_DATA("packageData"), SERVICE("service")
}

enum class SettingValueKind {
    BOOLEAN, INTEGER, FLOAT, ENUM, STRING, PACKAGE, COMPONENT_LIST, TILE_LIST
}
enum class SettingRestoreClass { PORTABLE, EXACT_ROM, EXACT_DEVICE, ARCHIVE_ONLY, NEVER }

data class SettingRule(
    val id: String,
    val displayName: String,
    val adapter: String,
    val source: SettingSource,
    val kind: SettingValueKind,
    val restoreClass: SettingRestoreClass,
    val requiresConfirmation: Boolean = false,
    val capability: String? = null,
    val minimum: Double? = null,
    val maximum: Double? = null,
    val allowedValues: Set<String> = emptySet()
) {
    val key: String get() = id.substringAfter(':')

    fun validate(value: String): String? {
        if (value.length > 16_384 || value.any { it == '\u0000' }) return null
        return when (kind) {
            SettingValueKind.BOOLEAN -> when (value.lowercase()) {
                "0", "false" -> "0"
                "1", "true" -> "1"
                else -> null
            }
            SettingValueKind.INTEGER -> value.toLongOrNull()?.let { number ->
                if ((minimum == null || number >= minimum) &&
                    (maximum == null || number <= maximum)) number.toString() else null
            }
            SettingValueKind.FLOAT -> value.toDoubleOrNull()?.takeIf { number ->
                number.isFinite() && (minimum == null || number >= minimum) &&
                    (maximum == null || number <= maximum)
            }?.toString()
            SettingValueKind.ENUM -> value.takeIf { allowedValues.isEmpty() || it in allowedValues }
            SettingValueKind.PACKAGE -> value.takeIf { PACKAGE_PATTERN.matches(it) }
            SettingValueKind.COMPONENT_LIST -> value.takeIf { list ->
                list.split(',').all { it.isBlank() || COMPONENT_PATTERN.matches(it.trim()) }
            }
            SettingValueKind.TILE_LIST -> value.takeIf { list ->
                list.split(',').all { tile ->
                    val trimmed = tile.trim()
                    BUILT_IN_TILE_PATTERN.matches(trimmed) || CUSTOM_TILE_PATTERN.matches(trimmed)
                }
            }
            SettingValueKind.STRING -> value
        }
    }

    fun toJson(status: String, reason: String): JSONObject = JSONObject()
        .put("id", id)
        .put("displayName", displayName)
        .put("adapter", adapter)
        .put("source", source.wireName)
        .put("valueType", kind.name.lowercase())
        .put("restoreClass", restoreClass.name.lowercase())
        .put("requiresConfirmation", requiresConfirmation)
        .put("capability", capability ?: JSONObject.NULL)
        .put("status", status)
        .put("reason", reason)

    companion object {
        private val PACKAGE_PATTERN = Regex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$")
        private val COMPONENT_PATTERN = Regex("^[A-Za-z0-9_./:$-]+$")
        private val BUILT_IN_TILE_PATTERN = Regex("^[A-Za-z0-9_.:-]+$")
        private val CUSTOM_TILE_PATTERN =
            Regex("^custom\\([A-Za-z0-9_.]+/[A-Za-z0-9_.$]+\\)$")
    }
}

interface RomSettingsAdapter {
    val id: String
    fun isAvailable(identity: JSONObject): Boolean
    fun rules(): List<SettingRule>
}

private fun booleans(
    adapter: String,
    source: SettingSource,
    restoreClass: SettingRestoreClass,
    keys: List<String>,
    confirmation: Boolean = false,
    capability: String? = null
) = keys.map { key ->
    SettingRule("${source.wireName}:$key", key, adapter, source, SettingValueKind.BOOLEAN,
        restoreClass, confirmation, capability)
}

private fun integers(
    adapter: String,
    source: SettingSource,
    restoreClass: SettingRestoreClass,
    keys: List<String>,
    minimum: Double,
    maximum: Double,
    confirmation: Boolean = false,
    capability: String? = null
) = keys.map { key ->
    SettingRule("${source.wireName}:$key", key, adapter, source, SettingValueKind.INTEGER,
        restoreClass, confirmation, capability, minimum, maximum)
}

private fun floats(
    adapter: String,
    source: SettingSource,
    restoreClass: SettingRestoreClass,
    keys: List<String>,
    minimum: Double,
    maximum: Double
) = keys.map { key ->
    SettingRule("${source.wireName}:$key", key, adapter, source, SettingValueKind.FLOAT,
        restoreClass, minimum = minimum, maximum = maximum)
}

object AospSettingsAdapter : RomSettingsAdapter {
    override val id = "aosp"
    override fun isAvailable(identity: JSONObject) = identity.optInt("sdk") in 29..40
    override fun rules(): List<SettingRule> = buildList {
        addAll(booleans(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "accelerometer_rotation", "haptic_feedback_enabled", "sound_effects_enabled",
            "lockscreen_sounds_enabled", "vibrate_when_ringing", "dtmf_tone",
            "screen_brightness_mode", "keyboard_vibration_enabled"
        )))
        addAll(integers(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("screen_off_timeout"), 5_000.0, 86_400_000.0))
        addAll(integers(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("screen_brightness"), 0.0, 10_000.0))
        addAll(integers(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("user_rotation"), 0.0, 3.0))
        addAll(integers(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("pointer_speed"), -7.0, 7.0))
        addAll(booleans(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("dim_screen")))
        add(SettingRule("system:font_scale", "font_scale", id, SettingSource.SYSTEM,
            SettingValueKind.FLOAT, SettingRestoreClass.PORTABLE, minimum = 0.5, maximum = 2.0))
        add(SettingRule("system:device_font_scale", "Device font scale", id,
            SettingSource.SYSTEM, SettingValueKind.FLOAT, SettingRestoreClass.PORTABLE,
            minimum = 0.5, maximum = 2.0))
        add(SettingRule("system:time_12_24", "24-hour clock", id, SettingSource.SYSTEM,
            SettingValueKind.ENUM, SettingRestoreClass.PORTABLE,
            allowedValues = setOf("12", "24")))
        add(SettingRule("system:peak_refresh_rate", "peak_refresh_rate", id,
            SettingSource.SYSTEM, SettingValueKind.FLOAT, SettingRestoreClass.PORTABLE,
            capability = "aosp-settings", minimum = 30.0, maximum = 240.0))
        add(SettingRule("service:system_locale", "System language", id, SettingSource.SERVICE,
            SettingValueKind.STRING, SettingRestoreClass.PORTABLE,
            requiresConfirmation = true, capability = "aosp-settings"))
        addAll(booleans(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE, listOf(
            "doze_enabled", "doze_always_on", "doze_pulse_on_pick_up",
            "doze_pulse_on_double_tap", "doze_wake_screen_gesture", "wake_gesture_enabled",
            "double_tap_to_wake", "camera_double_tap_power_gesture_disabled",
            "lock_screen_show_notifications", "lock_screen_allow_private_notifications",
            "lock_screen_show_silent_notifications", "notification_badging",
            "notification_bubbles", "notification_history_enabled", "show_notification_snooze",
            "screensaver_enabled", "screensaver_activate_on_dock",
            "screensaver_activate_on_sleep", "charging_sounds_enabled",
            "charging_vibration_enabled", "status_bar_show_vibrate_icon",
            "adaptive_sleep", "double_tap_to_sleep", "doze_tap_gesture", "clock_seconds",
            "accessibility_display_inversion_enabled",
            "accessibility_display_magnification_enabled", "back_gesture_haptic",
            "lock_screen_show_only_unseen_notifications", "lock_screen_show_qr_code_scanner",
            "lockscreen_show_controls", "lockscreen_show_wallet", "one_handed_mode_enabled",
            "show_ime_with_hard_keyboard", "nfc_payment_foreground", "aware_lock_enabled",
            "back_gesture_arrow", "swipe_bottom_to_notification_enabled"
        )))
        add(SettingRule("secure:accessibility_display_magnification_scale",
            "Accessibility magnification scale", id, SettingSource.SECURE,
            SettingValueKind.FLOAT, SettingRestoreClass.PORTABLE, minimum = 1.0, maximum = 8.0))
        addAll(integers(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE,
            listOf("ui_night_mode"), 0.0, 3.0))
        addAll(integers(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE,
            listOf("dark_theme_custom_start_time", "dark_theme_custom_end_time"),
            0.0, 86_400_000_000.0))
        addAll(integers(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE,
            listOf("long_press_timeout", "multi_press_timeout"), 100.0, 10_000.0))
        addAll(integers(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE,
            listOf("volume_hush_gesture"), 0.0, 3.0))
        add(SettingRule("secure:icon_blacklist", "Status bar icon visibility", id,
            SettingSource.SECURE, SettingValueKind.STRING, SettingRestoreClass.PORTABLE))
        add(SettingRule("secure:sysui_qs_tiles", "Quick settings tiles", id,
            SettingSource.SECURE, SettingValueKind.TILE_LIST, SettingRestoreClass.PORTABLE))
        add(SettingRule("secure:theme_customization_overlay_packages", "Theme overlays", id,
            SettingSource.SECURE, SettingValueKind.STRING, SettingRestoreClass.ARCHIVE_ONLY,
            capability = "theme-overlays"))
        addAll(floats(id, SettingSource.GLOBAL, SettingRestoreClass.ARCHIVE_ONLY,
            listOf("animator_duration_scale", "transition_animation_scale", "window_animation_scale"),
            0.0, 10.0))
        addAll(booleans(id, SettingSource.GLOBAL, SettingRestoreClass.PORTABLE, listOf(
            "heads_up_notifications_enabled", "apply_ramping_ringer", "power_sounds_enabled",
            "auto_time", "auto_time_zone", "theater_mode_on", "ambient_enabled",
            "ambient_tilt_to_bright", "ambient_tilt_to_wake", "ambient_touch_to_wake"
        )))
        add(SettingRule("global:add_users_when_locked", "Add users from lockscreen", id,
            SettingSource.GLOBAL, SettingValueKind.BOOLEAN, SettingRestoreClass.PORTABLE,
            requiresConfirmation = true))
        addAll(listOf("default_input_method", "enabled_input_methods",
            "enabled_accessibility_services").map { key ->
            SettingRule("secure:$key", key, id, SettingSource.SECURE,
                SettingValueKind.COMPONENT_LIST, SettingRestoreClass.PORTABLE,
                requiresConfirmation = true)
        })
        add(SettingRule("secure:accessibility_enabled", "Accessibility services enabled", id,
            SettingSource.SECURE, SettingValueKind.BOOLEAN, SettingRestoreClass.PORTABLE,
            requiresConfirmation = true))
        add(SettingRule("secure:touch_exploration_enabled", "Touch exploration", id,
            SettingSource.SECURE, SettingValueKind.BOOLEAN, SettingRestoreClass.PORTABLE,
            requiresConfirmation = true))
        addAll(listOf("autofill_service", "default_voice_input_method",
            "screensaver_default_component", "selected_spell_checker").map { key ->
            SettingRule("secure:$key", key, id, SettingSource.SECURE,
                SettingValueKind.COMPONENT_LIST, SettingRestoreClass.PORTABLE,
                requiresConfirmation = true)
        })
        addAll(listOf("navigation_mode", "install_non_market_apps", "voice_interaction_service",
            "voice_recognition_service", "nearby_sharing_component", "qs_auto_tiles",
            "active_unlock_provider", "active_unlock_target", "lockscreen.disabled",
            "lockscreen.options", "face_unlock_attention_required",
            "face_unlock_dismisses_keyguard", "face_unlock_re_enroll",
            "facelock_detection_threshold", "facelock_liveliness_recognition_threshold",
            "facelock_max_center_movement").map { key ->
            SettingRule("secure:$key", key, id, SettingSource.SECURE,
                SettingValueKind.STRING, SettingRestoreClass.ARCHIVE_ONLY)
        })
        addAll(listOf("ringtone", "ringtone2", "notification_sound", "alarm_alert",
            "volume_alarm", "volume_bluetooth_sco", "volume_music", "volume_music_speaker",
            "volume_notification", "volume_ring", "volume_system", "volume_voice").map { key ->
            SettingRule("system:$key", key, id, SettingSource.SYSTEM,
                SettingValueKind.STRING, SettingRestoreClass.ARCHIVE_ONLY)
        })
        addAll(listOf("wifi_on", "bluetooth_on", "mobile_data", "low_power", "mode_ringer",
            "zen_mode", "zen_duration", "device_name", "wifi_scan_always_enabled").map { key ->
            SettingRule("global:$key", key, id, SettingSource.GLOBAL,
                SettingValueKind.STRING, SettingRestoreClass.ARCHIVE_ONLY,
                requiresConfirmation = true)
        })
    }
}

object LineageSettingsAdapter : RomSettingsAdapter {
    override val id = "lineage"
    override fun isAvailable(identity: JSONObject) =
        identity.optJSONArray("capabilities")?.let { array ->
            (0 until array.length()).any { array.optString(it) == "lineage-settings" }
        } == true
    override fun rules(): List<SettingRule> = buildList {
        addAll(booleans(id, SettingSource.LINEAGE_SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "swap_volume_keys_on_rotation", "system_profiles_enabled", "proximity_on_wake",
            "double_tap_sleep_gesture", "high_touch_polling_rate_enable",
            "high_touch_sensitivity_enable", "navigation_bar_hint"
        ), capability = "lineage-settings"))
        addAll(integers(id, SettingSource.LINEAGE_SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "qs_quick_pulldown", "status_bar_battery_style", "status_bar_clock"
        ), 0.0, 20.0, capability = "lineage-settings"))
        add(SettingRule("lineageSystem:display_color_adjustment", "Display color adjustment", id,
            SettingSource.LINEAGE_SYSTEM, SettingValueKind.STRING, SettingRestoreClass.PORTABLE,
            capability = "lineage-livedisplay"))
        addAll(booleans(id, SettingSource.LINEAGE_SECURE, SettingRestoreClass.PORTABLE, listOf(
            "lockscreen_visualizer", "volume_panel_on_left", "qs_show_brightness_slider"
        ), capability = "lineage-settings"))
        add(SettingRule("lineageSecure:stats_collection", "Lineage statistics", id,
            SettingSource.LINEAGE_SECURE, SettingValueKind.BOOLEAN,
            SettingRestoreClass.ARCHIVE_ONLY, capability = "lineage-settings"))
        add(SettingRule("lineageGlobal:wake_when_plugged_or_unplugged", "Wake on power", id,
            SettingSource.LINEAGE_GLOBAL, SettingValueKind.BOOLEAN,
            SettingRestoreClass.PORTABLE, capability = "lineage-settings"))
        addAll(integers(id, SettingSource.LINEAGE_SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "battery_light_brightness_level", "battery_light_brightness_level_zen",
            "notification_light_brightness_level", "notification_light_brightness_level_zen"
        ), 0.0, 255.0, capability = "lineage-settings"))
        addAll(booleans(id, SettingSource.LINEAGE_SYSTEM, SettingRestoreClass.PORTABLE,
            listOf("notification_light_pulse_custom_enable"), capability = "lineage-settings"))
        add(SettingRule("lineageSystem:live_display_hinted", "LiveDisplay UI hint", id,
            SettingSource.LINEAGE_SYSTEM, SettingValueKind.BOOLEAN,
            SettingRestoreClass.ARCHIVE_ONLY, capability = "lineage-settings"))
        addAll(listOf("charging_control_enabled", "charging_control_mode",
            "charging_control_limit").map { key ->
            SettingRule("service:$key", key, id, SettingSource.SERVICE,
                SettingValueKind.STRING, SettingRestoreClass.PORTABLE,
                capability = "lineage-health")
        })
        addAll(listOf("live_display_color_profile", "live_display_color_temperature_day",
            "live_display_color_temperature_night", "live_display_auto_outdoor").map { key ->
            SettingRule("service:$key", key, id, SettingSource.SERVICE,
                SettingValueKind.STRING, SettingRestoreClass.PORTABLE,
                capability = "lineage-livedisplay")
        })
    }
}

object Crdroid12Adapter : RomSettingsAdapter {
    override val id = "crdroid12"
    override fun isAvailable(identity: JSONObject) =
        identity.optString("crDroidBuildVersion").startsWith("12")
    override fun rules(): List<SettingRule> = buildList {
        addAll(booleans(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "bluetooth_show_battery", "network_traffic_enabled", "status_bar_brightness_control",
            "status_bar_brightness_control_lockscreen", "status_bar_clock_bold",
            "status_bar_custom_header", "statusbar_battery_bar", "statusbar_battery_bar_animate",
            "statusbar_colored_icons", "statusbar_notif_count", "wifi_standard_icon",
            "double_tap_sleep_lockscreen", "notification_light_pulse", "data_disabled_icon",
            "show_fourg_icon"
        ), capability = "crdroid-settings"))
        addAll(integers(id, SettingSource.SYSTEM, SettingRestoreClass.PORTABLE, listOf(
            "status_bar_clock", "status_bar_clock_date_display", "status_bar_clock_date_position",
            "status_bar_battery_style", "statusbar_battery_bar_style",
            "statusbar_battery_bar_thickness", "statusbar_extra_padding_start",
            "statusbar_extra_padding_end", "statusbar_extra_padding_top",
            "network_traffic_mode", "network_traffic_refresh_interval", "network_traffic_units",
            "back_gesture_height", "status_bar_clock_period_separator",
            "status_bar_custom_header_height", "status_bar_custom_header_shadow"
        ), 0.0, 100.0, capability = "crdroid-settings"))
        add(SettingRule("system:udfps_icon", "UDFPS icon", id, SettingSource.SYSTEM,
            SettingValueKind.INTEGER, SettingRestoreClass.EXACT_ROM,
            requiresConfirmation = true, capability = "crdroid-settings",
            minimum = 0.0, maximum = 100.0))
        addAll(booleans(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE, listOf(
            "notification_row_transparency_lockscreen", "pulse_on_new_tracks",
            "qs_show_auto_brightness", "qs_show_brightness_slider", "qs_show_data_usage",
            "qs_show_ringer_mode", "qs_show_volume_slider", "qs_tile_haptic",
            "enable_camera_privacy_indicator", "enable_location_privacy_indicator",
            "enable_projection_privacy_indicator", "ambient_media_art_enabled",
            "ambient_pulse_enabled", "lockscreen_weather_enabled",
            "lockscreen_weather_humidity_info", "lockscreen_weather_wind_info",
            "media_squiggle_animation", "show_clipboard_overlay"
        ), capability = "crdroid-settings"))
        addAll(integers(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE, listOf(
            "notification_row_transparency", "qs_tiles_columns", "qs_tiles_columns_landscape",
            "qs_tiles_rows", "qs_tiles_rows_landscape", "qqs_tiles_rows",
            "qs_brightness_slider_position", "qs_tile_animation_style", "qs_tile_shape",
            "qs_tile_icon_shape", "edge_light_style", "edge_light_pulse_count",
            "edge_light_stroke_width", "ls_media_art_blur_level", "ls_media_art_fade_level",
            "volume_dialog_dismiss_timeout"
        ), 0.0, 1000.0, capability = "crdroid-settings"))
        addAll(booleans(id, SettingSource.SECURE, SettingRestoreClass.PORTABLE, listOf(
            "ax_dynamic_bar_enabled", "ax_dynamic_bar_keyguard_enabled", "event_alarm",
            "event_audio_recording", "event_biometric_unlock", "event_bluetooth", "event_call",
            "event_charging", "event_clipboard", "event_hotspot", "event_media",
            "event_notification", "event_ringer", "event_sports", "event_stopwatch",
            "event_timer", "event_torch", "event_vpn", "edge_light_enabled"
        ), capability = "crdroid-settings"))
        addAll(booleans(id, SettingSource.SECURE, SettingRestoreClass.EXACT_ROM, listOf(
            "enable_lockscreen_quick_settings", "lockscreen_enable_power_menu",
            "notification_guts_kill_app_button", "advanced_reboot", "pm_downgrade_allowed",
            "power_menu_locked_show_content"
        ), confirmation = true, capability = "crdroid-settings"))
        addAll(listOf("play_integrity_fix", "tricky_store").map { key ->
            SettingRule("secure:$key", key, id, SettingSource.SECURE,
                SettingValueKind.BOOLEAN, SettingRestoreClass.ARCHIVE_ONLY,
                capability = "crdroid-settings")
        })
    }
}

object Launcher3Adapter : RomSettingsAdapter {
    override val id = "launcher3"
    override fun isAvailable(identity: JSONObject) = hasCapability(identity, "launcher3")
    override fun rules() = launcherKeys.map { key ->
        SettingRule("packageData:$key", key, id, SettingSource.PACKAGE_DATA,
            SettingValueKind.STRING, SettingRestoreClass.EXACT_ROM, capability = "launcher3")
    }
    private val launcherKeys = listOf(
        "pref_add_icon_to_home", "pref_allowRotation", "pref_allow_wallpaper_scrolling",
        "pref_allow_wallpaper_zooming", "pref_app_drawer_opacity", "pref_app_drawer_sorting_mode",
        "pref_auto_hide_dots", "pref_custom_font_size", "pref_custom_icon_size",
        "pref_desktop_show_labels", "pref_drawer_show_labels", "pref_icon_badging",
        "pref_icon_pack", "pref_recents_clear_all", "pref_recents_screenshot",
        "pref_show_statusbar", "pref_sleep_gesture", "pref_workspace_lock"
    )
}

object XiaomiDadaPartsAdapter : RomSettingsAdapter {
    override val id = "xiaomi-dada"
    override fun isAvailable(identity: JSONObject) =
        identity.optString("device") == "dada" && hasCapability(identity, "xiaomi-dada-parts")
    override fun rules() = keys.map { key ->
        SettingRule("packageData:$key", key, id, SettingSource.PACKAGE_DATA,
            SettingValueKind.STRING, SettingRestoreClass.EXACT_DEVICE,
            requiresConfirmation = true, capability = "xiaomi-dada-parts")
    }
    private val keys = listOf(
        "auto_hbm", "auto_hbm_threshold", "bypass_charge", "dc_dimming", "hbm",
        "hbm_disable_time", "htsr_enable", "saturation", "dirac_enable", "dirac_hifi_pref",
        "dirac_preset_pref", "scenario_selection", "touch_game_mode", "touch_resistant",
        "touch_response", "touch_sensitivity", "game_bar_enable", "game_bar_position",
        "game_bar_update_interval"
    )
}

object GameSpaceAdapter : RomSettingsAdapter {
    override val id = "gamespace"
    override fun isAvailable(identity: JSONObject) = hasCapability(identity, "gamespace")
    override fun rules() = keys.map { key ->
        SettingRule("packageData:$key", key, id, SettingSource.PACKAGE_DATA,
            SettingValueKind.STRING, SettingRestoreClass.EXACT_ROM, capability = "gamespace")
    }
    private val keys = listOf(
        "gamespace_game_list", "gamespace_auto_brightness_disabled", "gamespace_auto_dnd",
        "gamespace_calls_mode", "gamespace_danmaku_notification_mode", "gamespace_lock_gesture",
        "gamespace_ringer_mode", "gamespace_stay_awake", "game_cache_management",
        "game_memory_management", "game_optimization", "bypass_charge_enabled"
    )
}

object ThemeOverlayAdapter : RomSettingsAdapter {
    override val id = "theme-overlays"
    override fun isAvailable(identity: JSONObject) = hasCapability(identity, "theme-overlays")
    override fun rules() = listOf(
        SettingRule("service:theme_overlays", "Enabled theme overlays", id,
            SettingSource.SERVICE, SettingValueKind.STRING, SettingRestoreClass.EXACT_ROM,
            requiresConfirmation = true, capability = "theme-overlays"),
        SettingRule("secure:theme_customization_overlay_packages", "Theme overlay selection", id,
            SettingSource.SECURE, SettingValueKind.STRING, SettingRestoreClass.ARCHIVE_ONLY,
            capability = "theme-overlays")
    )
}

object RomSettingsCatalog {
    private val semanticServiceRuleIds = setOf(
        "service:system_locale", "service:theme_overlays"
    )
    val adapters: List<RomSettingsAdapter> = listOf(
        AospSettingsAdapter, LineageSettingsAdapter, Crdroid12Adapter, Launcher3Adapter,
        XiaomiDadaPartsAdapter, GameSpaceAdapter, ThemeOverlayAdapter
    )

    fun activeRules(identity: JSONObject): List<SettingRule> = adapters
        .filter { it.isAvailable(identity) }
        .flatMap { it.rules() }
        .distinctBy { it.id }

    fun coverage(identity: JSONObject): JSONArray = JSONArray().apply {
        adapters.forEach { adapter ->
            val available = adapter.isAvailable(identity)
            adapter.rules().forEach { rule ->
                val hasSemanticApply = rule.source != SettingSource.PACKAGE_DATA &&
                    (rule.source != SettingSource.SERVICE || rule.id in semanticServiceRuleIds)
                val supported = available && hasSemanticApply &&
                    rule.restoreClass !in setOf(
                        SettingRestoreClass.ARCHIVE_ONLY, SettingRestoreClass.NEVER)
                val status = when {
                    supported -> "supported"
                    rule.restoreClass == SettingRestoreClass.NEVER -> "ignored"
                    else -> "archived"
                }
                val reason = when {
                    !available -> "adapter_or_capability_unavailable"
                    !hasSemanticApply -> "semantic_adapter_not_implemented"
                    rule.restoreClass == SettingRestoreClass.ARCHIVE_ONLY -> "no_safe_apply_api"
                    rule.restoreClass == SettingRestoreClass.NEVER -> "sensitive_state"
                    else -> "validated_adapter"
                }
                put(rule.toJson(status, reason))
            }
        }
    }

    private fun hasCapability(identity: JSONObject, capability: String): Boolean =
        identity.optJSONArray("capabilities")?.let { array ->
            (0 until array.length()).any { array.optString(it) == capability }
        } == true
}

private fun hasCapability(identity: JSONObject, capability: String): Boolean =
    identity.optJSONArray("capabilities")?.let { array ->
        (0 until array.length()).any { array.optString(it) == capability }
    } == true
