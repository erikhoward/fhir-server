﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Specialized;
using System.Net;
using Microsoft.AzureHealth.DataServices.Bindings;
using Microsoft.AzureHealth.DataServices.Clients;
using Microsoft.AzureHealth.DataServices.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SMARTProxy.Bindings
{
    public class AzureActiveDirectoryBinding : IBinding
    {
        private readonly ILogger _logger;

        private readonly IOptions<AzureActiveDirectoryBindingOptions> _options;

        public AzureActiveDirectoryBinding(ILogger<RestBinding> logger, IOptions<AzureActiveDirectoryBindingOptions> options)
        {
            _logger = logger;
            Id = Guid.NewGuid().ToString();
            _options = options;
        }

        /// <summary>
        /// An event that signals an error in the binding.
        /// </summary>
        public event EventHandler<BindingErrorEventArgs>? OnError;

        /// <summary>
        /// An event that signals the binding has completed.
        /// </summary>
        public event EventHandler<BindingCompleteEventArgs>? OnComplete;

        /// <summary>
        /// Gets the name of the binding "AzureActiveDirectoryBinding".
        /// </summary>
        public string Name => nameof(AzureActiveDirectoryBinding);

        /// <summary>
        /// Gets a unique ID of the binding instance.
        /// </summary>
        public string Id { get; internal set; }

        /// <summary>
        /// Executes the binding.
        /// </summary>
        /// <param name="context">Operation context.</param>
        /// <returns>Operation <paramref name="context"/>.</returns>
        public async Task<OperationContext> ExecuteAsync(OperationContext context)
        {
            _logger?.LogInformation("{Name}-{Id} binding received.", Name, Id);

            if (context == null)
            {
                OnError?.Invoke(this, new BindingErrorEventArgs(Id, Name, new ArgumentNullException(nameof(context))));
                return context!;
            }

            if (context.StatusCode == HttpStatusCode.Redirect)
            {
                _logger?.LogTrace("Context is set to redirect. Skipping AAD binding.");
                return context;
            }

            try
            {
                // NameValueCollection headers = context.Request.GetHeaders(false);

                NameValueCollection headers = context.Headers.RequestAppendAndReplace(context.Request, false);
                headers["User-Agent"] = "Azure Health Data Services Toolkit SMART Sample";
                var contentType = context.Request.Content!.Headers.ContentType!.ToString();

                string method = context.Request.Method.ToString();
                string serverUrl = _options.Value.AzureActiveDirectoryEndpoint!;
                string localPath = context.Request.RequestUri!.LocalPath;
                string query = context.Request.RequestUri!.Query;
                string? token = null;

                // TODO - remove before merging or remove client secret
                _logger.LogTrace("Sending AAD request to {ServerUrl}{LocalPath}{Query} with body {Body}", serverUrl, localPath, query, context.Request.Content.ToString());

                byte[]? content = (context.Request.Content != null) ? (await context.Request.Content!.ReadAsByteArrayAsync()) : null;
                HttpResponseMessage httpResponseMessage = await new RestRequest(new RestRequestBuilder(method, serverUrl, token, localPath, query, headers, content, contentType)).SendAsync();

                var responseContent = await httpResponseMessage.Content.ReadAsStringAsync();

                context.StatusCode = httpResponseMessage.StatusCode;
                context.ContentString = responseContent;

                // TODO - remove before merging or remove client secret
                _logger?.LogTrace("AAD responded with code {StatusCode} and Body {Body}.", context.StatusCode, context.ContentString);

                if ((int)context.StatusCode >= 400)
                {
                    context.IsFatal = true;
                    _logger.LogWarning("Invalid response from AzureActiveDirectory Binding. {Code}, {Body}", context.StatusCode, context.ContentString);
                }

                // context.Content = await (httpResponseMessage.Content?.ReadAsByteArrayAsync());

                OnComplete?.Invoke(this, new BindingCompleteEventArgs(Id, Name, context));
                _logger?.LogInformation("{Name}-{Id} completed.", Name, Id);
                return context;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{Name}-{Id} fault with server request.", Name, Id);
                context.IsFatal = true;
                context.Error = ex;
                context.Content = null;
                OnError?.Invoke(this, new BindingErrorEventArgs(Id, Name, ex));
                _logger?.LogInformation("{Name}-{Id} signaled error.", Name, Id);
                return context;
            }
        }
    }
}
