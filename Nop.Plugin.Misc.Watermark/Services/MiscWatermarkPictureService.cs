using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

using Nop.Core;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Media;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Plugin.Misc.Watermark.Infrastructure;
using Nop.Services.Catalog;
using Nop.Services.Configuration;
using Nop.Services.Logging;
using Nop.Services.Media;
using Nop.Services.Plugins;
using Nop.Services.Seo;
using SkiaSharp;

namespace Nop.Plugin.Misc.Watermark.Services
{
    public class MiscWatermarkPictureService : PictureService, IDisposable
    {
        // _productPictureRepository, _fileProvider, _settingService, _mediaSettings, _thumbService
        // are inherited protected fields from PictureService — not redeclared here.
        private readonly IRepository<Category> _categoryRepository;
        private readonly IRepository<Manufacturer> _manufacturerRepository;
        private readonly IPluginService _pluginService;
        private readonly FontProvider _fontProvider;
        private readonly IStoreContext _storeContext;
        private readonly Lazy<Task<SKImage>> _watermarkImage;

        public MiscWatermarkPictureService(
            IRepository<Picture> pictureRepository,
            IRepository<Category> categoryRepository,
            IRepository<Manufacturer> manufacturerRepository,
            IRepository<ProductPicture> productPictureRepository,
            ISettingService settingService,
            IWebHelper webHelper,
            MediaSettings mediaSettings,
            IStoreContext storeContext,
            INopFileProvider fileProvider,
            IProductAttributeParser productAttributeParser,
            IProductAttributeService productAttributeService,
            IRepository<PictureBinary> pictureBinaryRepository,
            IUrlRecordService urlRecordService,
            IDownloadService downloadService,
            IHttpContextAccessor httpContextAccessor,
            ILogger logger,
            IPluginService pluginService,
            FontProvider fontProvider,
            IThumbService thumbService)
            : base(
                downloadService,
                httpContextAccessor,
                logger,
                fileProvider,
                productAttributeParser,
                productAttributeService,
                pictureRepository,
                pictureBinaryRepository,
                productPictureRepository,
                settingService,
                thumbService,
                urlRecordService,
                webHelper,
                mediaSettings)
        {
            _categoryRepository = categoryRepository;
            _manufacturerRepository = manufacturerRepository;
            _storeContext = storeContext;
            _pluginService = pluginService;
            _fontProvider = fontProvider;
            // _productPictureRepository, _settingService, _mediaSettings, _fileProvider, _thumbService
            // are assigned by the base PictureService constructor.

            _watermarkImage = new Lazy<Task<SKImage>>(async () =>
            {
                var watermarkPictureId = (await GetSettingsAsync()).PictureId;
                if (watermarkPictureId == 0)
                    return null;

                var picture = await base.GetPictureByIdAsync(watermarkPictureId);
                if (picture == null)
                    return null;
                var pictureBinary = await LoadPictureBinaryAsync(picture);
                return SKImage.FromEncodedData(pictureBinary);
            });
        }

        private async Task<bool> IsPluginInstalledAsync() =>
            (await _pluginService.GetPluginDescriptorBySystemNameAsync<IPlugin>("Misc.Watermark")) != null;

