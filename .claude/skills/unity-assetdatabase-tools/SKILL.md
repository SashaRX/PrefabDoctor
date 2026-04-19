---
name: unity-assetdatabase-tools
description: Используй при написании AssetPostprocessor, батчинге AssetDatabase-операций с StartAssetEditing, управлении импортом или генерации HideAndDontSave-ассетов.
---

# AssetDatabase Tools

Батчинг: StartAssetEditing в try/finally. AssetPostprocessor: GetPostprocessOrder + bypass HashSet. Progress bar: DisplayCancelableProgressBar + ClearProgressBar в finally. ЗАПРЕЩЕНО: без try/finally, Refresh в цикле, Resources.Load, FindAssets без t:.
