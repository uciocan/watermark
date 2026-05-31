# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is a NopCommerce plugin (`Misc.Watermark`) that adds image watermarking functionality to an e-commerce store. It targets NopCommerce 3.90 and .NET Framework 4.5.1. The plugin lives in `Nop.Plugin.Misc.Watermark/` and is built as a class library DLL.

## Build

This project must be built as part of a full NopCommerce solution. The `.csproj` expects `$(SolutionDir)` to point to the root of a NopCommerce checkout (three levels up: `../../../`). NopCommerce core projects (`Nop.Core`, `Nop.Data`, `Nop.Services`, `Nop.Web.Framework`) are referenced by project reference, not NuGet.

```
# Build via MSBuild (requires full NopCommerce solution)
msbuild Nop.Plugin.Misc.Watermark/Nop.Plugin.Misc.Watermark.csproj /p:Configuration=Release
```

**Output directory**: `$(SolutionDir)\Presentation\Nop.Web\Plugins\Misc.Watermark\`

`PreBuild.targets` strips `CopyLocal=true` from all references so that only this plugin's DLL is emitted — NopCommerce core DLLs are NOT copied to the output folder, matching the NopCommerce plugin convention.

There are no unit tests in this repository.

## Architecture

### Plugin Registration Pattern

NopCommerce plugins use Autofac for IoC. `DependencyRegistrar` (priority 5) replaces the default `IPictureService` with `MiscWatermarkPictureService`:

```csharp
builder.RegisterType<MiscWatermarkPictureService>().As<IPictureService>().InstancePerLifetimeScope();
```

This means every call to `IPictureService.GetPictureUrl()` in NopCommerce is intercepted by this plugin.

### Image Watermark Pipeline

`MiscWatermarkPictureService` extends `Nop.Services.Media.PictureService` and overrides `GetPictureUrl()`. The pipeline:

1. Check if plugin is installed via `IPluginFinder` (guards against being active when uninstalled)
2. Build a thumbnail filename that encodes `pictureId + seoName + targetSize + storeId`
3. Use a named `Mutex` on the filename to prevent duplicate thumbnail generation across threads
4. If thumbnail doesn't exist: resize with `ImageResizer`, then call `MakeImageWatermark()`
5. `MakeImageWatermark()` checks `IsWatermarkRequired()` — queries product/category/manufacturer repositories to determine if the picture belongs to an entity type that has watermarking enabled
6. Applies text watermark (`PlaceTextWatermark`) and/or image watermark (`PlaceImageWatermark`) using `System.Drawing`
7. Save thumbnail

The watermark bitmap (`_watermarkBitmap`) is `Lazy<Bitmap>` — loaded once from the NopCommerce picture store on first use.

### Settings & Multi-Store

`WatermarkSettings` implements `ISettings` and is persisted via `ISettingService`. Settings are loaded per-store: `_settingService.LoadSetting<WatermarkSettings>(_storeContext.CurrentStore.Id)`. The thumbnail filename includes `storeId` to separate cached thumbnails per store.

`CommonSettings` (shared between text and picture watermarks) is serialized to JSON for database storage. This requires a custom `TypeConverter` (`CommonSettingsConvertor`) and `JsonConverter` (`NoTypeConverterJsonConverter<T>`) to break the circular converter dependency.

### Watermark Positioning

`WatermarkPosition` enum has 9 values (3×3 grid). Both text and picture watermarks accept a `List<WatermarkPosition>`, so a watermark can be rendered at multiple positions simultaneously. Pixel coordinates are computed in `CalculateWatermarkPosition()`.

Text watermark size is computed by iterating font sizes from 2pt upward until the rotated text bounding box exceeds the configured percentage of the image dimensions (`ComputeMaxFontSize`).

### Plugin Lifecycle

- **Install** (`WatermarkPlugin.Install`): saves default `WatermarkSettings`, inserts the default watermark PNG into the NopCommerce picture store, loads locale XML resources for all active languages
- **Uninstall** (`WatermarkPlugin.Uninstall`): deletes the watermark picture, removes all settings, deletes locale resources, clears the NopCommerce cache, and wipes `~/content/images/thumbs/` so cached unwatermarked thumbnails are regenerated

### Localization

Locale strings are in `Resources/Locale.default.xml` (English, applied to all languages), `Locale.ru.xml` (Russian), and `Locale.ua.xml` (Ukrainian). All resource keys use the `Plugins.Misc.Watermark.*` namespace.

### Admin UI

`MiscWatermarkController` handles the `~/Admin/MiscWatermark/Configure` route. It builds the font list from `System.Drawing.FontFamily.Families`. `ConfigurationModelValidator` (FluentValidation) enforces: opacity in `(0, 1]`, size in `[0, 100]`.

## NopCommerce Version Branches

The repository maintains separate branches/tags per NopCommerce version (e.g. `v1.0.9_nop4.60`). The current `master` branch targets NopCommerce 3.90. When porting to a different version, check what changed in `PictureService.GetPictureUrl()` in that NopCommerce release — the override must match the base method's signature exactly.