        public virtual Task DeleteThumbs()
        {
            foreach (var relPath in new[] { Path.Combine("images", "thumbs"), "thumbs" })
            {
                var dir = _fileProvider.GetAbsolutePath(relPath);
                if (!Directory.Exists(dir))
                    continue;
                foreach (var file in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
                {
                    try { file.Delete(); }
                    catch { /* skip locked or read-only files */ }
                }
            }
            return Task.CompletedTask;
        }

        public override async Task<(string Url, Picture Picture)> GetPictureUrlAsync(Picture picture,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            if (!await IsPluginInstalledAsync())
                return await base.GetPictureUrlAsync(picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType);

            if (picture == null)
                return showDefaultPicture
                    ? (await GetDefaultPictureUrlAsync(targetSize, defaultPictureType, storeLocation), null)
                    : (string.Empty, (Picture)null);

            byte[] pictureBinary = null;
            if (picture.IsNew)
            {
                await _thumbService.DeletePictureThumbsAsync(picture);
                pictureBinary = await LoadPictureBinaryAsync(picture);

                if ((pictureBinary?.Length ?? 0) == 0)
                    return showDefaultPicture
                        ? (await GetDefaultPictureUrlAsync(targetSize, defaultPictureType, storeLocation), picture)
                        : (string.Empty, picture);

                picture = await UpdatePictureAsync(picture.Id,
                    pictureBinary,
                    picture.MimeType,
                    picture.SeoFilename,
                    picture.AltAttribute,
                    picture.TitleAttribute,
                    false,
                    false);
            }

            var seoFileName = picture.SeoFilename;
            if (!string.IsNullOrEmpty(seoFileName))
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                seoFileName = new string(seoFileName.Where(c => !invalidChars.Contains(c)).ToArray());
            }
            var storeId = (await _storeContext.GetCurrentStoreAsync()).Id;
            var lastPart = await GetFileExtensionFromMimeTypeAsync(picture.MimeType);

            string thumbFileName;
            if (storeId == 1)
            {
                if (targetSize == 0 || picture.MimeType == MimeTypes.ImageSvg)
                    thumbFileName = !string.IsNullOrEmpty(seoFileName)
                        ? $"{picture.Id:0000000}_{seoFileName}.{lastPart}"
                        : $"{picture.Id:0000000}.{lastPart}";
                else
                    thumbFileName = !string.IsNullOrEmpty(seoFileName)
                        ? $"{picture.Id:0000000}_{seoFileName}_{targetSize}.{lastPart}"
                        : $"{picture.Id:0000000}_{targetSize}.{lastPart}";
            }
            else
            {
                if (targetSize == 0 || picture.MimeType == MimeTypes.ImageSvg)
                    thumbFileName = !string.IsNullOrEmpty(seoFileName)
                        ? $"{picture.Id:0000000}_{seoFileName}_{storeId}.{lastPart}"
                        : $"{picture.Id:0000000}_{storeId}.{lastPart}";
                else
                    thumbFileName = !string.IsNullOrEmpty(seoFileName)
                        ? $"{picture.Id:0000000}_{seoFileName}_{targetSize}_{storeId}.{lastPart}"
                        : $"{picture.Id:0000000}_{targetSize}_{storeId}.{lastPart}";
            }

            var thumbFilePath = await _thumbService.GetThumbLocalPathByFileNameAsync(thumbFileName);

            if (await _thumbService.GeneratedThumbExistsAsync(thumbFilePath, thumbFileName))
                return (await _thumbService.GetThumbUrlAsync(thumbFileName, storeLocation), picture);

            pictureBinary ??= await LoadPictureBinaryAsync(picture);

            // Named mutex prevents duplicate generation across threads without significant performance cost.
            // Async/await cannot be used inside a mutex-protected region — .Wait() is intentional here.
            using var mutex = new Mutex(false, thumbFileName);
            mutex.WaitOne();
            try
            {
                if (picture.MimeType != MimeTypes.ImageSvg)
                {
                    SKBitmap inputImage;
                    try
                    {
                        inputImage = SKBitmap.Decode(pictureBinary);
                    }
                    catch
                    {
                        inputImage = null;
                    }

                    if (inputImage == null)
                    {
                        // Unsupported or corrupt image — save original binary without watermark
                        try { _thumbService.SaveThumbAsync(thumbFilePath, thumbFileName, picture.MimeType, pictureBinary).Wait(); } catch { }
                        return (await _thumbService.GetThumbUrlAsync(thumbFileName, storeLocation), picture);
                    }

                    SKBitmap outputImage = inputImage;

                    if (targetSize != 0)
                        try
                        {
                            var newSize = ScaleRectangleToFitBounds(
                                new SKSizeI(targetSize, targetSize), inputImage.Info.Size);
                            outputImage = inputImage.Resize(newSize, SKFilterQuality.Medium);
                        }
                        catch
                        {
                            // ignored — use original size if resize fails
                        }

                    MakeImageWatermarkAsync(outputImage, picture.Id).Wait();

                    var format = GetImageFormatByMimeType(picture.MimeType);
                    pictureBinary = outputImage.Encode(format,
                        _mediaSettings.DefaultImageQuality > 0 ? _mediaSettings.DefaultImageQuality : 80).ToArray();

                    if (outputImage != inputImage)
                        inputImage.Dispose();
                    outputImage.Dispose();
                }

                try { _thumbService.SaveThumbAsync(thumbFilePath, thumbFileName, picture.MimeType, pictureBinary).Wait(); } catch { }
            }
            finally
            {
                mutex.ReleaseMutex();
            }

            return (await _thumbService.GetThumbUrlAsync(thumbFileName, storeLocation), picture);
        }

