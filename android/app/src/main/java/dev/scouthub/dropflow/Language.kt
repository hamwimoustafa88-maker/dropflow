// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow

import android.app.LocaleManager
import android.content.Context
import android.content.res.Configuration
import android.os.Build
import android.os.LocaleList
import java.util.Locale

/**
 * App language: follows the system by default, with an in-app switch (English / العربية).
 * Android 13+ stores the choice as the per-app language (also visible in system settings); older versions
 * apply it to each activity and service context.
 */
object Language {
    const val SYSTEM = "system"
    const val ENGLISH = "en"
    const val ARABIC = "ar"

    private const val PREFS = "dropflow"
    private const val KEY = "language"

    fun get(context: Context): String {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val locales = context.getSystemService(LocaleManager::class.java)?.applicationLocales
            return if (locales == null || locales.isEmpty) SYSTEM else locales[0].language
        }
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, SYSTEM) ?: SYSTEM
    }

    /** Saves the choice; the caller recreates its activity on Android 12 and older (13+ does it by itself). */
    fun set(context: Context, language: String) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, language).apply()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            context.getSystemService(LocaleManager::class.java)?.applicationLocales =
                if (language == SYSTEM) LocaleList.getEmptyLocaleList() else LocaleList.forLanguageTags(language)
        }
    }

    /** For attachBaseContext on Android 12 and older. */
    fun wrap(base: Context): Context {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) return base
        val lang = base.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, SYSTEM) ?: SYSTEM
        if (lang == SYSTEM) return base
        val locale = Locale.forLanguageTag(lang)
        val config = Configuration(base.resources.configuration)
        config.setLocale(locale)
        config.setLayoutDirection(locale)
        return base.createConfigurationContext(config)
    }

    fun isArabic(context: Context): Boolean = context.resources.configuration.locales[0].language == ARABIC
}
