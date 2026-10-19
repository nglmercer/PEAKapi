using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PeakRestApi.Bridge;

/// <summary>
/// Finds the components and prefabs that the running level contains.
/// </summary>
/// <remarks>
/// The game spawns its hostile entities from components that are placed in
/// the level and that carry the prefab as a serialized field, for example
/// <c>MushroomZombieSpawner.mushroomZombiePrefab</c>. Reading those fields is
/// how PEAKapi learns the exact prefab name that is registered with Photon,
/// without guessing.
/// </remarks>
internal static class SceneComponents
{
    /// <summary>
    /// All active or inactive components of the given type in the loaded scene.
    /// </summary>
    public static IReadOnlyList<object> FindAll(Type type)
    {
        var found = new List<object>();
        if (type == null)
        {
            return found;
        }

        try
        {
            var objects = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (var o in objects)
            {
                if (o != null)
                {
                    found.Add(o);
                }
            }
        }
        catch (Exception)
        {
        }

        return found;
    }

    /// <summary>
    /// The first component of the given type, or null.
    /// </summary>
    public static object? FindFirst(Type type) => FindAll(type).FirstOrDefault();

    /// <summary>
    /// The name of the prefab a scene component carries, or null.
    /// </summary>
    public static string? PrefabNameOf(object component, string fieldName)
    {
        var prefab = GameMembers.Get(component, fieldName);
        if (prefab == null)
        {
            return null;
        }

        var name = GameMembers.GetString(prefab, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// True when a prefab with the given name exists in the game's resources.
    /// </summary>
    public static bool PrefabExists(string name)
    {
        try
        {
            return Resources.Load(name) != null || Resources.Load("0_Items/" + name) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
