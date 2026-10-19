using System;
using PeakRestApi.Bridge;

namespace PeakRestApi.Networking;

/// <summary>
/// Answers the only networking question PEAKapi may act on: does this peer
/// have the authority to change the world for everybody else?
/// </summary>
/// <remarks>
/// PEAK uses Photon. The room host is the Photon master client. In a local
/// or offline session nobody is connected and the single player is the only
/// authority, so offline mode is treated as authoritative.
/// </remarks>
internal sealed class PhotonAuthority
{
    private readonly GameTypes types;

    public PhotonAuthority(GameTypes types)
    {
        this.types = types;
    }

    public bool PhotonAvailable => types.PhotonNetwork != null;

    /// <summary>
    /// True when the game is running without a Photon connection, in which case
    /// the local player is the only authority.
    /// </summary>
    public bool Offline
    {
        get
        {
            if (types.PhotonNetwork == null)
            {
                return false;
            }

            return ReadStaticBool("OfflineMode");
        }
    }

    /// <summary>
    /// True when the local peer is the room host.
    /// </summary>
    public bool IsHost
    {
        get
        {
            if (types.PhotonNetwork == null)
            {
                // Without Photon there is no multiplayer session at all, so the
                // local peer is the only possible authority.
                return true;
            }

            if (ReadStaticBool("IsMasterClient"))
            {
                return true;
            }

            return Offline;
        }
    }

    /// <summary>
    /// True when the local peer is connected to a room.
    /// </summary>
    public bool InRoom => types.PhotonNetwork != null && ReadStaticBool("InRoom");

    public bool IsConnected => types.PhotonNetwork != null && ReadStaticBool("IsConnected");

    private bool ReadStaticBool(string name)
    {
        var value = GameMembers.GetStatic(types.PhotonNetwork, name);
        return value is bool b && b;
    }
}
