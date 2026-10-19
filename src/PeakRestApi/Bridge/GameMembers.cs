using System;
using System.Linq;
using System.Reflection;

namespace PeakRestApi.Bridge;

/// <summary>
/// Late bound access to a member of a game type.
/// </summary>
/// <remarks>
/// PEAK's <c>Assembly-CSharp</c> and Photon assemblies are proprietary and
/// cannot be referenced from a redistributable build, so the game facing
/// layer resolves members at runtime instead. Every lookup is cached and
/// every miss is reported instead of throwing, which is what allows
/// PEAKapi to report an honest, granular capability instead of guessing.
/// </remarks>
internal static class GameMembers
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Reads a static property or field. Returns null when the member does not exist.
    /// </summary>
    public static object? GetStatic(Type type, string name)
    {
        if (type == null)
        {
            return null;
        }

        try
        {
            var property = type.GetProperty(name, All);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null);
            }

            var field = type.GetField(name, All);
            return field?.GetValue(null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an instance property or field. Returns null when the member does not exist.
    /// </summary>
    public static object? Get(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        var type = instance.GetType();
        try
        {
            var property = type.GetProperty(name, All);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            var field = type.GetField(name, All);
            return field?.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a method by name and argument count.
    /// </summary>
    /// <remarks>
    /// Optional parameters are supported: a method with optional trailing
    /// parameters matches when the supplied argument count fits, and the
    /// missing arguments are filled in with their declared defaults.
    /// </remarks>
    public static MethodInfo? FindMethod(Type? type, string name, int argumentCount)
    {
        if (type == null)
        {
            return null;
        }

        try
        {
            var candidates = type.GetMethods(All)
                .Where(m => m.Name == name && m.GetParameters().Length >= argumentCount)
                .Where(m => m.GetParameters().Count(p => !p.IsOptional) <= argumentCount)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer the overload that needs the fewest default values.
            candidates.Sort((a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
            return candidates[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Pads the argument list with the declared defaults of the optional
    /// parameters of <paramref name="method"/>.
    /// </summary>
    public static object?[] FitArguments(MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == arguments.Length)
        {
            return arguments;
        }

        var fitted = new object?[parameters.Length];
        Array.Copy(arguments, fitted, arguments.Length);

        for (var i = arguments.Length; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            fitted[i] = parameter.HasDefaultValue ? parameter.DefaultValue : GetDefault(parameter.ParameterType);
        }

        return fitted;
    }

    private static object? GetDefault(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;

    /// <summary>
    /// Invokes a method and returns the result, or null when the call failed.
    /// The exception is reported through <paramref name="failure"/> so callers
    /// can turn it into a truthful error instead of a crash.
    /// </summary>
    public static object? Invoke(
        object? instance,
        Type? type,
        string name,
        object?[] arguments,
        out Exception? failure)
    {
        failure = null;
        var method = FindMethod(type ?? instance?.GetType(), name, arguments?.Length ?? 0);
        if (method == null)
        {
            failure = new MissingMethodException(type?.FullName ?? "?", name);
            return null;
        }

        try
        {
            return method.Invoke(instance, FitArguments(method, arguments));
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            failure = e.InnerException;
            return null;
        }
        catch (Exception e)
        {
            failure = e;
            return null;
        }
    }

    /// <summary>
    /// Reads a boolean member, defaulting to false when it is missing.
    /// </summary>
    public static bool GetBool(object? instance, string name) =>
        Get(instance, name) is bool value && value;

    /// <summary>
    /// Reads a string member, defaulting to an empty string when it is missing.
    /// </summary>
    public static string GetString(object? instance, string name) =>
        Get(instance, name) as string ?? string.Empty;

    /// <summary>
    /// Casts a value to <typeparamref name="T"/> when possible.
    /// </summary>
    public static T? As<T>(object? value) where T : class =>
        value as T;
}
