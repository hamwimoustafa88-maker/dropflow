// SPDX-License-Identifier: GPL-3.0-or-later
package dev.scouthub.dropflow.catalog

/** One category of catalog/tools.json (generated into [CatalogData] at build time). */
data class CatalogCategory(
    val id: String,
    val en: String,
    val ar: String,
    val inputs: List<String>,
    val outputs: List<String>,
    val platforms: List<String>,
)

/** One tool or action of catalog/tools.json. */
data class CatalogTool(
    val id: String,
    val category: String?,
    val en: String,
    val ar: String,
    val multi: Boolean,
    val minFiles: Int?,
    val maxFiles: Int?,
    val platforms: List<String>,
) {
    fun label(arabic: Boolean): String = if (arabic) ar else en
}

/** Lookups over the generated catalog. */
object Catalog {
    val categories: List<CatalogCategory> get() = CatalogData.categories
    val tools: List<CatalogTool> get() = CatalogData.tools
    val actions: List<CatalogTool> get() = CatalogData.actions

    private val byId: Map<String, CatalogTool> by lazy { (CatalogData.tools + CatalogData.actions).associateBy { it.id } }

    fun tool(id: String): CatalogTool? = byId[id]

    fun category(id: String): CatalogCategory? = CatalogData.categories.firstOrNull { it.id == id }
}
