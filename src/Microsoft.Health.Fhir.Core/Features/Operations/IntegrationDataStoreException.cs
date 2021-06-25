﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Net;
using EnsureThat;

namespace Microsoft.Health.Fhir.Core.Features.Operations
{
    public class IntegrationDataStoreException : Exception
    {
        public IntegrationDataStoreException(string message, HttpStatusCode statusCode)
            : base(message)
        {
            EnsureArg.IsNotNullOrWhiteSpace(message, nameof(message));

            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}