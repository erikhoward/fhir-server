// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.Core.Features.Search.Access;

public class ExpressionAccessControl
{
    private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;

    public ExpressionAccessControl(RequestContextAccessor<IFhirRequestContext> requestContextAccessor)
    {
        _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
    }

    public void CheckAndRaiseAccessExceptions(Expression expression)
    {
        if (expression == null)
        {
            return;
        }

        if (_requestContextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true)
        {
            if (ExtractIncludeAndChainedExpressions(
                    expression,
                    out IReadOnlyList<IncludeExpression> includeExpressions,
                    out IReadOnlyList<IncludeExpression> revIncludeExpressions,
                    out IReadOnlyList<ChainedExpression> chainedExpressions))
            {
                var validResourceTypes = _requestContextAccessor.RequestContext?.AccessControlContext.AllowedResourceActions.Select(r => r.Resource).ToHashSet();

                // check resource type restrictions from SMART clinical scopes
                foreach (var type in chainedExpressions.SelectMany(x => x.ResourceTypes))
                {
                    if (!ResourceTypeAllowedByClinicalScopes(validResourceTypes, type))
                    {
                        throw new InvalidSearchOperationException(Core.Resources.ChainedResourceTypesNotAllowedDueToScope);
                    }
                }

                IEnumerable<string> typesToCheck = includeExpressions
                    .SelectMany(x => x.ResourceTypes)
                    .Concat(revIncludeExpressions.SelectMany(x => x.ResourceTypes));

                foreach (var type in typesToCheck)
                {
                    if (!ResourceTypeAllowedByClinicalScopes(validResourceTypes, type))
                    {
                        throw new InvalidSearchOperationException(string.Format(Core.Resources.ResourceTypeNotAllowedRestrictedByClinicalScopes, type));
                    }
                }
            }
        }
    }

    private static bool ResourceTypeAllowedByClinicalScopes(ICollection<string> validResourceTypes, string resourceType)
    {
        if (validResourceTypes != null && (validResourceTypes.Contains("*") || validResourceTypes.Contains(resourceType)))
        {
            return true;
        }

        return false;
    }

    private static bool ExtractIncludeAndChainedExpressions(
            Expression inputExpression,
            out IReadOnlyList<IncludeExpression> includeExpressions,
            out IReadOnlyList<IncludeExpression> revIncludeExpressions,
            out IReadOnlyList<ChainedExpression> chainedExpressions)
        {
            switch (inputExpression)
            {
                case IncludeExpression ie when ie.Reversed:
                    includeExpressions = Array.Empty<IncludeExpression>();
                    revIncludeExpressions = new[] { ie };
                    chainedExpressions = Array.Empty<ChainedExpression>();
                    return true;
                case IncludeExpression ie:
                    includeExpressions = new[] { ie };
                    revIncludeExpressions = Array.Empty<IncludeExpression>();
                    chainedExpressions = Array.Empty<ChainedExpression>();
                    return true;
                case ChainedExpression ie:
                    includeExpressions = Array.Empty<IncludeExpression>();
                    revIncludeExpressions = Array.Empty<IncludeExpression>();
                    chainedExpressions = new[] { ie };
                    return true;
                case MultiaryExpression me when me.Expressions.Any(e => e is IncludeExpression || e is ChainedExpression):
                    includeExpressions = me.Expressions.OfType<IncludeExpression>().Where(ie => !ie.Reversed).ToList();
                    revIncludeExpressions = me.Expressions.OfType<IncludeExpression>().Where(ie => ie.Reversed).ToList();
                    chainedExpressions = me.Expressions.OfType<ChainedExpression>().ToList();
                    return true;
                default:
                    includeExpressions = Array.Empty<IncludeExpression>();
                    revIncludeExpressions = Array.Empty<IncludeExpression>();
                    chainedExpressions = Array.Empty<ChainedExpression>();
                    return false;
            }
        }
}
