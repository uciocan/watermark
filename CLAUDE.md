# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

This is a NopCommerce plugin (`Misc.Watermark`) that adds image watermarking to product, category, and manufacturer images. The current code on this branch targets **NopCommerce 4.90 / .NET 9**. The `master` branch contains the original NopCommerce 3.90 version; historical tags follow the pattern `v1.0.9_nop4.60`.

## Build

This project must be built inside a full NopCommerce 4.90 solution checkout. The `.csproj` expects `$(SolutionDir)` three levels up (`../../../`). Only `Nop.Web.Framework` is declared as a `<ProjectReference>`; all other NopCommerce assemblies are resolved transitively.

```sh
# Release build (outputs to ../../../Presentation/Nop.Web/Plugins/Misc.Watermark/)
dotnet build Nop.Plugin.Misc.Watermark/Nop.Plugin.Misc.Watermark.csproj -c Release
```

The `NopTarget` MSBuild target invokes `ClearPluginAssemblies.proj` after each build to strip redundant DLLs from the output folder — this matches NopCommerce's plugin deployment convention.

There are no unit tests in this repository.

## Architecture

### Plugin Registration

Registration is done via `Infrastructure/NopStartup.cs` (implements `INopStartup`, `Order = 3000`). The high order value ensures our registration runs after NopCommerce core and wins the last-registration-wins DI resolution:

```csharp
services.AddScoped<IPictureService, MiscWatermarkPictureService>();
services.AddScoped<FontProvider>();
```

`FontProvider` is a scoped singleton that caches `SKTypeface` instances (never dispose returned typefaces — they are shared). It exposes system fonts via `SKFontManager.Default` and custom `.ttf` fonts bundled in `Fonts/` (Open Sans, Roboto).

### Image Watermark Pipeline

`MiscWatermarkPictureService` extends `PictureService` and overrides `GetPictureUrlAsync(Picture, ...)`. The pipeline:

1. `IsPluginInstalledAsync()` — queries `IPluginService` to ensure the plugin is active; falls back to base if not
2. Build a thumbnail filename encoding `pictureId + seoName + targetSize + storeId`
3. Check cache via `IThumbService.GeneratedThumbExistsAsync()` — return immediately if found
4. Acquire a named `Mutex` on the filename (prevents duplicate generation across threads; `.Wait()` is intentional inside mutex scope — `await` cannot cross a mutex boundary)
5. Decode with `SKBitmap.Decode`, optionally resize, call `MakeImageWatermarkAsync()`
6. Re-encode and save via `IThumbService.SaveThumbAsync()`
7. Return URL via `IThumbService.GetThumbUrlAsync()`

### IThumbService Delegation (NopCommerce 4.90+)

From NopCommerce 4.90, all thumbnail file operations moved from `PictureService` into a separate `IThumbService`/`ThumbService`. Our service injects this via the constructor and delegates all thumb operations to it. This means the plugin **automatically works with Azure Blob storage** when `Nop.Plugin.Misc.AzureBlob` is active — that plugin registers `AzureThumbService` as `IThumbService`, and our service picks it up transparently. `MiscWatermarkAzurePictureService` (which existed in ≤4.60) is no longer needed.

### Watermark Drawing

`MakeImageWatermarkAsync` calls `IsWatermarkRequired()` — which does synchronous LINQ-to-DB queries against `IRepository<ProductPicture>`, `IRepository<Category>`, `IRepository<Manufacturer>` — to decide if the image belongs to a watermarked entity type.

Text watermark: `PlaceTextWatermark` uses SkiaSharp (`SKCanvas`, `SKPaint`). Font size is computed iteratively from 2pt upward (`ComputeMaxFontSize`) until the rotated bounding box exceeds the configured % of the image.

Image watermark: `PlaceImageWatermark` scales the watermark bitmap proportionally to fit within the configured % of the destination image, then applies per-position rendering. The watermark `SKImage` is loaded lazily via `AsyncLazy<SKImage>` from the NopCommerce picture store on first use.

### Settings & Multi-Store

`WatermarkSettings : ISettings` is persisted via `ISettingService` with per-store scope. The thumbnail filename includes `storeId` to isolate cached thumbnails per store (store ID 1 = default store, which omits the store suffix for backwards compatibility).

`CommonSettings` (shared by text and picture watermarks: size, opacity, position list) serializes to JSON in the database. It carries both a `[TypeConverter]` and a `[JsonConverter]` attribute to break the circular converter dependency that arises when `ISettingService` uses JSON serialization.

### Watermark Positioning

`WatermarkPosition` is a 9-value enum (3×3 grid). Both text and image watermarks accept a `List<WatermarkPosition>`, allowing placement at multiple positions simultaneously. Pixel coordinates are computed in `CalculateWatermarkPosition()`.

### Plugin Lifecycle

- **Install**: saves default `WatermarkSettings`, inserts the default watermark PNG into the NopCommerce picture store, imports locale XML resources for all active languages
- **Uninstall**: deletes the watermark picture, removes all settings, deletes locale resources (`Plugins.Misc.Watermark.*` prefix), clears cache, wipes the local thumbs directory

### Localization

Resource files are in `Resources/`: `Locale.default.xml` (English — applied to all languages), `Locale.ru.xml` (Russian), `Locale.uk.xml` (Ukrainian). All keys use the `Plugins.Misc.Watermark.*` namespace.

### Admin UI

`MiscWatermarkController` serves `~/Admin/MiscWatermark/Configure`. The font dropdown is populated from `FontProvider`, grouping custom fonts before system fonts. `ConfigurationModelValidator` (FluentValidation via `BaseNopValidator`) enforces: opacity in `[0, 1]`, size in `[0, 100]`.

## NopCommerce Version Tags

Tags follow the pattern `v{plugin-version}_nop{nop-version}` (e.g. `v1.0.9_nop4.60`). When porting to a new NopCommerce version, the primary change point is the `PictureService` constructor signature and any methods moved between services. In 4.90 the key changes from 4.60 were: `IProductAttributeService` added to the constructor, thumbnail operations extracted to `IThumbService`, and Azure storage split into a separate plugin.
