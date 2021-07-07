﻿using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Smartstore.Core;
using Smartstore.Net;
using Smartstore.Threading;
using Smartstore.Web.Bundling.Processors;
using Smartstore.Web.Theming;

namespace Smartstore.Web.Bundling
{
    internal class BundleMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IBundleCollection _collection;
        private readonly IBundleCache _bundleCache;
        private readonly IBundleBuilder _bundleBuilder;
        private readonly IOptionsMonitor<BundlingOptions> _optionsMonitor;
        private readonly ILogger _logger;

        public BundleMiddleware(
            RequestDelegate next,
            IBundleCollection collection,
            IBundleCache bundleCache,
            IBundleBuilder bundleBuilder,
            IOptionsMonitor<BundlingOptions> optionsMonitor,
            ILogger<BundleMiddleware> logger)
        {
            _next = next;
            _collection = collection;
            _bundleCache = bundleCache;
            _bundleBuilder = bundleBuilder;
            _optionsMonitor = optionsMonitor;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext httpContext, IWorkContext workContext)
        {
            var bundle = _collection.GetBundleFor(httpContext.Request.Path);
            if (bundle == null)
            {
                await _next(httpContext);
                return;
            }

            _logger.Debug("Request for bundle '{0}' started.", bundle.Route);

            var cacheKey = bundle.GetCacheKey(httpContext);
            var options = _optionsMonitor.CurrentValue;

            if (cacheKey.IsValidationMode() && workContext.CurrentCustomer.IsAdmin())
            {
                await HandleValidation(cacheKey, bundle, httpContext, options);
                return;
            }

            var bundleResponse = await _bundleCache.GetResponseAsync(cacheKey, bundle);
            if (bundleResponse != null)
            {
                _logger.Debug("Serving bundle '{0}' from cache.", bundle.Route);
                await ServeBundleResponse(bundleResponse, httpContext, options);
                return;
            }
            
            using (await AsyncLock.KeyedAsync("bm_" + cacheKey))
            {
                // Double paranoia check
                bundleResponse = await _bundleCache.GetResponseAsync(cacheKey, bundle);

                if (bundleResponse != null)
                {
                    await ServeBundleResponse(bundleResponse, httpContext, options);
                    return;
                }

                try
                {
                    // Build
                    var watch = Stopwatch.StartNew();
                    bundleResponse = await _bundleBuilder.BuildBundleAsync(bundle, cacheKey, httpContext, options);
                    watch.Stop();
                    Debug.WriteLine($"Bundle build time for {bundle.Route}: {watch.ElapsedMilliseconds} ms.");

                    if (bundleResponse == null)
                    {
                        await _next(httpContext);
                        return;
                    }

                    // Put to cache
                    await _bundleCache.PutResponseAsync(cacheKey, bundle, bundleResponse);

                    // Serve
                    await ServeBundleResponse(bundleResponse, httpContext, options);
                }
                catch (Exception ex)
                {
                    await ServerErrorResponse(ex, bundle, httpContext);
                    _logger.Error(ex, $"Error while processing bundle '{bundle.Route}'.");
                }
            }
        }

        /// <summary>
        /// The validation mode bypasses cache and always compiles Sass files to ensure validity.
        /// </summary>
        private async ValueTask HandleValidation(BundleCacheKey cacheKey, Bundle bundle, HttpContext httpContext, BundlingOptions options)
        {
            try
            {
                var clone = new Bundle(bundle);
                clone.Processors.Clear();
                clone.Processors.Add(SassProcessor.Instance);

                var bundleResponse = await _bundleBuilder.BuildBundleAsync(clone, cacheKey, httpContext, options);
                await ServeBundleResponse(bundleResponse, httpContext, options, true);
            }
            catch (Exception ex)
            {
                await ServerErrorResponse(ex, bundle, httpContext);
            }
        }

        private static ValueTask ServeBundleResponse(BundleResponse bundleResponse, HttpContext httpContext, BundlingOptions options, bool noCache = false)
        {
            var response = httpContext.Response;
            var contentHash = bundleResponse.ContentHash;

            response.ContentType = bundleResponse.ContentType;

            if (!noCache)
            {
                if (options.EnableClientCache == true)
                {
                    response.Headers[HeaderNames.CacheControl] = "max-age=31536000"; // 1 year

                    if (httpContext.Request.Query.ContainsKey("v"))
                    {
                        response.Headers[HeaderNames.CacheControl] += ",immutable";
                    }
                }

                if (contentHash.HasValue())
                {
                    response.Headers[HeaderNames.ETag] = $"\"{contentHash}\"";

                    if (IsConditionalGet(httpContext, contentHash))
                    {
                        response.StatusCode = 304;
                        return ValueTask.CompletedTask;
                    }
                }
            }

            if (bundleResponse.Content?.Length > 0)
            {
                SetCompressionMode(httpContext, options);
                var buffer = Encoding.UTF8.GetBytes(bundleResponse.Content);
                return response.Body.WriteAsync(buffer.AsMemory(0, buffer.Length));
            }

            return ValueTask.CompletedTask;
        }

        private static ValueTask ServerErrorResponse(Exception ex, Bundle bundle, HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = bundle.ContentType;
            response.StatusCode = 500;

            var content = $"/*\n{ex.ToAllMessages()}\n*/";
            var buffer = Encoding.UTF8.GetBytes(content);
            return response.Body.WriteAsync(buffer.AsMemory(0, buffer.Length));
        }

        private static bool IsConditionalGet(HttpContext context, string contentHash)
        {
            var headers = context.Request.Headers;

            if (headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatch))
            {
                return contentHash == ifNoneMatch.ToString().Trim('"');
            }

            if (headers.TryGetValue(HeaderNames.IfModifiedSince, out var ifModifiedSince))
            {
                if (context.Response.Headers.TryGetValue(HeaderNames.LastModified, out var lastModified))
                {
                    return ifModifiedSince == lastModified;
                }
            }

            return false;
        }

        private static void SetCompressionMode(HttpContext context, BundlingOptions options)
        {
            // Only called when we expect to serve the body.
            var responseCompressionFeature = context.Features.Get<IHttpsCompressionFeature>();
            if (responseCompressionFeature != null)
            {
                responseCompressionFeature.Mode = options.HttpsCompression;
            }
        }
    }
}
