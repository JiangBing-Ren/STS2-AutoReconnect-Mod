using System.Reflection;

namespace AutoReconnect.Scripts;

/// <summary>
/// Utility for finding types and members by name without hard assembly references.
/// Pattern used extensively in CDC's CharacterDetector.
/// </summary>
internal static class ReflectionHelper
{
    /// <summary>
    /// Find a type by full name across all loaded assemblies.
    /// </summary>
    public static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullName);
            if (type != null) return type;
        }
        return null;
    }

    /// <summary>
    /// Find a type by simple name suffix match.
    /// </summary>
    public static Type? FindTypeBySuffix(string suffix)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.FullName?.EndsWith(suffix) == true)
                    return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Get a property value by trying multiple possible names.
    /// </summary>
    public static object? GetPropertyValue(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null)
                return prop.GetValue(obj);
        }
        return null;
    }

    /// <summary>
    /// Get a field value by trying multiple possible names.
    /// </summary>
    public static object? GetFieldValue(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);
        }
        return null;
    }

    /// <summary>
    /// Invoke a method by trying multiple possible names.
    /// </summary>
    public static object? InvokeMethod(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
        object?[]? args = null)
    {
        args ??= Array.Empty<object?>();
        var type = obj.GetType();
        foreach (var name in names)
        {
            var method = type.GetMethod(name, flags);
            if (method != null)
                return method.Invoke(obj, args);
        }
        return null;
    }
}
