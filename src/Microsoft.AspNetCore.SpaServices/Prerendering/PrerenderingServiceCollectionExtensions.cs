﻿using Microsoft.AspNetCore.SpaServices.Prerendering;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extension methods for setting up prerendering features in an <see cref="IServiceCollection" />.
    /// </summary>
    public static class PrerenderingServiceCollectionExtensions
    {
        /// <summary>
        /// Configures the dependency injection system to supply an implementation
        /// of <see cref="ISpaPrerenderer"/>.
        /// </summary>
        /// <param name="serviceCollection">The <see cref="IServiceCollection"/>.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddSpaPrerenderer(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddHttpContextAccessor();
            serviceCollection.AddSingleton<ISpaPrerenderer, DefaultSpaPrerenderer>();

            return serviceCollection;
        }
    }
}