        private async Task MakeImageWatermarkAsync(SKBitmap sourceImage, int pictureId)
        {
            var currentSettings = await GetSettingsAsync();

            if (!currentSettings.WatermarkTextEnable && !currentSettings.WatermarkPictureEnable)
                return;

            var applyWatermark = IsWatermarkRequired(pictureId, currentSettings);

            if (!applyWatermark || ((sourceImage.Height <= currentSettings.MinimumImageHeightForWatermark) &&
                                    (sourceImage.Width <= currentSettings.MinimumImageWidthForWatermark)))
                return;

            if (currentSettings.WatermarkTextEnable && !string.IsNullOrEmpty(currentSettings.WatermarkText))
                PlaceTextWatermark(sourceImage, currentSettings);

            var watermarkImage = await _watermarkImage.Value;
            if (currentSettings.WatermarkPictureEnable && watermarkImage != null)
                PlaceImageWatermark(sourceImage, watermarkImage, currentSettings);
        }

        private static void PlaceImageWatermark(SKBitmap destImage, SKImage watermarkImage,
            WatermarkSettings currentSettings)
        {
            var watermarkSizeInPercent = (double)currentSettings.PictureSettings.Size / 100;
            var boundingBoxSize = new SKSizeI(
                (int)(destImage.Width * watermarkSizeInPercent),
                (int)(destImage.Height * watermarkSizeInPercent));
            var calculatedWatermarkSize =
                ScaleRectangleToFitBounds(boundingBoxSize, new SKSizeI(watermarkImage.Width, watermarkImage.Height));
            if (calculatedWatermarkSize.Width == 0 || calculatedWatermarkSize.Height == 0)
                return;

            var alpha = (byte)(currentSettings.PictureSettings.Opacity * 255);
            using var paint = new SKPaint
            {
                BlendMode = SKBlendMode.SrcOver,
                Color = SKColors.White.WithAlpha(alpha),
                FilterQuality = SKFilterQuality.High
            };

            using var canvas = new SKCanvas(destImage);
            foreach (var watermarkPosition in currentSettings.PictureSettings.PositionList.Select(position =>
                         CalculateWatermarkPosition(position, destImage.Info.Size, calculatedWatermarkSize)))
                canvas.DrawImage(watermarkImage, SKRectI.Create(watermarkPosition, calculatedWatermarkSize), paint);
        }

        private void PlaceTextWatermark(SKBitmap sourceBitmap, WatermarkSettings currentSettings)
        {
            var text = currentSettings.WatermarkText;
            var textAngle = currentSettings.TextRotatedDegree;
            var sizeFactor = (double)currentSettings.TextSettings.Size / 100;
            var maxTextSize = new SKSizeI(
                (int)(sourceBitmap.Width * sizeFactor),
                (int)(sourceBitmap.Height * sizeFactor));

            var color = SKColor.Parse(currentSettings.TextColor);
            color = color.WithAlpha((byte)(currentSettings.TextSettings.Opacity * 255));

            var typeface = GetFontTypeface(currentSettings);
            var fontSize = ComputeMaxFontSize(typeface, text, textAngle, maxTextSize, out var rotatedTextSize);

            using var paint = new SKPaint
            {
                Color = color,
                Typeface = typeface,
                TextSize = fontSize,
                TextAlign = SKTextAlign.Center,
                IsAntialias = true,
            };

            var horizontalTextRect = new SKRect();
            paint.MeasureText(text, ref horizontalTextRect);

            using var canvas = new SKCanvas(sourceBitmap);
            foreach (var textPosition in currentSettings.TextSettings.PositionList.Select(position =>
                         CalculateWatermarkPosition(position, sourceBitmap.Info.Size, rotatedTextSize)))
            {
                textPosition.Offset(rotatedTextSize.Width / 2, rotatedTextSize.Height / 2);
                canvas.Save();
                canvas.Translate(textPosition);
                canvas.RotateDegrees(textAngle);
                canvas.DrawText(text, 0, -horizontalTextRect.MidY, paint);
                canvas.Restore();
            }
        }

        private static int ComputeMaxFontSize(SKTypeface typeface, string text, int angle, SKSizeI bounds,
            out SKSizeI actualRotatedTextSize)
        {
            actualRotatedTextSize = new SKSizeI();
            using var paint = new SKPaint { Typeface = typeface };
            for (var fontSize = 2; ; fontSize++)
            {
                paint.TextSize = fontSize;
                var textRect = new SKRect();
                paint.MeasureText(text, ref textRect);
                var rotatedTextSize = CalculateRotatedRectSize(textRect.Size, angle);
                if ((rotatedTextSize.Width > bounds.Width) || (rotatedTextSize.Height > bounds.Height))
                    return fontSize - 1;

                actualRotatedTextSize = rotatedTextSize.ToSizeI();
            }
        }

