/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Listenarr.Tests.Common;

/// <summary>
/// Wraps a real implementation of <typeparamref name="T"/> and forwards every call to it, except
/// calls to the method named <c>methodName</c>, which fail with <c>failure</c> (a faulted task for
/// an async method). Lets a test fail one query of a real repository whose members are not virtual.
/// </summary>
public class FailingCallProxy<T> : DispatchProxy
    where T : class
{
    private T _inner = null!;
    private string _methodName = string.Empty;
    private Exception _failure = null!;

    public static T Create(T inner, string methodName, Exception failure)
    {
        var proxy = DispatchProxy.Create<T, FailingCallProxy<T>>();
        var state = (FailingCallProxy<T>)(object)proxy;
        state._inner = inner;
        state._methodName = methodName;
        state._failure = failure;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name != _methodName)
        {
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        if (targetMethod.ReturnType == typeof(Task))
        {
            return Task.FromException(_failure);
        }

        if (targetMethod.ReturnType.IsGenericType
            && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return typeof(Task)
                .GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                .MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0])
                .Invoke(null, [_failure]);
        }

        throw _failure;
    }
}
