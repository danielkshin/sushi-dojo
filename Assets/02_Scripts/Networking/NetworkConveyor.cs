
using System;
using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;
using SushiDojo.Conveyor;
using SushiDojo.Ingredients;
using SushiDojo.Cards;
using SushiDojo.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

public class NetworkConveyor : NetworkBehaviour
{
    [SerializeField] private ConveyorBelt conveyor;
    [SerializeField] private NetworkMatchTimer matchTimer;
    // Fired locally when this player wins an ingredient.
    public event Action<IngredientData> OnIngredientAwarded;

    // Shared count for debugging successful claims.
    public NetworkVariable<int> TotalClaims = new(0);

    // Host-controlled slots: -1 = empty, >= 0 = ingredient,
    // <= -2 = claimed ingredient encoded as -index - 2 (plate remains).
    private NetworkList<int> slots = new();
    private NetworkVariable<int> beltStep = new(0);
    private bool slotsChanged;
    private bool claimPending;
    private IngredientPlacementZone placementZone;
    RecipeQueueView localQueue;
    IngredientHandView localHand;
    readonly Dictionary<ulong, int[]> playerHardness = new();
    readonly Dictionary<ulong, bool[]> playerMissing = new();
    readonly List<ulong> staleDemandIds = new();
    readonly Dictionary<IngredientData, int> demandHardness = new();
    readonly Dictionary<IngredientData, int> requiredScratch = new();
    readonly HashSet<IngredientData> demandMissing = new();
    int[] sendHardness = Array.Empty<int>();
    bool[] sendMissing = Array.Empty<bool>();
    int[] sentHardness = Array.Empty<int>();
    bool[] sentMissing = Array.Empty<bool>();
    int[] mergedHardness = Array.Empty<int>();
    bool[] mergedMissing = Array.Empty<bool>();
    bool demandSent;

    const int MaxThrownIngredients = IngredientHandView.MaxCards + RecipeData.IngredientSlotCount;

    public override void OnNetworkSpawn()
    {
        if (IsClient)
            EnsureLocalGameplayUI();

        placementZone = FindFirstObjectByType<IngredientPlacementZone>();
        if (placementZone != null)
            placementZone.IngredientsThrown += HandleLocalBoardThrown;

        if (conveyor == null)
        {
            Debug.LogError("Conveyor reference is missing!");
            return;
        }

        if (matchTimer != null)
        {
            matchTimer.OnMatchSettling += StopConveyorForMatch;
            matchTimer.OnMatchEnded += StopConveyorForMatch;
            if (matchTimer.IsMatchSettling || matchTimer.HasMatchEnded)
                StopConveyorForMatch();
        }

        // Both host and client can request ingredient claims.
        conveyor.OnCenterClicked += RequestTakeIngredient;

        if (IsServer)
        {
            int count = conveyor.VisibleSlotCount + 2;

            for (int i = 0; i < count; i++)
                slots.Add(-1);

            Debug.Log("Host initialized the conveyor!");
        }
        else
        {
            slots.OnListChanged += OnSlotsChanged;
            beltStep.OnValueChanged += OnBeltStepChanged;
            slotsChanged = true;

            Debug.Log("Client waiting for conveyor updates.");
        }
    }

