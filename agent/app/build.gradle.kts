plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

val vexarkBuildId = (System.getenv("VEXARK_BUILD_ID") ?: "local")
    .replace("\\", "\\\\")
    .replace("\"", "\\\"")

android {
    namespace = "com.vex.phonebackup.agent"
    compileSdk = 37
    ndkVersion = "29.0.14206865"

    signingConfigs {
        create("production") {
            val keystorePath = System.getenv("VEXARK_KEYSTORE_PATH")
            if (!keystorePath.isNullOrBlank()) {
                storeFile = file(keystorePath)
                storePassword = System.getenv("VEXARK_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("VEXARK_KEY_ALIAS") ?: "vexark"
                keyPassword = System.getenv("VEXARK_KEY_PASSWORD")
            }
        }
    }

    defaultConfig {
        applicationId = "com.vex.phonebackup.agent"
        minSdk = 29
        targetSdk = 36
        versionCode = 14
        versionName = "0.9.0-beta.1"
        manifestPlaceholders["appLabel"] = "VeXArk Agent"
        buildConfigField("String", "BUILD_CHANNEL", "\"stable\"")
        buildConfigField("String", "BUILD_ID", "\"$vexarkBuildId\"")
        buildConfigField("int", "AGENT_PORT", "49321")
        buildConfigField("boolean", "DIAGNOSTICS_ENABLED", "false")
    }

    buildTypes {
        getByName("debug") {
            buildConfigField("String", "BUILD_CHANNEL", "\"debug\"")
            buildConfigField("int", "AGENT_PORT", "49321")
            buildConfigField("boolean", "DIAGNOSTICS_ENABLED", "true")
        }
        create("dev") {
            initWith(getByName("debug"))
            applicationIdSuffix = ".dev"
            versionNameSuffix = "-dev"
            manifestPlaceholders["appLabel"] = "VeXArk Agent Dev"
            buildConfigField("String", "BUILD_CHANNEL", "\"dev\"")
            buildConfigField("int", "AGENT_PORT", "49322")
            buildConfigField("boolean", "DIAGNOSTICS_ENABLED", "true")
            matchingFallbacks += listOf("debug")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
            // A stable build must never silently fall back to the debug certificate.
            signingConfig = signingConfigs.getByName("production")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.06.00"))
    implementation("androidx.core:core-ktx:1.19.0")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.11.0")
    implementation("androidx.lifecycle:lifecycle-service:2.11.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3:1.5.0-alpha22")
    implementation("org.bouncycastle:bcprov-jdk18on:1.83")
    implementation("com.github.topjohnwu.libsu:core:6.0.0")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    debugImplementation("androidx.compose.ui:ui-tooling")
}
