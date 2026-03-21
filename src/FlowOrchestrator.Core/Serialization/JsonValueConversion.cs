using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Serialization;

internal static class JsonValueConversion
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static T? Deserialize<T>(object? value, JsonSerializerOptions? options = null)
    {
        var converted = Deserialize(value, typeof(T), options);
        return converted is null ? default : (T)converted;
    }

    public static bool TryDeserialize<T>(object? value, out T? result, JsonSerializerOptions? options = null)
    {
        try
        {
            result = Deserialize<T>(value, options);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public static object? Deserialize(object? value, Type targetType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        var serializerOptions = options ?? _webOptions;
        var underlyingNullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveTargetType = underlyingNullableType ?? targetType;

        if (value is null)
        {
            return GetDefaultValue(targetType);
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return GetDefaultValue(targetType);
            }
        }

        if (value is IDictionary<string, object?> dictionary
            && TryDeserializeFromDictionary(dictionary, targetType, serializerOptions, out var dictionaryResult))
        {
            return dictionaryResult;
        }

        if (TryConvertPrimitive(value, effectiveTargetType, serializerOptions, out var primitiveResult))
        {
            return WrapNullable(targetType, underlyingNullableType, primitiveResult);
        }

        if (value is JsonElement jsonElement)
        {
            return jsonElement.Deserialize(targetType, serializerOptions);
        }

        var json = JsonSerializer.SerializeToElement(value, value.GetType(), serializerOptions);
        return json.Deserialize(targetType, serializerOptions);
    }

    private static object? GetDefaultValue(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static object? WrapNullable(Type targetType, Type? nullableUnderlyingType, object? value)
    {
        if (nullableUnderlyingType is null || value is null)
        {
            return value;
        }

        return Activator.CreateInstance(targetType, value);
    }

    private static bool TryDeserializeFromDictionary(
        IDictionary<string, object?> source,
        Type targetType,
        JsonSerializerOptions options,
        out object? result)
    {
        result = null;
        if (!CanBindFromDictionary(targetType))
        {
            return false;
        }

        object? instance;
        try
        {
            instance = Activator.CreateInstance(targetType);
        }
        catch
        {
            return false;
        }

        if (instance is null)
        {
            return false;
        }

        var lookup = new Dictionary<string, object?>(source, StringComparer.OrdinalIgnoreCase);
        var properties = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite);

        foreach (var property in properties)
        {
            if (!TryGetPropertyValue(lookup, property, options, out var raw))
            {
                continue;
            }

            var converted = Deserialize(raw, property.PropertyType, options);
            property.SetValue(instance, converted);
        }

        result = instance;
        return true;
    }

    private static bool CanBindFromDictionary(Type targetType)
    {
        if (targetType == typeof(object) || targetType == typeof(string))
        {
            return false;
        }

        if (targetType.IsPrimitive || targetType.IsEnum)
        {
            return false;
        }

        if (typeof(IDictionary).IsAssignableFrom(targetType))
        {
            return false;
        }

        if (typeof(IEnumerable).IsAssignableFrom(targetType) && targetType != typeof(byte[]))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetPropertyValue(
        IReadOnlyDictionary<string, object?> lookup,
        PropertyInfo property,
        JsonSerializerOptions options,
        out object? value)
    {
        value = null;
        foreach (var candidateName in GetCandidateNames(property, options))
        {
            if (lookup.TryGetValue(candidateName, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetCandidateNames(PropertyInfo property, JsonSerializerOptions options)
    {
        yield return property.Name;

        if (options.PropertyNamingPolicy is not null)
        {
            yield return options.PropertyNamingPolicy.ConvertName(property.Name);
        }

        var jsonPropertyName = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonPropertyName is { Name.Length: > 0 })
        {
            yield return jsonPropertyName.Name;
        }
    }

    private static bool TryConvertPrimitive(object value, Type targetType, JsonSerializerOptions options, out object? result)
    {
        result = null;

        if (targetType == typeof(object))
        {
            result = value;
            return true;
        }

        if (value is JsonElement element)
        {
            return TryConvertFromJsonElement(element, targetType, options, out result);
        }

        if (targetType.IsEnum)
        {
            if (value is string enumText && Enum.TryParse(targetType, enumText, true, out var enumValue))
            {
                result = enumValue;
                return true;
            }

            if (IsNumericType(value.GetType()))
            {
                result = Enum.ToObject(targetType, value);
                return true;
            }
        }

        if (value is string text)
        {
            return TryConvertFromString(text, targetType, out result);
        }

        if (targetType == typeof(string))
        {
            result = value switch
            {
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
            return true;
        }

        if (value is IConvertible && IsConvertibleType(targetType))
        {
            result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool TryConvertFromJsonElement(
        JsonElement element,
        Type targetType,
        JsonSerializerOptions options,
        out object? result)
    {
        result = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                result = GetDefaultValue(targetType);
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (targetType == typeof(bool))
                {
                    result = element.GetBoolean();
                    return true;
                }
                return TryConvertFromString(element.GetBoolean().ToString(CultureInfo.InvariantCulture), targetType, out result);
            case JsonValueKind.Number:
                return TryConvertFromString(element.GetRawText(), targetType, out result);
            case JsonValueKind.String:
                return TryConvertFromString(element.GetString() ?? string.Empty, targetType, out result);
            case JsonValueKind.Array:
            case JsonValueKind.Object:
                if (targetType == typeof(string))
                {
                    result = element.GetRawText();
                    return true;
                }
                result = element.Deserialize(targetType, options);
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertFromString(string text, Type targetType, out object? result)
    {
        result = null;

        if (targetType == typeof(string))
        {
            result = text;
            return true;
        }

        if (targetType == typeof(bool))
        {
            if (bool.TryParse(text, out var parsedBool))
            {
                result = parsedBool;
                return true;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boolAsNumber))
            {
                result = boolAsNumber != 0;
                return true;
            }

            return false;
        }

        if (targetType == typeof(Guid))
        {
            if (Guid.TryParse(text, out var guid))
            {
                result = guid;
                return true;
            }

            return false;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                result = dto;
                return true;
            }

            return false;
        }

        if (targetType == typeof(DateTime))
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
            {
                result = dateTime;
                return true;
            }

            return false;
        }

        if (targetType == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan))
            {
                result = timeSpan;
                return true;
            }

            return false;
        }

        if (targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, text, true, out var enumValue))
            {
                result = enumValue;
                return true;
            }

            return false;
        }

        var typeCode = Type.GetTypeCode(targetType);
        try
        {
            result = typeCode switch
            {
                TypeCode.Byte => byte.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.SByte => sbyte.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Int16 => short.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.UInt16 => ushort.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Int32 => int.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.UInt32 => uint.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Int64 => long.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.UInt64 => ulong.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Single => float.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Double => double.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                TypeCode.Decimal => decimal.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture),
                _ => null
            };

            return result is not null;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private static bool IsNumericType(Type type)
    {
        var typeCode = Type.GetTypeCode(type);
        return typeCode is TypeCode.Byte
            or TypeCode.SByte
            or TypeCode.Int16
            or TypeCode.UInt16
            or TypeCode.Int32
            or TypeCode.UInt32
            or TypeCode.Int64
            or TypeCode.UInt64
            or TypeCode.Single
            or TypeCode.Double
            or TypeCode.Decimal;
    }

    private static bool IsConvertibleType(Type type)
    {
        var typeCode = Type.GetTypeCode(type);
        return typeCode is not TypeCode.Object;
    }
}
