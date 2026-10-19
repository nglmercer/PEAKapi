using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using UnityEngine;

namespace PeakRestApi.Networking;

/// <summary>
/// Creates networked objects through Photon's prefab registry.
/// </summary>
/// <remarks>
/// The prefab names are the ones the game registers with Photon. PEAK's items
/// are registered below <c>0_Items/</c>; Photon adds that prefix itself for
/// the item helpers, so the plain item name is passed here.
/// </remarks>
internal sealed class PhotonSpawner
{
    private readonly GameTypes types;

    public PhotonSpawner(GameTypes types)
    {
        this.types = types;
    }

    public bool Available => types.PhotonNetwork != null;

    /// <summary>
    /// Instantiates a registered network prefab by its exact name.
    /// </summary>
    public object? Instantiate(string prefabName, Vector3 position, Quaternion rotation, out string? failure)
    {
        failure = null;
        if (types.PhotonNetwork == null)
        {
            failure = "Photon is not available in this game session.";
            return null;
        }

        var arguments = new object?[] { prefabName, position, rotation, (byte)0, Array.Empty<object>() };
        var result = GameMembers.Invoke(null, types.PhotonNetwork, "Instantiate", arguments, out var error);
        if (error != null)
        {
            failure = "Photon could not instantiate '" + prefabName + "': " + error.Message;
            return null;
        }

        if (result == null)
        {
            failure = "Photon returned no object for '" + prefabName + "'. The prefab is probably not registered or the session has no room.";
            return null;
        }

        return result;
    }

    /// <summary>
    /// Instantiates an item in the room. Photon prefixes the prefab with
    /// <c>0_Items/</c> itself.
    /// </summary>
    public object? InstantiateItemRoom(string itemName, Vector3 position, Quaternion rotation, out string? failure)
    {
        failure = null;
        if (types.PhotonNetwork == null)
        {
            failure = "Photon is not available in this game session.";
            return null;
        }

        var arguments = new object?[] { itemName, position, rotation };
        var result = GameMembers.Invoke(null, types.PhotonNetwork, "InstantiateItemRoom", arguments, out var error);
        if (error != null)
        {
            failure = "Photon could not instantiate the item '" + itemName + "': " + error.Message;
            return null;
        }

        if (result == null)
        {
            failure = "Photon returned no object for the item '" + itemName + "'. The prefab is probably not registered or the session has no room.";
            return null;
        }

        return result;
    }

    /// <summary>
    /// Destroys a networked object for everybody in the room.
    /// </summary>
    public static bool Destroy(object target)
    {
        try
        {
            if (target is GameObject go)
            {
                var view = go.GetComponent("PhotonView");
                if (view != null)
                {
                    var photonNetwork = FindPhotonNetwork(view.GetType());
                    if (photonNetwork != null)
                    {
                        GameMembers.Invoke(null, photonNetwork, "Destroy", new object?[] { view }, out _);
                        return true;
                    }
                }

                UnityEngine.Object.Destroy(go);
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// Calls a Photon RPC on a view, for example to hand a hazard over to the
    /// game's own AI.
    /// </summary>
    public static bool CallRpc(object view, string methodName, string targetName, params object?[] parameters)
    {
        try
        {
            var viewType = view.GetType();
            var rpcTargetType = viewType.Assembly.GetType("Photon.Pun.RpcTarget");
            if (rpcTargetType == null || !rpcTargetType.IsEnum)
            {
                return false;
            }

            var target = Enum.Parse(rpcTargetType, targetName);
            var arguments = new object?[] { methodName, target, parameters };

            GameMembers.Invoke(view, viewType, "RPC", arguments, out var error);
            return error == null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Type? FindPhotonNetwork(Type anyPhotonType)
    {
        var assembly = anyPhotonType.Assembly;
        var photonNetwork = assembly.GetType("Photon.Pun.PhotonNetwork", false);
        return photonNetwork;
    }
}