        private bool IsWatermarkRequired(int pictureId, WatermarkSettings settings)
        {
            if (settings.ApplyOnProductPictures &&
                _productPictureRepository.Table.Any(p => p.PictureId == pictureId))
                return true;

            if (settings.ApplyOnCategoryPictures &&
                _categoryRepository.Table.Any(c => c.PictureId == pictureId))
                return true;

            return settings.ApplyOnManufacturerPictures &&
                   _manufacturerRepository.Table.Any(m => m.PictureId == pictureId);
        }

        private async Task<WatermarkSettings> GetSettingsAsync()
        {
            var currentStore = await _storeContext.GetCurrentStoreAsync();
            return await _settingService.LoadSettingAsync<WatermarkSettings>(currentStore.Id);
        }

        private static SKSizeI ScaleRectangleToFitBounds(SKSizeI bounds, SKSizeI rect)
        {
            if (rect.Width < bounds.Width && rect.Height < bounds.Height)
                return rect;

            if (bounds.Width == 0 || bounds.Height == 0)
                return new SKSizeI(0, 0);

            var scaleFactorWidth = (double)rect.Width / bounds.Width;
            var scaleFactorHeight = (double)rect.Height / bounds.Height;
            var scaleFactor = Math.Max(scaleFactorWidth, scaleFactorHeight);
            return new SKSizeI
            {
                Width = (int)(rect.Width / scaleFactor),
                Height = (int)(rect.Height / scaleFactor)
            };
        }

        private static SKSize CalculateRotatedRectSize(SKSize rectSize, double angleDeg)
        {
            var angleRad = angleDeg * Math.PI / 180;
            var width = rectSize.Height * Math.Abs(Math.Sin(angleRad)) +
                        rectSize.Width * Math.Abs(Math.Cos(angleRad));
            var height = rectSize.Height * Math.Abs(Math.Cos(angleRad)) +
                         rectSize.Width * Math.Abs(Math.Sin(angleRad));
            return new SKSize((float)width, (float)height);
        }

        private static SKPointI CalculateWatermarkPosition(WatermarkPosition watermarkPosition, SKSizeI imageSize,
            SKSizeI watermarkSize)
        {
            return watermarkPosition switch
            {
                WatermarkPosition.TopLeftCorner => new SKPointI(0, 0),
                WatermarkPosition.TopCenter => new SKPointI(
                    (imageSize.Width / 2) - (watermarkSize.Width / 2), 0),
                WatermarkPosition.TopRightCorner => new SKPointI(
                    imageSize.Width - watermarkSize.Width, 0),
                WatermarkPosition.CenterLeft => new SKPointI(
                    0, (imageSize.Height / 2) - (watermarkSize.Height / 2)),
                WatermarkPosition.Center => new SKPointI(
                    (imageSize.Width / 2) - (watermarkSize.Width / 2),
                    (imageSize.Height / 2) - (watermarkSize.Height / 2)),
                WatermarkPosition.CenterRight => new SKPointI(
                    imageSize.Width - watermarkSize.Width,
                    (imageSize.Height / 2) - (watermarkSize.Height / 2)),
                WatermarkPosition.BottomLeftCorner => new SKPointI(
                    0, imageSize.Height - watermarkSize.Height),
                WatermarkPosition.BottomCenter => new SKPointI(
                    (imageSize.Width / 2) - (watermarkSize.Width / 2),
                    imageSize.Height - watermarkSize.Height),
                WatermarkPosition.BottomRightCorner => new SKPointI(
                    imageSize.Width - watermarkSize.Width,
                    imageSize.Height - watermarkSize.Height),
                _ => new SKPointI(0, 0)
            };
        }

        private SKTypeface GetFontTypeface(WatermarkSettings settings)
        {
            var typeface = _fontProvider.GetTypeface(settings.WatermarkFont);
            if (typeface != null)
                return typeface;

            return _fontProvider.AvailableFonts.Any()
                ? _fontProvider.GetTypeface(_fontProvider.AvailableFonts.First())
                : throw new InvalidOperationException("No fonts available for watermark text rendering");
        }

        #region IDisposable

        private void ReleaseUnmanagedResources()
        {
            if (_watermarkImage.IsValueCreated && _watermarkImage.Value.IsCompletedSuccessfully)
                _watermarkImage.Value.Result?.Dispose();
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~MiscWatermarkPictureService()
        {
            ReleaseUnmanagedResources();
        }

        #endregion
    }
}
