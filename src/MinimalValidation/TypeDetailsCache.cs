﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;

namespace MinimalValidationLib
{
    internal class TypeDetailsCache
    {
        private readonly ConcurrentDictionary<Type, PropertyDetails[]> _cache = new();

        public PropertyDetails[] Get(Type type)
        {
            if (!_cache.ContainsKey(type))
            {
                Visit(type);
            }

            return _cache[type];
        }

        private void Visit(Type type)
        {
            var visited = new HashSet<Type>();
            Visit(type, visited);
        }

        private void Visit(Type type, HashSet<Type> visited)
        {
            if (_cache.ContainsKey(type))
            {
                return;
            }

            if (!visited.Add(type))
            {
                return;
            }

            var propertiesToValidate = new List<PropertyDetails>();
            var hasPropertiesOfOwnType = false;
            var hasValidatableProperties = false;

            foreach (var property in type.GetProperties())
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    // Ignore indexer properties
                    continue;
                }

                var hasValidationOnProperty = property.GetCustomAttributes().OfType<ValidationAttribute>().Any();
                var enumerableType = GetEnumerableType(property.PropertyType);
                if (enumerableType != null)
                {
                    Visit(enumerableType, visited);
                }

                // Defer fully checking properties that are of the same type we're currently building the cache for.
                // We'll remove them at the end if any other validatable properties are present.
                if (type == property.PropertyType)
                {
                    propertiesToValidate.Add(new (property, hasValidationOnProperty, true, enumerableType));
                    hasPropertiesOfOwnType = true;
                    continue;
                }

                Visit(property.PropertyType, visited);
                var propertyTypeHasProperties = _cache.TryGetValue(property.PropertyType, out var properties) && properties.Length > 0;
                var enumerableTypeHasProperties = enumerableType != null
                    && _cache.TryGetValue(enumerableType, out var enumProperties)
                    && enumProperties.Length > 0;
                var recurse = enumerableTypeHasProperties || propertyTypeHasProperties;

                if (recurse || hasValidationOnProperty)
                {
                    propertiesToValidate.Add(new(property, hasValidationOnProperty, recurse, enumerableTypeHasProperties ? enumerableType : null));
                    hasValidatableProperties = true;
                }
            }

            if (hasPropertiesOfOwnType)
            {
                for (int i = propertiesToValidate.Count - 1; i >= 0; i--)
                {
                    var property = propertiesToValidate[i];
                    var enumerableTypeHasProperties = property.EnumerableType != null
                        && _cache.TryGetValue(property.EnumerableType, out var enumProperties)
                        && enumProperties.Length > 0;
                    var keepProperty = property.PropertyInfo.PropertyType != type || (hasValidatableProperties || enumerableTypeHasProperties);
                    if (!keepProperty)
                    {
                        propertiesToValidate.RemoveAt(i);
                    }
                }
            }

            _cache[type] = propertiesToValidate.ToArray();
        }

        private static Type? GetEnumerableType(Type type)
        {
            if (type.IsInterface && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return type.GetGenericArguments()[0];
            }

            foreach (Type intType in type.GetInterfaces())
            {
                if (intType.IsGenericType
                    && intType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return intType.GetGenericArguments()[0];
                }
            }

            return null;
        }
    }

    internal record PropertyDetails(PropertyInfo PropertyInfo, bool HasValidationAttribute, bool Recurse, Type? EnumerableType)
    {
        // TODO: Replace this with cached property getter (aka FastPropertyGetter)
        public object? GetValue(object target) => PropertyInfo.GetValue(target);
        public bool IsEnumerable => EnumerableType != null;
    }
}