    private void EnsureLocalGameplayUI()
    {
        // Virtual players can start with a scene snapshot that predates the UI.
        // Create the local HUD before claims can deliver ingredient awards.
        if (FindFirstObjectByType<IngredientHandView>() != null &&
            FindFirstObjectByType<RecipeQueueView>() != null)
            return;

        GameObject prefab = Resources.Load<GameObject>("GameplayUI");
        if (prefab == null)
        {
            Debug.LogError("Missing Resources/GameplayUI prefab.", this);
            return;
        }

        IngredientHandView existingHand = FindFirstObjectByType<IngredientHandView>();
        RecipeQueueView existingRecipes = FindFirstObjectByType<RecipeQueueView>();
        GameObject ui = Instantiate(prefab);
        ui.name = "Local Gameplay UI";
        if (existingHand != null)
        {
            IngredientHandView duplicate = ui.GetComponentInChildren<IngredientHandView>();
            duplicate.gameObject.SetActive(false);
            Destroy(duplicate.gameObject);
        }
        if (existingRecipes != null)
        {
            RecipeQueueView duplicate = ui.GetComponentInChildren<RecipeQueueView>();
            duplicate.gameObject.SetActive(false);
            Destroy(duplicate.gameObject);
        }

        if (EventSystem.current == null)
        {
            GameObject events = new GameObject("Local EventSystem", typeof(EventSystem));
            events.transform.SetParent(ui.transform, false);
            events.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }
        Debug.Log("Created missing local gameplay UI for this player.", this);
    }

    public override void OnNetworkDespawn()
{
    claimPending = false;
    slots.OnListChanged -= OnSlotsChanged;
    if (placementZone != null)
        placementZone.IngredientsThrown -= HandleLocalBoardThrown;
    beltStep.OnValueChanged -= OnBeltStepChanged;

    if (conveyor != null)
    {
        conveyor.OnCenterClicked -= RequestTakeIngredient;
    }

    if (matchTimer != null)
    {
        matchTimer.OnMatchSettling -= StopConveyorForMatch;
        matchTimer.OnMatchEnded -= StopConveyorForMatch;
    }
}

    private void OnSlotsChanged(NetworkListEvent<int> change)
    {
        slotsChanged = true;
    }

