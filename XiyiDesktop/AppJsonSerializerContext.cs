using System.Collections.Generic;
using System.Text.Json.Serialization;
using XiyiDesktop.DesktopItems;
using XiyiDesktop.FileOperations;
using XiyiDesktop.Widgets;

namespace XiyiDesktop;

// Generate metadata for every persisted JSON type so storage also works with AOT.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "Settings")]
[JsonSerializable(typeof(DesktopLayoutStore.DesktopLayoutData), TypeInfoPropertyName = "DesktopLayout")]
[JsonSerializable(typeof(DesktopLayoutStore.DesktopItemPosition), TypeInfoPropertyName = "DesktopItemPosition")]
[JsonSerializable(typeof(List<DesktopUndoHistory.TrackedRecycleItem[]>), TypeInfoPropertyName = "UndoHistory")]
[JsonSerializable(typeof(List<WidgetDesktopPlacement>), TypeInfoPropertyName = "DesktopWidgets")]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
