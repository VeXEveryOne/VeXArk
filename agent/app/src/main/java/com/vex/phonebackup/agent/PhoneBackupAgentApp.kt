package com.vex.phonebackup.agent

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import com.topjohnwu.superuser.Shell

class PhoneBackupAgentApp : Application() {
    override fun onCreate() {
        super.onCreate()
        AgentDiagnostics.initialize(this)
        Shell.enableVerboseLogging = BuildConfig.DIAGNOSTICS_ENABLED
        Shell.setDefaultBuilder(
            Shell.Builder.create()
                .setFlags(Shell.FLAG_MOUNT_MASTER)
                .setTimeout(90)
        )
        val language = getSharedPreferences("vexark_ui", MODE_PRIVATE)
            .getString("language", "en")
        updateNotificationChannel(this, language == "ru")
    }

    companion object {
        fun updateNotificationChannel(context: Context, russian: Boolean) {
            context.getSystemService(NotificationManager::class.java).createNotificationChannel(
                NotificationChannel(
                    AgentForegroundService.CHANNEL_ID,
                    if (russian) "Операции VeXArk" else "VeXArk operations",
                    NotificationManager.IMPORTANCE_LOW
                ).apply {
                    description = if (russian)
                        "Показывает активное резервное копирование и восстановление"
                    else
                        "Shows active backup and restore operations"
                    setShowBadge(false)
                }
            )
        }
    }
}
