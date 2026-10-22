// SPDX-License-Identifier: GPL-3.0-or-later
import groovy.json.JsonSlurper

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

// ---------------------------------------------------------------------------------------------
// Version: -Pdropflow.versionName=0.2.0 (CI passes the release tag). versionCode = major*10000 + minor*100 + patch.
// ---------------------------------------------------------------------------------------------
val dropflowVersion: String = providers.gradleProperty("dropflow.versionName").orNull?.removePrefix("v") ?: "0.1.0"

fun versionCodeOf(name: String): Int {
    val parts = name.substringBefore('-').split('.').map { it.toIntOrNull() ?: throw GradleException("Version '$name' is not like 1.2.3") }
    if (parts.size != 3 || parts.any { it !in 0..99 }) throw GradleException("Version '$name' must be major.minor.patch with parts 0-99")
    return maxOf(1, parts[0] * 10000 + parts[1] * 100 + parts[2])
}

// ---------------------------------------------------------------------------------------------
// Release signing. Never committed: the keystore path and passwords come from Gradle properties
// (dropflow.storeFile, dropflow.storePassword, dropflow.keyAlias, dropflow.keyPassword, e.g. in ~/.gradle/gradle.properties)
// or from the environment (DROPFLOW_KEYSTORE, DROPFLOW_KEYSTORE_PASSWORD, DROPFLOW_KEY_ALIAS, DROPFLOW_KEY_PASSWORD).
// -Pdropflow.requireSigning=true (CI) turns a missing keystore into a build failure instead of an unsigned APK.
// ---------------------------------------------------------------------------------------------
fun secret(property: String, env: String): String? =
    providers.gradleProperty(property).orNull?.takeIf { it.isNotBlank() }
        ?: providers.environmentVariable(env).orNull?.takeIf { it.isNotBlank() }

val keystorePath = secret("dropflow.storeFile", "DROPFLOW_KEYSTORE")
val requireSigning = providers.gradleProperty("dropflow.requireSigning").orNull?.toBoolean() ?: false
if (requireSigning && keystorePath == null) {
    throw GradleException(
        "Release signing is required but no keystore was given. Set DROPFLOW_KEYSTORE, DROPFLOW_KEYSTORE_PASSWORD, " +
            "DROPFLOW_KEY_ALIAS and DROPFLOW_KEY_PASSWORD (or the dropflow.* Gradle properties)."
    )
}

android {
    namespace = "dev.scouthub.dropflow"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "dev.scouthub.dropflow"
        minSdk = 29
        targetSdk = 36
        versionCode = versionCodeOf(dropflowVersion)
        versionName = dropflowVersion
    }

    signingConfigs {
        if (keystorePath != null) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = secret("dropflow.storePassword", "DROPFLOW_KEYSTORE_PASSWORD")
                    ?: throw GradleException("DROPFLOW_KEYSTORE_PASSWORD is missing")
                keyAlias = secret("dropflow.keyAlias", "DROPFLOW_KEY_ALIAS") ?: throw GradleException("DROPFLOW_KEY_ALIAS is missing")
                keyPassword = secret("dropflow.keyPassword", "DROPFLOW_KEY_PASSWORD")
                    ?: throw GradleException("DROPFLOW_KEY_PASSWORD is missing")
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
        release {
            if (keystorePath != null) signingConfig = signingConfigs.getByName("release")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    androidResources {
        // DropFlow speaks English and Arabic; drop the other languages that libraries bring along.
        localeFilters += listOf("en", "ar")
    }

    packaging {
        resources.excludes += setOf("META-INF/{AL2.0,LGPL2.1}", "META-INF/DEPENDENCIES")
    }
}

// ---------------------------------------------------------------------------------------------
// catalog/tools.json (the repository's single source of truth) -> CatalogData.kt, generated at build time.
// ---------------------------------------------------------------------------------------------
abstract class GenerateCatalog : DefaultTask() {
    @get:InputFile
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val catalog: RegularFileProperty

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun generate() {
        @Suppress("UNCHECKED_CAST")
        val root = JsonSlurper().parse(catalog.get().asFile, "UTF-8") as Map<String, Any?>

        fun q(s: Any?): String {
            if (s == null) return "null"
            val e = s.toString().replace("\\", "\\\\").replace("\"", "\\\"").replace("$", "\\$").replace("\n", "\\n")
            return "\"$e\""
        }
        fun strings(v: Any?): String = ((v as? List<*>) ?: emptyList<Any?>()).joinToString(", ", "listOf(", ")") { q(it) }
        fun int(v: Any?): String = (v as? Number)?.toInt()?.toString() ?: "null"
        fun platforms(v: Any?): String = strings(v)

        val out = StringBuilder()
        out.appendLine("// GENERATED from catalog/tools.json by the generateCatalog Gradle task. Do not edit.")
        out.appendLine("package dev.scouthub.dropflow.catalog")
        out.appendLine()
        out.appendLine("internal object CatalogData {")
        out.appendLine("    const val VERSION: Int = ${int(root["version"])}")
        out.appendLine()
        out.appendLine("    val categories: List<CatalogCategory> = listOf(")
        @Suppress("UNCHECKED_CAST")
        for (c in root["categories"] as List<Map<String, Any?>>) {
            out.appendLine(
                "        CatalogCategory(id = ${q(c["id"])}, en = ${q(c["en"])}, ar = ${q(c["ar"])}, " +
                    "inputs = ${strings(c["inputs"])}, outputs = ${strings(c["outputs"])}, platforms = ${platforms(c["platforms"])}),"
            )
        }
        out.appendLine("    )")
        out.appendLine()
        for (section in listOf("tools", "actions")) {
            out.appendLine("    val $section: List<CatalogTool> = listOf(")
            @Suppress("UNCHECKED_CAST")
            for (t in root[section] as List<Map<String, Any?>>) {
                out.appendLine(
                    "        CatalogTool(id = ${q(t["id"])}, category = ${q(t["cat"])}, en = ${q(t["en"])}, ar = ${q(t["ar"])}, " +
                        "multi = ${t["multi"] == true}, minFiles = ${int(t["minFiles"])}, maxFiles = ${int(t["maxFiles"])}, " +
                        "platforms = ${platforms(t["platforms"])}),"
                )
            }
            out.appendLine("    )")
            out.appendLine()
        }
        out.appendLine("}")

        val dir = outputDir.get().asFile.resolve("dev/scouthub/dropflow/catalog")
        dir.mkdirs()
        dir.resolve("CatalogData.kt").writeText(out.toString(), Charsets.UTF_8)
    }
}

val generateCatalog = tasks.register<GenerateCatalog>("generateCatalog") {
    catalog.set(rootProject.layout.projectDirectory.file("../catalog/tools.json"))
    outputDir.set(layout.buildDirectory.dir("generated/source/catalog"))
}

androidComponents {
    onVariants { variant ->
        variant.sources.java?.addGeneratedSourceDirectory(generateCatalog, GenerateCatalog::outputDir)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.androidx.exifinterface)
    implementation(libs.androidx.heifwriter)
    implementation(libs.androidx.documentfile)
    implementation(libs.kotlinx.coroutines.android)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.media3.transformer)
    implementation(libs.media3.effect)
    implementation(libs.media3.common)
    implementation(libs.media3.muxer)
    implementation(libs.media3.exoplayer)
    implementation(libs.media3.ui)
    implementation(libs.pdfbox.android)
    implementation(libs.zxing.core)

    testImplementation(libs.junit)
}
