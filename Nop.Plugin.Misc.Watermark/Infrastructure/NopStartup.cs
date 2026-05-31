using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Infrastructure;
using Nop.Plugin.Misc.Watermark.Services;
using Nop.Services.Media;

namespace Nop.Plugin.Misc.Watermark.Infrastructure
{
    public class NopStartup : INopStartup
    {
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // Replace the default IPictureService with our watermark-aware implementation.
            // IThumbService is resolved from the container, so it automatically works with
            // both local file storage and the Azure Blob plugin (Nop.Plugin.Misc.AzureBlob).
            services.AddScoped<IPictureService, MiscWatermarkPictureService>();
            services.AddScoped<FontProvider>();
        }

        public void Configure(IApplicationBuilder application)
        {
        }

        // High order value ensures this registration runs after nopCommerce core (order ~0)
        // and overrides the default IPictureService binding.
        public int Order => 3000;
    }
}
