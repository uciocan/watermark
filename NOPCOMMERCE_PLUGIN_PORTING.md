# NopCommerce Plugin Porting Guide

Lessons learned porting `Misc.Watermark` from NopCommerce 3.90 to 4.90.
Use this as a reference for future plugin development and version upgrades.

---

## Breaking changes 3.90 → 4.90

### Dependency Injection
- Replaced Autofac `IDependencyRegistrar` with `INopStartup` (implements `IStartup`)
- Use a high `Order` value (e.g. `3000`) to win last-registration-wins DI resolution
- Register via `IServiceCollection`, not Autofac container directly

```csharp
public class NopStartup : INopStartup
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPictureService, MiscWatermarkPictureService>();
    }
    public void Configure(IApplicationBuilder application) { }
    public int Order => 3000;
}
```

### PictureService / IThumbService
- From 4.90, **all thumbnail file operations** moved from `PictureService` to a separate `IThumbService`/`ThumbService`
- Inject `IThumbService` in the constructor and use it for:
  - `GeneratedThumbExistsAsync(thumbFilePath, thumbFileName)`
  - `GetThumbLocalPathByFileNameAsync(thumbFileName)`
  - `SaveThumbAsync(thumbFilePath, thumbFileName, mimeType, binary)`
  - `GetThumbUrlAsync(thumbFileName, storeLocation)`
  - `DeletePictureThumbsAsync(picture)`
- Azure storage is now handled transparently: `AzureThumbService` implements `IThumbService`, no separate Azure subclass needed

### PictureService constructor
- Added `IProductAttributeService` parameter in 4.90
- Added `IThumbService` parameter in 4.90
- Base class constructor order changed — verify against the actual version source

### Protected fields in PictureService
**Do NOT redeclare** these fields in subclasses — they are already `protected readonly` in the base:
```
_productPictureRepository
_fileProvider
_settingService
_mediaSettings
_thumbService
```
Redeclaring them causes `CS0108` hiding warnings and double-assignment bugs.

### Permissions
```csharp
// Old (3.90 / 4.60)
StandardPermissionProvider.ManagePlugins

// New (4.90)
StandardPermission.Configuration.MANAGE_PLUGINS
```

### Area names
```csharp
// Old
[Area(AreaNames.Admin)]

// New (4.90)
[Area(AreaNames.ADMIN)]
```

### Plugin check
```csharp
// Old — wrong, compares Task to null
if (_pluginService.GetPluginDescriptorBySystemName("Misc.Watermark") != null)

// New — correct async
private async Task<bool> IsPluginInstalledAsync() =>
    (await _pluginService.GetPluginDescriptorBySystemNameAsync<IPlugin>("Misc.Watermark")) != null;
```

---

## Thumbnail path

NopCommerce 4.90 stores thumbnails in **`wwwroot/images/thumbs/`**, not `wwwroot/thumbs/`.  
Do NOT use `NopMediaDefaults.ImageThumbsPath` (returns `"thumbs"`) for direct file operations — it resolves to the wrong path. Use `IThumbService` for all thumb operations.

If you need to clear all thumbs manually (e.g. on plugin save):
```csharp
// Try both known paths — value of ImageThumbsPath may differ between versions
foreach (var relPath in new[] { Path.Combine("images", "thumbs"), "thumbs" })
{
    var dir = _fileProvider.GetAbsolutePath(relPath);
    if (!Directory.Exists(dir)) continue;
    foreach (var file in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
    {
        try { file.Delete(); }
        catch { /* skip locked/read-only files */ }
    }
}
```

---

## Dependencies — critical rules

### Never use Nito.AsyncEx
`Nito.AsyncEx` is a dependency of `Nop.Core` at compile time but is **not deployed** with NopCommerce. Using `AsyncLazy<T>` in a plugin causes `FileNotFoundException` at startup → HTTP 500.30.

Use the built-in `Lazy<Task<T>>` instead:

```csharp
// WRONG — requires Nito.AsyncEx.Coordination.dll (not deployed)
private readonly AsyncLazy<SKImage> _watermarkImage;
_watermarkImage = new AsyncLazy<SKImage>(async () => { ... });
var image = await _watermarkImage.Task;

// CORRECT — BCL only
private readonly Lazy<Task<SKImage>> _watermarkImage;
_watermarkImage = new Lazy<Task<SKImage>>(async () => { ... });
var image = await _watermarkImage.Value;
```

### General rule
Before using any NuGet package in a plugin, verify its DLL is present in the production `bin/` folder of the NopCommerce app. Only use packages that NopCommerce itself declares as dependencies and actually uses at runtime.

---

## Windows filename safety

