﻿using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace ProbabilisticDataStructures.Serialization
{
    public static class ExpressionExtensions<T>
    {
        private static readonly ConcurrentDictionary<Type, Func<T, object>> Cached =
            new ConcurrentDictionary<Type, Func<T, object>>();

        private static readonly ConcurrentDictionary<Type, Func<object, T>> Cached2 =
            new ConcurrentDictionary<Type, Func<object, T>>();

        private static readonly ConcurrentDictionary<Type, Func<T>> CachedInstanceFunc =
            new ConcurrentDictionary<Type, Func<T>>();

        public static Func<T, object> GetCastDelegate(Type to)
            => Cached.GetOrAdd(to, MakeCastDelegateTo);

        public static Func<object, T> GetCastDelegate2(Type to)
            => Cached2.GetOrAdd(to, MakeCastDelegateFrom);
        
        public static Func<T> GetInstanceDelegate(Type typeToCreate)
            => CachedInstanceFunc.GetOrAdd(typeToCreate, CreateInstance);

        private static Func<T> CreateInstance(Type type)
        {
            return Expression.Lambda<Func<T>>(
                Expression.Convert(Expression.New(type), typeof(T))).Compile();
        }

        private static Func<T, object> MakeCastDelegateTo(Type to)
        {
            var p = Expression.Parameter(typeof(T));

            return Expression.Lambda<Func<T, object>>(
                Expression.Convert(Expression.Convert(p, to), typeof(object)),
                p).Compile();
        }

        private static Func<object, T> MakeCastDelegateFrom(Type to)
        {
            var p = Expression.Parameter(typeof(object));

            return Expression.Lambda<Func<object, T>>(
                Expression.Convert(Expression.Convert(p, to), typeof(T)),
                p).Compile();
        }
    }
}