    void HandleLocalBoardThrown(IReadOnlyList<IngredientData> thrown)
    {
        if (!IsSpawned || !IsClient || conveyor == null || thrown == null || thrown.Count == 0)
            return;
        if (matchTimer != null && !matchTimer.IsMatchRunning && !matchTimer.IsMatchSettling)
            return;

        var indexes = new List<int>(thrown.Count);
        foreach (IngredientData data in thrown)
        {
            int index = conveyor.GetIngredientIndex(data);
            if (index >= 0)
                indexes.Add(index);
        }

        if (indexes.Count == 0)
            return;

        ThrowBoardRpc(indexes.ToArray());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ThrowBoardRpc(int[] ingredientIndexes, RpcParams rpcParams = default)
    {
        if (!IsServer || conveyor == null || ingredientIndexes == null)
            return;
        if (matchTimer != null && !matchTimer.IsMatchRunning && !matchTimer.IsMatchSettling)
            return;

        ulong sender = rpcParams.Receive.SenderClientId;
        if (!NetworkManager.ConnectedClients.ContainsKey(sender))
            return;

        var valid = new List<int>(ingredientIndexes.Length);
        foreach (int index in ingredientIndexes)
        {
            if (valid.Count >= MaxThrownIngredients)
                break;
            if (conveyor.GetIngredientByIndex(index) == null)
                continue;
            valid.Add(index);
        }

        if (valid.Count == 0)
            return;

        int[] payload = valid.ToArray();
        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
        {
            if (clientId == sender)
                continue;
            ShowOpponentSplatRpc(payload, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ShowOpponentSplatRpc(int[] ingredientIndexes, RpcParams rpcParams = default)
    {
        if (conveyor == null || ingredientIndexes == null)
            return;

        var sprites = new List<Sprite>(ingredientIndexes.Length);
        foreach (int index in ingredientIndexes)
        {
            IngredientData data = conveyor.GetIngredientByIndex(index);
            if (data != null && data.Sprite != null)
                sprites.Add(data.Sprite);
        }

        BoardSplatOverlay.Play(sprites);
    }

    private void OnBeltStepChanged(int previous, int current)
    {
        slotsChanged = true;
    }

    public void StopLocalPresentation() => StopConveyorForMatch();

    private void StopConveyorForMatch()
{
    if (conveyor != null)
    {
        conveyor.FreezeBelt();
        Debug.Log("Conveyor stopped: Match over!");
    }
}
    private void Update()
    {
        if (!IsSpawned || conveyor == null)
            return;
        if (matchTimer != null && matchTimer.HasMatchEnded)
            return;
        if (IsClient)
            PublishLocalDemand();
        if (IsServer)
            PruneDemands();
        if (IsServer)
        {
            // Host determines which ingredients occupy each slot.
            beltStep.Value = conveyor.StepCount;
            for (int i = 0; i < slots.Count; i++)
            {
                int ingredient =
                    conveyor.GetSlotState(i);

                if (slots[i] != ingredient)
                    slots[i] = ingredient;
            }
        }
        else if (slotsChanged)
        {
            if (slots.Count != conveyor.VisibleSlotCount + 2)
                return;

            slotsChanged = false;

            int[] snapshot = new int[slots.Count];

            for (int i = 0; i < slots.Count; i++)
                snapshot[i] = slots[i];

            conveyor.ApplyRemoteSlots(snapshot, beltStep.Value);
        }
    }

    void PublishLocalDemand()
    {
        int count = conveyor.IngredientCount;
        if (count == 0) return;
        if (sendHardness.Length != count)
        {
            sendHardness = new int[count];
            sendMissing = new bool[count];
            sentHardness = new int[count];
            sentMissing = new bool[count];
            demandSent = false;
        }

        if (localQueue == null) localQueue = FindFirstObjectByType<RecipeQueueView>();
        if (localHand == null) localHand = FindFirstObjectByType<IngredientHandView>();
        if (placementZone == null) placementZone = FindFirstObjectByType<IngredientPlacementZone>();

        RecipeIngredientWeights.CollectPlayerDemand(
            localQueue, localHand, placementZone, demandHardness, demandMissing, requiredScratch);

        for (int i = 0; i < count; i++)
        {
            IngredientData data = conveyor.GetIngredientByIndex(i);
            sendHardness[i] = 0;
            sendMissing[i] = false;
            if (data == null) continue;
            if (demandHardness.TryGetValue(data, out int hardness))
                sendHardness[i] = hardness;
            sendMissing[i] = demandMissing.Contains(data);
        }

        if (demandSent && Same(sendHardness, sentHardness) && Same(sendMissing, sentMissing))
            return;

        Array.Copy(sendHardness, sentHardness, count);
        Array.Copy(sendMissing, sentMissing, count);
        demandSent = true;
        ReportSpawnDemandRpc(sendHardness, sendMissing);
    }

    static bool Same(int[] current, int[] previous)
    {
        for (int i = 0; i < current.Length; i++)
            if (current[i] != previous[i]) return false;
        return true;
    }

    static bool Same(bool[] current, bool[] previous)
    {
        for (int i = 0; i < current.Length; i++)
            if (current[i] != previous[i]) return false;
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportSpawnDemandRpc(int[] hardness, bool[] missing, RpcParams rpcParams = default)
    {
        if (!IsServer || conveyor == null || hardness == null || missing == null) return;
        int count = conveyor.IngredientCount;
        if (hardness.Length != count || missing.Length != count) return;

        ulong sender = rpcParams.Receive.SenderClientId;
        if (!NetworkManager.ConnectedClients.ContainsKey(sender)) return;
        for (int i = 0; i < count; i++)
        {
            if (hardness[i] < 0 || hardness[i] > RecipeData.IngredientSlotCount)
                return;
        }

        playerHardness[sender] = (int[])hardness.Clone();
        playerMissing[sender] = (bool[])missing.Clone();
        ApplySharedDemand();
    }

    void PruneDemands()
    {
        staleDemandIds.Clear();
        foreach (ulong playerId in playerHardness.Keys)
        {
            if (!NetworkManager.ConnectedClients.ContainsKey(playerId))
                staleDemandIds.Add(playerId);
        }

        if (staleDemandIds.Count == 0) return;
        foreach (ulong playerId in staleDemandIds)
        {
            playerHardness.Remove(playerId);
            playerMissing.Remove(playerId);
        }
        ApplySharedDemand();
    }

    void ApplySharedDemand()
    {
        int count = conveyor.IngredientCount;
        if (mergedHardness.Length != count)
        {
            mergedHardness = new int[count];
            mergedMissing = new bool[count];
        }

        Array.Clear(mergedHardness, 0, count);
        Array.Clear(mergedMissing, 0, count);
        foreach (KeyValuePair<ulong, int[]> pair in playerHardness)
        {
            int[] hardness = pair.Value;
            if (hardness == null || hardness.Length != count) continue;
            playerMissing.TryGetValue(pair.Key, out bool[] missing);
            for (int i = 0; i < count; i++)
            {
                if (hardness[i] > mergedHardness[i])
                    mergedHardness[i] = hardness[i];
                if (missing != null && missing.Length == count && missing[i])
                    mergedMissing[i] = true;
            }
        }

        conveyor.SetSharedRecipeDemand(mergedHardness, mergedMissing);
    }

    // Called when the local player clicks the revealed ingredient.
    public void RequestTakeIngredient()
    {
        if (SushiMultiplayer.Instance != null && SushiMultiplayer.Instance.ConnectionLost) return;
        if (matchTimer != null && !matchTimer.IsMatchRunning)
            return;
        if (!IsSpawned || !IsClient)
            return;

        IngredientHandView hand = FindFirstObjectByType<IngredientHandView>();
        if (claimPending || hand == null)
            return;

        if (hand.IsFull)
        {
            hand.PlayFullHandShake();
            return;
        }

        claimPending = true;
        TakeIngredientRpc();
    }

    // Claim validation always happens on the server.
    
[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
private void TakeIngredientRpc(RpcParams rpcParams = default)
{
    ulong playerId = rpcParams.Receive.SenderClientId;
    if (SushiMultiplayer.Instance != null && SushiMultiplayer.Instance.ConnectionLost)
    {
        RejectClaimRpc(RpcTarget.Single(playerId, RpcTargetUse.Temp));
        return;
    }
    if ((matchTimer != null && !matchTimer.IsMatchRunning) || conveyor == null)
    {
        RejectClaimRpc(RpcTarget.Single(playerId, RpcTargetUse.Temp));
        return;
    }

    if (conveyor.TryTakeCenter((int)playerId, out IngredientData ingredient))
    {
        int ingredientIndex =
            conveyor.GetIngredientIndex(ingredient);

        if (ingredientIndex < 0)
        {
            Debug.LogError("Claimed ingredient is not registered!");
            RejectClaimRpc(RpcTarget.Single(playerId, RpcTargetUse.Temp));
            return;
        }

        TotalClaims.Value++;

        Debug.Log($"Player {playerId} claimed {ingredient.name}");

        ConfirmIngredientRpc(
            ingredientIndex,
            RpcTarget.Single(playerId, RpcTargetUse.Temp)
        );
    }
    else
    {
        RejectClaimRpc(RpcTarget.Single(playerId, RpcTargetUse.Temp));
    }
}

    [Rpc(SendTo.SpecifiedInParams)]
    private void RejectClaimRpc(RpcParams rpcParams = default)
    {
        claimPending = false;
    }

    // Runs only on the player who successfully claimed the food.
    [Rpc(SendTo.SpecifiedInParams)]
    private void ConfirmIngredientRpc(
        int ingredientIndex,
        RpcParams rpcParams = default)
    {
        claimPending = false;
        if (conveyor == null)
            return;

        IngredientData ingredient =
            conveyor.GetIngredientByIndex(ingredientIndex);

        if (ingredient == null)
        {
            Debug.LogError(
                "Received an unknown ingredient!"
            );

            return;
        }

        Debug.Log(
            $"LOCAL PLAYER RECEIVED: {ingredient.name}"
        );

        if (OnIngredientAwarded == null)
            Debug.LogError("Ingredient awarded, but no local card hand is subscribed. Check that this player loaded SampleScene with its UI canvas.", this);
        OnIngredientAwarded?.Invoke(ingredient);
    }
}