Product SEO slugs can contain characters illegal in Windows filenames (`*`, `?`, `<`, `>`, `|`, `:`, `"`, `\`, `/`). Always sanitize before building a thumbnail filename:

```csharp
var seoFileName = picture.SeoFilename;
if (!string.IsNullOrEmpty(seoFileName))
{
    var invalidChars = Path.GetInvalidFileNameChars();
    seoFileName = new string(seoFileName.Where(c => !invalidChars.Contains(c)).ToArray());
}
```

---

## SkiaSharp 3.x — obsolete APIs

SkiaSharp 3.x deprecated several APIs still used in older plugin code. They compile with warnings but work. To silence warnings, migrate:

| Obsolete | Replacement |
|---|---|
| `SKBitmap.Resize(size, SKFilterQuality)` | `SKBitmap.Resize(size, SKSamplingOptions)` |
| `SKPaint.Typeface` | `SKFont.Typeface` |
| `SKPaint.TextSize` | `SKFont.Size` |
| `SKPaint.TextAlign` | Pass `SKTextAlign` to `DrawText` overload |
| `SKPaint.MeasureText(string, ref SKRect)` | `SKFont.MeasureText(...)` |
| `SKCanvas.DrawText(string, float, float, SKPaint)` | `SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)` |
| `SKPaint.FilterQuality` | `SKSamplingOptions` |

### Always null-check SKBitmap.Decode
`SKBitmap.Decode` **throws** `ArgumentNullException` (does not return null) when it cannot decode an image (unsupported format, corrupt data, WebP without codec support). Always wrap in try-catch:

```csharp
SKBitmap inputImage;
try { inputImage = SKBitmap.Decode(pictureBinary); }
catch { inputImage = null; }

if (inputImage == null)
{
    // Save original without watermark
    try { await thumbService.SaveThumbAsync(...); } catch { }
    return (await thumbService.GetThumbUrlAsync(...), picture);
}
```

---

## Mutex pattern for thumbnail generation

`async/await` cannot be used inside a `Mutex`-protected region. The `.Wait()` calls below are **intentional**:

```csharp
using var mutex = new Mutex(false, thumbFileName);
mutex.WaitOne();
try
{
    // synchronous image processing
    MakeImageWatermarkAsync(outputImage, pictureId).Wait();
    thumbService.SaveThumbAsync(...).Wait();
}
finally
{
    mutex.ReleaseMutex();
}
```

---

## Performance — thumbnail regeneration

Saving plugin settings triggers `DeleteThumbs()` which deletes **all** cached thumbnails. On the next requests, every image must be regenerated (decoded, watermarked, saved). Under high traffic (e.g. Google crawler) this can saturate the thread pool and make the site slow for minutes.

**Best practice:** do the forced save at night or during low traffic. After the initial regeneration completes, the site returns to normal performance.

---

## HTTP 500.30 troubleshooting

`HTTP Error 500.30 - ASP.NET Core app failed to start` means the app cannot start at all. Common causes in NopCommerce plugins:

1. **Missing assembly**: the plugin DLL references an assembly not present in production `bin/`  
   → Check with `strings plugin.dll | grep -i "assemblyname"` or Event Viewer
2. **Constructor mismatch**: base class constructor changed between minor versions  
   → Recompile against the exact version running in production
3. **Broken DLL on disk**: partial upload, file locked during update  
   → Stop IIS app pool, manually replace DLL, restart

**To recover from 500.30 without admin access:**
1. Stop IIS Application Pool
2. Navigate to `{webroot}\Plugins\Misc.Watermark\`
3. Replace or delete `Nop.Plugin.Misc.Watermark.dll`
4. Start IIS Application Pool

**To diagnose:** enable stdout logging in `web.config`:
```xml
<aspNetCore stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" ...>
```
Make sure the `logs/` folder exists. The log file contains the exact exception.

---

## Build setup

```bash
# Requires the full NopCommerce solution checked out 3 levels up
# ProjectReference path: ../../Presentation/Nop.Web.Framework/Nop.Web.Framework.csproj

dotnet build Nop.Plugin.Misc.Watermark.csproj -c Release

# Output goes to: ../../../Presentation/Nop.Web/Plugins/Misc.Watermark/
# Post-build NopTarget runs ClearPluginAssemblies.proj to strip redundant DLLs
# (disable this target when building outside the full solution)
```

### .NET SDK vs target framework
- Plugin targets `net9.0`
- .NET 9 SDK may not be available on Ubuntu 24.04 (only 8 and 10 via apt)
- Use .NET 10 SDK with `"rollForward": "latestMajor"` in `global.json`

---

## Version tagging convention

```
v{plugin-version}_nop{nop-version}
# e.g. v1.0.9_nop4.60, v1.1.0_nop4.90
```
