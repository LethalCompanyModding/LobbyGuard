using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using UnityEngine;

namespace LobbyGuard;

// The game checks its kick list against the SteamID a joining client writes in its request, which the client can
// change. Steam has already proven who's on the other end of the connection, so the kick list is checked against that.
internal static class Approval
{
    private delegate void Orig(GameNetworkManager self, NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response);

    private static Hook? hook;
    private static readonly Cooldowns cooldowns = new();
    private static StartOfRound? session;

    public static void Hook()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        hook = new Hook(typeof(GameNetworkManager).GetMethod(nameof(GameNetworkManager.ConnectionApproval), any),
            typeof(Approval).GetMethod(nameof(Approve), any));
    }

    // Records a player LobbyGuard just disconnected. A new lobby starts clean.
    internal static void Kicked(ulong steamId)
    {
        NewSession();
        double wait = cooldowns.Add(steamId, Time.realtimeSinceStartupAsDouble);
        Plugin.Log.LogInfo($"Steam user {steamId} can't rejoin for {wait} s");
    }

    private static void NewSession()
    {
        if (StartOfRound.Instance != null && StartOfRound.Instance != session)
        {
            session = StartOfRound.Instance;
            cooldowns.Clear();
        }
    }

    private static void Approve(Orig orig, GameNetworkManager self, NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        NetworkManager manager = NetworkManager.Singleton;
        ulong steamId = request.ClientNetworkId == manager.LocalClientId ? 0 : Plugin.SteamIdOf(manager, request.ClientNetworkId);
        if (steamId == 0 || StartOfRound.Instance == null)
        {
            orig(self, request, response);
            return;
        }
        NewSession();
        int wait = (int)System.Math.Ceiling(cooldowns.Remaining(steamId, Time.realtimeSinceStartupAsDouble));
        if (!StartOfRound.Instance.KickedClientIds.Contains(steamId) && wait == 0)
        {
            orig(self, request, response);
            return;
        }
        Plugin.Log.LogWarning(wait > 0 ? $"Turned away Steam user {steamId}: disconnected, {wait} s left" : $"Turned away Steam user {steamId}: kicked earlier");
        response.Reason = wait > 0 ? $"You were disconnected by the host. Try again in {wait} s." : "You cannot rejoin after being kicked.";
        response.CreatePlayerObject = false;
        response.Approved = false;
        response.Pending = false;
    }
}
